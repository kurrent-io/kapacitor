using System.Text.Json;
using System.Threading.Channels;
using Capacitor.Cli.Core;
using Microsoft.Extensions.Logging;

namespace Capacitor.Cli.Daemon.Services;

internal enum SequencedKind { Launch, Stop }

internal readonly record struct SequencedItem(
    SequencedKind Kind, string Epoch, long Seq, string CommandId, string AgentId);

internal readonly record struct CommandOutcome(
    CommandOutcomeKind Kind, string? AgentId = null, string? SessionId = null, CommandRejectedReason? RejectReason = null);

/// <summary>
/// Phase B2-b (sequenced-settlement design §4.2.2; parent §5.5): the daemon's two-lane sequenced
/// command handler. Exactly two command types are sequenced (Seq'd LaunchAgentCommand + StopAgentV2),
/// executed strictly serially per epoch. Acceptance (bump HighestAcceptedSeq + cache entry + enqueue)
/// is one atomic operation under <c>_lock</c>; LastProcessedSeq is the contiguous terminal prefix
/// (advances only on a terminal outcome). Self-contained + delegate-injected so it is unit-testable
/// with no live orchestrator (mirrors OrphanReaper/AgentKillQuarantine).
/// </summary>
internal sealed class SequencedCommandProcessor : IAsyncDisposable {
    sealed class CacheEntry { public required string CommandId; public bool Processed; public CommandOutcome Outcome; }
    readonly record struct LaneItem(SequencedItem Item, Func<Task<CommandOutcome>> Execute, TaskCompletionSource Done);

    readonly string _epoch;
    readonly Func<string, AgentLiveness> _readLiveness;
    readonly Func<CommandAck, Task> _sendAck;
    readonly Func<CommandRejected, Task> _sendRejected;
    readonly ILogger _logger;
    readonly int _cacheBound;

    readonly object _lock = new();
    long _highestAcceptedSeq;
    long _lastProcessedSeq;
    long _lastAckedPrefix;
    readonly Dictionary<long, CacheEntry> _cache = new();
    readonly Channel<LaneItem> _lane = Channel.CreateUnbounded<LaneItem>(new UnboundedChannelOptions { SingleReader = true });
    readonly Task _laneTask;

    public SequencedCommandProcessor(
            string epoch, Func<string, AgentLiveness> readLiveness,
            Func<CommandAck, Task> sendAck, Func<CommandRejected, Task> sendRejected,
            ILogger logger, int cacheBound = 256) {
        _epoch = epoch; _readLiveness = readLiveness; _sendAck = sendAck; _sendRejected = sendRejected;
        _logger = logger; _cacheBound = cacheBound;
        _laneTask = Task.Run(RunLaneAsync);
    }

    public string Epoch => _epoch;
    public long HighestAcceptedSeq { get { lock (_lock) return _highestAcceptedSeq; } }
    public long LastProcessedSeq   { get { lock (_lock) return _lastProcessedSeq; } }

    public Task SubmitAsync(SequencedItem item, Func<Task<CommandOutcome>> execute) {
        CommandOutcome? replay;
        bool acceptedReplay;
        Task result;

        lock (_lock) {
            result = SubmitLocked(item, execute, out replay, out acceptedReplay);
        }

        // The in-progress duplicate answer. Sent outside the lock and contained for the same reasons as
        // the settled one: a synchronous transport throw escaped into the hub, a faulted task went
        // unobserved, and any synchronous work in the delegate ran inside this processor's critical
        // section — which is the section the whole change is trying to keep narrow.
        if (acceptedReplay)
            SendContained(() => _sendAck(new CommandAck(_epoch, item.Seq, item.CommandId, CommandAckState.Accepted)),
                item.Seq, "accepted ack");

        // _readLiveness is NEVER invoked under _lock (see BuildProcessedAck): the cached outcome is
        // captured above, the ack is built and sent here — through the same containment the lane's
        // proactive send uses, because this is the RECOVERY path (the server retransmitting a command
        // whose terminal ack it never received) and an escaping throw here would surface in the hub
        // handler while leaving the server's capacity slot held.
        if (replay is { } outcome) SendSettledAck(item, outcome);

        return result;
    }

    Task SubmitLocked(SequencedItem item, Func<Task<CommandOutcome>> execute,
            out CommandOutcome? replay, out bool acceptedReplay) {
        replay = null;
        acceptedReplay = false;

        if (!string.Equals(item.Epoch, _epoch, StringComparison.Ordinal))
            return RejectLocked(item, CommandRejectedReason.StaleEpoch);   // never touches THIS epoch's lane

        if (_cache.TryGetValue(item.Seq, out var existing))
            return HandleDuplicateLocked(item, existing, out replay, out acceptedReplay); // answered, never re-executed

        if (item.Seq != _highestAcceptedSeq + 1)
            return HandleNonNextLocked(item);

        if (_cache.Count >= _cacheBound)                                   // never evict unacked identity
            return RejectLocked(item, CommandRejectedReason.Backpressure); // reopens only via a validated AckProcessedPrefix

        // ACCEPT + lane-item, atomically under _lock.
        _highestAcceptedSeq = item.Seq;
        _cache[item.Seq] = new CacheEntry { CommandId = item.CommandId, Processed = false };
        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        if (!_lane.Writer.TryWrite(new LaneItem(item, execute, done))) {
            SynthesizeErrorLocked(item); // shutdown/allocation race: watermark must still advance
            done.SetResult();
        }
        return done.Task;
    }

    /// <summary>Phase B2-b (sequenced-settlement design): an exact-duplicate <c>(Epoch, Seq, CommandId)</c>
    /// is ANSWERED, never re-executed — <c>Accepted</c> while still processing, or <c>Processed</c> with the
    /// cached outcome. A DIFFERENT CommandId at an already-accepted Seq is a protocol-invariant violation →
    /// <c>duplicate_collision</c>. Called under <c>_lock</c>; the processed arm only CAPTURES the cached
    /// outcome, leaving the ack for <see cref="SubmitAsync"/> to build outside the lock.</summary>
    Task HandleDuplicateLocked(SequencedItem item, CacheEntry existing,
            out CommandOutcome? replay, out bool acceptedReplay) {
        replay = null;
        acceptedReplay = false;

        if (!string.Equals(existing.CommandId, item.CommandId, StringComparison.Ordinal)) {
            // A DIFFERENT command claiming an accepted Seq — protocol invariant violation.
            SendRejectedContained(new CommandRejected(item.Epoch, item.Seq, item.CommandId, CommandRejectedReason.DuplicateCollision, item.AgentId));
            return Task.CompletedTask;
        }

        // Both arms only CAPTURE what to answer with; SubmitAsync sends after releasing the lock.
        if (!existing.Processed) acceptedReplay = true;
        else replay = existing.Outcome;

        return Task.CompletedTask;
    }

    /// <summary>The terminal <c>Processed</c> ack for a settled command — the ONE builder shared by the
    /// duplicate-replay path and the proactive settle ack at the end of <see cref="RunLaneAsync"/>, so a
    /// retransmission can never disagree with the proactive send about outcome/agent/session/rejection-reason.
    /// The CACHED rejection reason rides along so a rejected launch stays distinguishable as daemon_capacity
    /// (requeue) vs semantic (fail) — the exact lost-rejection case the identity cache answers.
    ///
    /// <para>MUST NOT be called under <c>_lock</c>. <c>CurrentState</c> is read LIVE at ack time through the
    /// readLiveness delegate, which in production walks the orchestrator's lifecycle collections; every
    /// settled command reaches this, so holding <c>_lock</c> across it would put that read on the critical
    /// path of concurrent <see cref="SubmitAsync"/>/<see cref="AckPrefix"/> callers. Both callers record the
    /// outcome in the cache first and build the ack after releasing the lock, which preserves the ordering
    /// that matters: an ack can never advertise an outcome the cache has not recorded.</para></summary>
    CommandAck BuildProcessedAck(SequencedItem item, CommandOutcome outcome) {
        var live   = _readLiveness(outcome.AgentId ?? item.AgentId);
        var reason = outcome.RejectReason is { } r ? RejectReasonWireToken(r) : null;

        return new CommandAck(_epoch, item.Seq, item.CommandId, CommandAckState.Processed,
            outcome.Kind, live, outcome.AgentId ?? item.AgentId, outcome.SessionId, reason);
    }

    /// <summary>Build AND fire a settled command's terminal ack without ever letting it fault the caller.
    /// The ack is best-effort telemetry — a disconnected/reconnecting server just falls back to the
    /// periodic status-report reconcile — so a synchronous throw AND a faulted task are both swallowed
    /// at Debug. Never awaited: neither the lane nor a hub callback may block on the wire.
    ///
    /// <para>The BUILD is deliberately inside the try. Passing a pre-built ack (
    /// <c>Send(Build(..))</c>) evaluates the argument before entering the containment, so a throwing
    /// <c>_readLiveness</c> escapes: from the lane it faults <see cref="RunLaneAsync"/> and leaves the
    /// item's <c>Done</c> unresolved (hanging that submitter and stopping every later command), and from
    /// <see cref="SubmitAsync"/> it escapes into the hub handler. Both callers reach this only AFTER the
    /// outcome is recorded in the cache, so swallowing the ack costs at most one status-report interval
    /// of slot latency — never a wrong or missing terminal fact.</para></summary>
    void SendSettledAck(SequencedItem item, CommandOutcome outcome) =>
        SendContained(() => _sendAck(BuildProcessedAck(item, outcome)), item.Seq, "settled ack");

    /// <summary>Phase B2-b (sequenced-settlement design §5.5): the wire token a cached
    /// <see cref="CommandRejectedReason"/> carries on a processed-duplicate <see cref="CommandAck"/> —
    /// serialized through the SAME context the SignalR hub uses for <c>CommandRejected.Reason</c>, so a
    /// retransmitted duplicate reads <c>daemon_capacity</c>/<c>semantic</c> identically to the first
    /// rejection (the ack's <c>RejectionReason</c> is a STRING; the outcome's <c>RejectReason</c> is the
    /// enum). NEVER <c>.ToString()</c> — that emits the C# name, not the <c>[JsonStringEnumMemberName]</c>
    /// snake_case token.</summary>
    static string RejectReasonWireToken(CommandRejectedReason reason) =>
        JsonSerializer.Serialize(reason, CapacitorJsonContext.Default.CommandRejectedReason).Trim('"');

    /// <summary>Phase B2-b (sequenced-settlement design): a non-next Seq (a gap — Seq &gt; HighestAcceptedSeq+1,
    /// or a too-low already-retired Seq below the frontier) is NEVER accepted out of order. Emit wrong_next so
    /// the server's transport sequencer resyncs (nudge → observe → retransmit); accept path + watermark untouched.</summary>
    Task HandleNonNextLocked(SequencedItem item) => RejectLocked(item, CommandRejectedReason.WrongNext);

    Task RejectLocked(SequencedItem item, CommandRejectedReason reason) {
        SendRejectedContained(new CommandRejected(item.Epoch, item.Seq, item.CommandId, reason, item.AgentId));
        return Task.CompletedTask;
    }

    /// <summary>Every <c>CommandRejected</c> send goes through here. A rejection is a NOTIFICATION, never
    /// the settlement fact itself, so a transport failure must not gate settlement — a bare send that
    /// throws synchronously escapes into whatever is calling (the hub, or worse the serial lane, where it
    /// permanently kills the consumer and strands every queued command). The server reconciles from its
    /// periodic status report regardless.</summary>
    void SendRejectedContained(CommandRejected rejected) =>
        SendContained(() => _sendRejected(rejected), rejected.Seq, "rejection");

    /// <summary>Shared containment for every best-effort wire send: a synchronous throw AND a faulted
    /// task are both swallowed at Debug, and the delegate is invoked INSIDE the try so argument
    /// construction is covered too. Never awaited — no wire call may block the lane or a hub callback.</summary>
    void SendContained(Func<Task> send, long seq, string what) {
        try {
            var sent = send();
            if (sent is { IsCompletedSuccessfully: false })
                _ = sent.ContinueWith(
                    t => _logger.LogDebug(t.Exception, "SequencedCommandProcessor: {What} for seq {Seq} failed to send", what, seq),
                    CancellationToken.None, TaskContinuationOptions.OnlyOnFaulted, TaskScheduler.Default);
        } catch (Exception ex) {
            _logger.LogDebug(ex, "SequencedCommandProcessor: {What} for seq {Seq} threw", what, seq);
        }
    }

    void SynthesizeErrorLocked(SequencedItem item) {
        // Lane-item creation failed AFTER acceptance (shutdown/allocation race) — an advertised-accepted
        // Seq with no processable item is impossible, so mark this Seq terminally errored and advance the
        // watermark THROUGH THE CONTIGUOUS PREFIX only. NEVER set _lastProcessedSeq = item.Seq directly:
        // if the lane is completing while an earlier accepted item is still draining, a direct jump to N
        // would (a) advertise a non-contiguous prefix and (b) be regressed below when the earlier item's
        // consumer later advances to N-1. AdvanceWatermarkLocked is monotonic + contiguous by construction.
        _cache[item.Seq] = new CacheEntry {
            CommandId = item.CommandId, Processed = true,
            Outcome = new CommandOutcome(CommandOutcomeKind.InternalError, item.AgentId) };
        AdvanceWatermarkLocked();
        SendRejectedContained(new CommandRejected(item.Epoch, item.Seq, item.CommandId, CommandRejectedReason.InternalError, item.AgentId));
    }

    /// <summary>The watermark is the contiguous terminal-processed prefix. Walk forward through Processed
    /// cache entries from _lastProcessedSeq+1 so a synthesized out-of-order terminal (a shutdown race)
    /// never advances past a still-draining earlier item, and no advance can ever regress the watermark
    /// (monotonic by construction). Retired seqs are always &lt;= _lastProcessedSeq, so the walk is safe.</summary>
    void AdvanceWatermarkLocked() {
        while (_cache.TryGetValue(_lastProcessedSeq + 1, out var next) && next.Processed)
            _lastProcessedSeq++;
    }

    /// <summary>Test seam: complete the lane writer so a subsequent accepted Submit's TryWrite fails,
    /// forcing the SynthesizeErrorLocked path deterministically (mirrors a shutdown race).</summary>
    internal void CompleteLaneForTest() => _lane.Writer.TryComplete();

    /// <summary>Test seam: whether the CALLING thread currently holds <c>_lock</c>. Lets a test's
    /// readLiveness delegate assert directly that <see cref="BuildProcessedAck"/> is never invoked
    /// under the lock, with no timing or thread choreography.</summary>
    internal bool LockHeldByCurrentThreadForTest => Monitor.IsEntered(_lock);

    async Task RunLaneAsync() {
        await foreach (var li in _lane.Reader.ReadAllAsync()) {
            // Nothing between here and Done may escape. The lane is the SINGLE serial consumer: one
            // escaping exception kills it permanently, leaving this command nonterminal, its submitter
            // waiting forever, and every queued command unrun — which the server reads as a
            // permanently held daemon capacity slot. try/finally guarantees the submitter is released
            // even if something unforeseen throws.
            try {
                CommandOutcome outcome;
                CommandRejectedReason? rejection = null;

                try {
                    outcome = await li.Execute();
                } catch (Exception ex) {
                    _logger.LogWarning(ex, "SequencedCommandProcessor: execution faulted for seq {Seq} — internal_error", li.Item.Seq);
                    outcome = new CommandOutcome(CommandOutcomeKind.InternalError, li.Item.AgentId);
                    rejection = CommandRejectedReason.InternalError;
                }

                // Task 15: an execution-time terminal rejection (daemon_capacity / semantic).
                if (outcome.Kind == CommandOutcomeKind.LaunchRejected && outcome.RejectReason is { } r)
                    rejection = r;

                lock (_lock) {
                    if (_cache.TryGetValue(li.Item.Seq, out var e)) { e.Processed = true; e.Outcome = outcome; }
                    AdvanceWatermarkLocked(); // contiguous terminal prefix — serial lane => normally == prior + 1,
                                              // but shared with SynthesizeErrorLocked so a race can never regress it
                }

                // Both notifications happen AFTER the outcome is recorded, and both are contained. They
                // used to run BEFORE it: a synchronous throw from _sendRejected then escaped the lane
                // with the command still marked nonterminal — the settlement fact lost to a failure in
                // merely announcing it. Relative order (rejection, then ack) is unchanged.
                if (rejection is { } reason)
                    SendRejectedContained(new CommandRejected(
                        li.Item.Epoch, li.Item.Seq, li.Item.CommandId, reason, li.Item.AgentId));

                // Settlement-admission design (§3.2 F): a FRESH terminal command is acked proactively, not
                // just a retransmitted duplicate. Without this the server only learns the command settled
                // when its next periodic status report reconciles (up to one report interval later), and
                // its one-nonterminal-command-per-daemon invariant rejects any concurrent launch for that
                // whole window. The server's ack handler is idempotent against replays and unknown/stale
                // acks, so older servers accept it too and simply retire the slot earlier.
                //
                // Built AFTER the cache entry is marked Processed (so the ack can never advertise an
                // outcome the cache has not recorded) but OUTSIDE the lock — see BuildProcessedAck.
                SendSettledAck(li.Item, outcome);
            } finally {
                li.Done.SetResult();
            }
        }
    }

    /// <summary>Phase B2-b (sequenced-settlement design): retire per-epoch identity-cache entries through a
    /// VALIDATED <c>AckProcessedPrefix</c> — current epoch, not over-ahead of the processed prefix, strictly
    /// monotonic. An unacked entry is NEVER evicted (that is the backpressure contract's other half). Called
    /// off the hub, so it takes <c>_lock</c> itself.</summary>
    public void AckPrefix(AckProcessedPrefix ack) {
        lock (_lock) {
            if (!string.Equals(ack.Epoch, _epoch, StringComparison.Ordinal)) return; // stale epoch — not our lane
            if (ack.UpToSeq > _lastProcessedSeq) return;                              // over-ahead of the processed prefix
            if (ack.UpToSeq <= _lastAckedPrefix) return;                              // regressing / duplicate ack
            _lastAckedPrefix = ack.UpToSeq;
            List<long>? toRemove = null;
            foreach (var seq in _cache.Keys)
                if (seq <= ack.UpToSeq) (toRemove ??= []).Add(seq);
            if (toRemove is not null)
                foreach (var seq in toRemove) _cache.Remove(seq);
        }
    }

    public async ValueTask DisposeAsync() {
        _lane.Writer.TryComplete();
        try { await _laneTask; } catch { /* best-effort */ }
    }
}
