using System.Threading.Channels;
using Capacitor.Cli.Core;
using Microsoft.Extensions.Logging;

namespace Capacitor.Cli.Daemon.Services;

/// <summary>
/// Pumps an ACP runtime's aggregated
/// <see cref="AcpEventEnvelope"/> transcript (<see cref="IAcpTranscriptSource.Envelopes"/>)
/// to the server via a <c>SendAcpEventsAsync</c>-shaped delegate, assigning the real monotonic
/// <see cref="AcpEventEnvelope.Seq"/> and driving the seq/ack state machine against the server's
/// EXACT <c>CapacitorHub.AcpSessionEvents</c>/<c>AcpSessionRegistry</c> contract (read read-only from
/// <c>Capacitor.Server.Sessions.CapacitorHub</c> in the server repo):
///
/// <list type="bullet">
/// <item>
/// the server processes a batch's envelopes in seq order; a seq already accepted (<c>≤ AcceptedSeq</c>)
/// is silently skipped (dedup); a seq more than one past <c>AcceptedSeq</c> makes the server return
/// immediately with <see cref="AcpBatchAck.ExpectedNextSeq"/> set to <c>AcceptedSeq + 1</c> and stop
/// processing the REST of that batch (a gap) — <b>resend from <see cref="AcpBatchAck.ExpectedNextSeq"/></b>;
/// </item>
/// <item>
/// once the binding is terminal (a <c>SessionEnded</c> already persisted, e.g. by
/// <c>CapacitorHub.EndAgentSession</c>), a new envelope at exactly <c>AcceptedSeq + 1</c> is dropped
/// WITHOUT advancing <c>AcceptedSeq</c> — so a batch that fully drains against a terminal binding
/// completes with <see cref="AcpBatchAck.ExpectedNextSeq"/> <see langword="null"/> and
/// <see cref="AcpBatchAck.AcceptedSeq"/> LOWER than the highest seq this forwarder has sent
/// (a "terminal-drop"): <b>stop forwarding and clear the unacked buffer</b> — there is no seq the
/// server will ever accept again for this binding;
/// </item>
/// <item>
/// otherwise (<see cref="AcpBatchAck.ExpectedNextSeq"/> <see langword="null"/> and
/// <see cref="AcpBatchAck.AcceptedSeq"/> caught up to the highest seq sent) it's a normal ack:
/// <b>drop every buffered envelope with <c>Seq ≤ AcceptedSeq</c></b>.
/// </item>
/// </list>
///
/// <b>Known edge case (documented, not fully closed):</b> the "terminal-drop"
/// signature above is unambiguous only when the FIRST envelope of a batch is the one silently
/// dropped by an ALREADY-terminal binding and the batch has exactly one envelope, OR the binding
/// turns terminal partway through the batch (e.g. this very batch's own <c>SessionEnded</c> mapping).
/// If a binding was ALREADY terminal before a MULTI-envelope batch starts, the server's per-envelope
/// loop drops the first (matching) envelope silently, then reports the SECOND envelope as a "gap"
/// (<see cref="AcpBatchAck.ExpectedNextSeq"/> set to the very seq it just dropped) — indistinguishable
/// on the wire from a genuine gap. Resending in that state would loop forever against a terminal
/// binding — so a <b>hot-loop guard</b> caps consecutive same-<c>ExpectedNextSeq</c>
/// resends that make no progress (with a small backoff) and, once the cap is hit, treats the binding
/// as terminal (stop + clear + <see cref="IsTerminal"/>) rather than spinning. A legitimate gap
/// advances its <c>ExpectedNextSeq</c> each round and so never trips the guard. Deeper reconnect
/// resilience beyond this remains.
///
/// Out of scope here: building/emitting the <c>SessionStarted</c> envelope (a fresh session passes it
/// in pre-built as <c>initialEnvelope</c>) or calling <c>AcpSessionStarted</c> — the bind
/// always precedes starting this forwarder. This forwarder also never emits a <c>session_ended</c>
/// envelope; the server's <c>EndAgentSession</c> is the sole <c>SessionEnded</c> owner.
///
/// <para>§2.7 B4 resume/rebind: a rebind passes null <c>initialEnvelope</c> and <c>resumeFromSeq</c> =
/// the source-claim's canonical <c>AcceptedSeq</c> — the forwarder sends NO SessionStarted and numbers the
/// first new event at <c>AcceptedSeq + 1</c>, so round-2 events aren't deduped (<c>≤ AcceptedSeq</c>) and lost.</para>
/// </summary>
internal sealed class AcpTranscriptForwarder {
    static readonly TimeSpan DefaultInitialSendRetryDelay = TimeSpan.FromSeconds(1);
    static readonly TimeSpan DefaultMaxSendRetryDelay     = TimeSpan.FromSeconds(30);
    static readonly TimeSpan DefaultStalledGapResendDelay = TimeSpan.FromSeconds(1);
    const int                DefaultMaxStalledGapResends  = 5;

    readonly Func<AcpEventEnvelope[], CancellationToken, Task<AcpBatchAck>> _send;
    readonly ChannelReader<AcpEventEnvelope>                                _envelopes;
    readonly ILogger                                                       _logger;
    readonly AcpEventEnvelope?                                             _initialEnvelope;
    readonly TimeSpan                                                      _initialSendRetryDelay;
    readonly TimeSpan                                                      _maxSendRetryDelay;
    readonly int                                                           _maxStalledGapResends;
    readonly TimeSpan                                                      _stalledGapResendDelay;

    /// <summary>
    /// Every sent-but-not-yet-acked envelope, keyed by seq, always in ascending order (a
    /// <see cref="SortedDictionary{TKey,TValue}"/> — the design spec calls this "the unacked
    /// buffer"). Entries are removed once <see cref="AcpBatchAck.AcceptedSeq"/> covers them, or the
    /// whole buffer is cleared on a terminal-drop.
    /// </summary>
    readonly SortedDictionary<long, AcpEventEnvelope> _unacked = new();

    long _nextSeq     = 1; // seq 0 is reserved for the initial envelope
    long _highestSent = -1;

    // Hot-loop guard: the "already-terminal binding reporting an indistinguishable
    // gap" edge (see this class's remarks) would otherwise make the gap path resend forever from the
    // same ExpectedNextSeq. Track consecutive same-point resends that made no progress; cap them.
    long? _lastResendFrom;
    int   _stalledGapResends;

    /// <summary>
    /// Set once a terminal-drop ack stops the loop. Lets a caller distinguish "the transcript
    /// channel completed normally" from "the server-side binding went terminal out from under this
    /// forwarder".
    /// </summary>
    public bool IsTerminal { get; private set; }

    /// <summary>Test-only visibility into the unacked buffer's current size.</summary>
    internal int UnackedCount => _unacked.Count;

    /// <summary>
    /// <paramref name="initialSendRetryDelay"/>/<paramref name="maxSendRetryDelay"/> default to 1s/30s
    /// (production) but are overridable so unit tests can exercise <see cref="SendWithRetryAsync"/>'s
    /// backoff without real sleeping.
    /// </summary>
    /// <param name="resumeFromSeq">
    /// §2.7 B4 resume/rebind: the canonical AcceptedSeq this forwarder resumes from. When set, do NOT
    /// (re)send SessionStarted and number the first new event at <paramref name="resumeFromSeq"/>+1, so a
    /// resumed incarnation's events land at the next canonical sequence instead of being deduped against
    /// the prior incarnation's cursor. Null = a fresh session (SessionStarted@0, today's behaviour).
    /// </param>
    public AcpTranscriptForwarder(
            Func<AcpEventEnvelope[], CancellationToken, Task<AcpBatchAck>> send,
            AcpEventEnvelope?                                              initialEnvelope,
            ChannelReader<AcpEventEnvelope>                                envelopes,
            ILogger                                                        logger,
            TimeSpan?                                                      initialSendRetryDelay = null,
            TimeSpan?                                                      maxSendRetryDelay = null,
            int?                                                           maxStalledGapResends = null,
            TimeSpan?                                                      stalledGapResendDelay = null,
            long?                                                          resumeFromSeq = null
        ) {
        _send                   = send;
        _envelopes              = envelopes;
        _logger                 = logger;
        if (resumeFromSeq is { } rs) {
            // Resume/rebind: canonical already holds events through rs. No SessionStarted (the source-claim
            // rebind is idempotent by thread id), and new events start at rs+1.
            _initialEnvelope = null;
            _nextSeq         = rs + 1;
            _highestSent     = rs;
        } else {
            _initialEnvelope = (initialEnvelope ?? throw new ArgumentNullException(nameof(initialEnvelope))) with { Seq = 0 };
        }
        _initialSendRetryDelay  = initialSendRetryDelay ?? DefaultInitialSendRetryDelay;
        _maxSendRetryDelay      = maxSendRetryDelay ?? DefaultMaxSendRetryDelay;
        _maxStalledGapResends   = maxStalledGapResends ?? DefaultMaxStalledGapResends;
        _stalledGapResendDelay  = stalledGapResendDelay ?? DefaultStalledGapResendDelay;
    }

    /// <summary>
    /// Runs the forward loop until the transcript channel completes and every envelope has been
    /// acked, a terminal-drop ack stops it early (<see cref="IsTerminal"/> flips true), or
    /// <paramref name="ct"/> is cancelled (returns promptly — no hang; a cancellation is swallowed
    /// here rather than propagated, mirroring <c>AcpHostedAgentRuntime.RunTurnWorkerAsync</c>'s
    /// "normal shutdown" convention so a fire-and-forget caller doesn't need its own try/catch).
    ///
    /// Single-in-flight: exactly one <c>send</c> call is outstanding at a time — a batch is sent,
    /// its ack is fully processed, and only then is the next batch decided (either a gap resend from
    /// the buffer, or a fresh opportunistic drain of whatever the channel has ready). This keeps the
    /// seq/ack bookkeeping trivially race-free without needing its own locking.
    /// </summary>
    public async Task RunAsync(CancellationToken ct) {
        try {
            // Resume mode (_initialEnvelope null) skips the initial-send branch entirely and drains new
            // envelopes straight from _nextSeq (== resumeFromSeq+1); a fresh session sends SessionStarted@0.
            var    pendingInitial = _initialEnvelope is not null;
            long?  resendFrom     = null;

            while (true) {
                ct.ThrowIfCancellationRequested();

                AcpEventEnvelope[] batch;

                if (resendFrom is { } from) {
                    batch = _unacked.Where(kv => kv.Key >= from).Select(kv => kv.Value).ToArray();

                    if (batch.Length == 0) {
                        // Defensive only — a gap's ExpectedNextSeq should always point at something
                        // still in the buffer (see this class's remarks); if it somehow doesn't,
                        // drop the stale cursor and fall through to draining fresh envelopes rather
                        // than spinning here.
                        resendFrom = null;
                        continue;
                    }
                } else if (pendingInitial) {
                    pendingInitial = false;
                    // Only reachable when pendingInitial started true, i.e. _initialEnvelope is non-null
                    // (fresh session); resume mode never sets pendingInitial and so never dereferences it.
                    var initial = _initialEnvelope!.Value;
                    _unacked[initial.Seq] = initial;
                    _highestSent = initial.Seq;
                    batch = [initial];
                } else {
                    var drained = await DrainNewEnvelopesAsync(ct).ConfigureAwait(false);

                    // Channel completed. The buffer is always empty here: we only ever reach this
                    // branch right after a normal ack removed everything ≤ AcceptedSeq, and a
                    // normal ack (by construction below) always covers every envelope sent so far.
                    if (drained is null)
                        return;

                    // Canonical-only bookkeeping. Ephemerals are fire-and-forget: not retained for
                    // resend, and not counted in the high-water mark the ack is compared against. An
                    // all-ephemeral batch therefore leaves _highestSent untouched, so the ack that
                    // follows cannot look like a terminal-drop.
                    foreach (var envelope in drained.Where(static e => !e.Ephemeral))
                        _unacked[envelope.Seq] = envelope;

                    for (var i = drained.Length - 1; i >= 0; i--) {
                        if (drained[i].Ephemeral) continue;

                        _highestSent = drained[i].Seq;
                        break;
                    }

                    batch = drained;
                }

                var ack = await SendWithRetryAsync(batch, ct).ConfigureAwait(false);

                // the server explicitly rejected this binding (stale/foreign/unbound). Stop
                // BEFORE interpreting the gap/cursor fields — the rejection ack deliberately carries a
                // -1 AcceptedSeq that would otherwise read as a terminal-drop anyway, but checking the
                // flag first keeps the intent explicit. An old server never sets this; that path still
                // stops via the AcceptedSeq < _highestSent terminal-drop below.
                if (ack.Rejected) {
                    LogRejectedBinding();
                    _unacked.Clear();
                    IsTerminal = true;

                    return;
                }

                if (ack.ExpectedNextSeq is { } expectedNextSeq) {
                    // A legitimate gap resolves on the first resend (the server wanted an earlier seq,
                    // then catches up — a NEW ExpectedNextSeq each round = progress). But the server's
                    // per-envelope loop makes an ALREADY-terminal binding report a dropped envelope as
                    // a gap indistinguishable from a real one (this class's "Known edge case"): the
                    // SAME ExpectedNextSeq comes back with no progress, and a bare `continue` here would
                    // hot-spin forever. Guard it: cap consecutive same-point resends (with a small
                    // backoff), then treat the binding as terminal — stop + clear rather than hammer.
                    if (expectedNextSeq == _lastResendFrom) {
                        if (++_stalledGapResends >= _maxStalledGapResends) {
                            LogGapStall(expectedNextSeq, _stalledGapResends);
                            _unacked.Clear();
                            IsTerminal = true;
                            return;
                        }

                        await Task.Delay(_stalledGapResendDelay, ct).ConfigureAwait(false);
                    } else {
                        _lastResendFrom    = expectedNextSeq;
                        _stalledGapResends = 0;
                    }

                    resendFrom = expectedNextSeq;
                    continue;
                }

                resendFrom         = null;
                _lastResendFrom    = null;
                _stalledGapResends = 0;

                if (ack.AcceptedSeq < _highestSent) {
                    LogTerminalDrop(ack.AcceptedSeq, _highestSent);
                    _unacked.Clear();
                    IsTerminal = true;
                    return;
                }

                foreach (var seq in _unacked.Keys.Where(seq => seq <= ack.AcceptedSeq).ToArray())
                    _unacked.Remove(seq);
            }
        } catch (OperationCanceledException) when (ct.IsCancellationRequested) {
            // normal shutdown — see this method's remarks.
        }
    }

    /// <summary>
    /// Blocks until at least one new envelope is available (or the channel completes), then drains
    /// everything immediately available too — "batch opportunistically" per the design spec — and
    /// stamps each CANONICAL envelope with the next monotonic seq. Returns <see langword="null"/> when
    /// the channel has completed with nothing left to read.
    ///
    /// <para>Ephemeral envelopes consume NO sequence number: they ride the same batch in arrival order,
    /// are excluded from the server's dup/gap logic, and never advance any cursor. Numbering them would
    /// inflate this side's <c>_highestSent</c> past the <c>AcceptedSeq</c> the server can ever return
    /// (it sequences canonical envelopes only), which the ack loop would read as a terminal-drop and
    /// stop forwarding for the rest of the session.</para>
    /// </summary>
    async Task<AcpEventEnvelope[]?> DrainNewEnvelopesAsync(CancellationToken ct) {
        if (!await _envelopes.WaitToReadAsync(ct).ConfigureAwait(false))
            return null;

        List<AcpEventEnvelope>? batch = null;

        while (_envelopes.TryRead(out var raw))
            (batch ??= []).Add(raw.Ephemeral ? raw : raw with { Seq = _nextSeq++ });

        return batch?.ToArray();
    }

    /// <summary>
    /// Sends one batch, retrying indefinitely (bounded backoff, capped at
    /// <see cref="_maxSendRetryDelay"/>) on ANY exception the send delegate throws that isn't driven
    /// by <paramref name="ct"/> itself. The send delegate is expected to already be
    /// <c>ConnectionRetry</c>/<c>IsReady</c>-gated (bound to <c>ServerConnection.SendAcpEventsAsync</c>
    /// — see this class's remarks), so most transient connection drops are already resolved inside
    /// it; this is a defensive outer layer for whatever still escapes (a non-transient
    /// <c>HubException</c>, or a drop precisely at the gate/invoke boundary) — the design spec calls
    /// this "send-retry ... after the connection is ready again". Retrying the SAME batch (never
    /// advancing the seq cursor on a failed send) is what keeps this safe: the server dedups by seq,
    /// so a resent already-persisted batch is a no-op there.
    /// </summary>
    async Task<AcpBatchAck> SendWithRetryAsync(AcpEventEnvelope[] batch, CancellationToken ct) {
        var delay = _initialSendRetryDelay;

        while (true) {
            try {
                return await _send(batch, ct).ConfigureAwait(false);
            } catch (OperationCanceledException) when (ct.IsCancellationRequested) {
                throw;
            } catch (Exception ex) {
                LogSendFailed(ex, batch[0].Seq, batch[^1].Seq, delay);

                await Task.Delay(delay, ct).ConfigureAwait(false);
                delay = TimeSpan.FromMilliseconds(Math.Min(delay.TotalMilliseconds * 2, _maxSendRetryDelay.TotalMilliseconds));
            }
        }
    }

    void LogGapStall(long expectedNextSeq, int attempts) =>
        _logger.LogWarning(
            "ACP transcript forwarder: gap ack stalled at ExpectedNextSeq={ExpectedNextSeq} for {Attempts} consecutive no-progress resends — treating the binding as terminal and stopping (likely an already-terminal binding reporting an indistinguishable gap; see AcpTranscriptForwarder remarks).",
            expectedNextSeq, attempts);

    void LogTerminalDrop(long acceptedSeq, long highestSent) =>
        _logger.LogWarning(
            "ACP transcript forwarder: terminal-drop ack (AcceptedSeq={AcceptedSeq} < highest-sent={HighestSent}) — the server-side binding is terminal; stopping.",
            acceptedSeq, highestSent);

    void LogRejectedBinding() =>
        _logger.LogInformation(
            "ACP transcript forwarder: server returned a rejection ack (stale/foreign/unbound binding) — stopping.");

    void LogSendFailed(Exception ex, long firstSeq, long lastSeq, TimeSpan delay) =>
        _logger.LogDebug(
            ex,
            "ACP transcript forwarder: send failed for seq [{FirstSeq}..{LastSeq}]; retrying in {DelayMs}ms.",
            firstSeq, lastSeq, delay.TotalMilliseconds);
}
