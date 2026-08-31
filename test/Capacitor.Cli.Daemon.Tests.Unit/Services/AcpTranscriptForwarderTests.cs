using System.Threading.Channels;
using Capacitor.Cli.Core;
using Capacitor.Cli.Daemon.Services;
using Microsoft.Extensions.Logging.Abstractions;

namespace Capacitor.Cli.Daemon.Tests.Unit.Services;

/// <summary>
/// Option B task 3: exercises <see cref="AcpTranscriptForwarder"/>'s seq assignment, unacked
/// buffer, and ack state machine (gap-resend, terminal-drop, send-throw-then-recover) against a fake
/// send delegate and a fake transcript channel — no real <c>ServerConnection</c>/SignalR involved.
/// The ack rules under test are transcribed EXACTLY from
/// <c>Capacitor.Server.Sessions.CapacitorHub.AcpSessionEvents</c> (read read-only in the ai-686 server
/// worktree): a gap sets <see cref="AcpBatchAck.ExpectedNextSeq"/> and stops that batch immediately;
/// a terminal-drop reports <see cref="AcpBatchAck.AcceptedSeq"/> below the highest seq ever sent with
/// <see cref="AcpBatchAck.ExpectedNextSeq"/> left <see langword="null"/>; anything else is a normal
/// ack covering every envelope sent so far.
/// </summary>
public class AcpTranscriptForwarderTests {
    static readonly TimeSpan HangGuard = TimeSpan.FromSeconds(5);

    // Fast enough that the send-retry test doesn't burn real wall-clock time on the production 1s
    // backoff, but still exercises a genuine Task.Delay-based retry loop.
    static readonly TimeSpan FastRetryDelay = TimeSpan.FromMilliseconds(5);

    static AcpEventEnvelope InitialEnvelope =>
        new() { Kind = AcpEventKind.SessionStarted, RawSessionId = "sess-1" };

    static AcpEventEnvelope NewTextEnvelope(string text) =>
        new() { Kind = AcpEventKind.AssistantText, Text = text }; // Seq=0 placeholder, per task 2's contract

    static AcpEventEnvelope NewEphemeralEnvelope(string text) =>
        new() { Kind = AcpEventKind.AssistantText, Text = text, Ephemeral = true, ItemId = "item-1" };

    /// <summary>Acks the way the server actually does: it sequences CANONICAL envelopes only
    /// (<c>envelopes.Where(e =&gt; !e.Ephemeral)</c>), so AcceptedSeq can never reflect an ephemeral.
    /// A batch carrying no canonical envelope leaves the cursor exactly where it was.</summary>
    static Func<AcpEventEnvelope[], CancellationToken, Task<AcpBatchAck>> ServerAccurateSend(
            List<AcpEventEnvelope[]> observed) {
        long accepted = -1;

        return (batch, _) => {
            observed.Add(batch);
            foreach (var env in batch.Where(static e => !e.Ephemeral).OrderBy(static e => e.Seq))
                if (env.Seq == accepted + 1) accepted = env.Seq;

            return Task.FromResult(new AcpBatchAck(accepted, accepted));
        };
    }

    static Channel<AcpEventEnvelope> NewChannel() =>
        Channel.CreateUnbounded<AcpEventEnvelope>();

    static AcpTranscriptForwarder NewForwarder(
            Func<AcpEventEnvelope[], CancellationToken, Task<AcpBatchAck>> send,
            ChannelReader<AcpEventEnvelope>                                envelopes
        ) => new(send, InitialEnvelope, envelopes, NullLogger.Instance, FastRetryDelay, FastRetryDelay);

    // ── Seq assignment ───────────────────────────────────────────────────────────────────────────

    [Test]
    public async Task Initial_envelope_gets_seq_0_then_channel_envelopes_get_monotonic_seq() {
        var channel = NewChannel();
        channel.Writer.TryWrite(NewTextEnvelope("a"));
        channel.Writer.TryWrite(NewTextEnvelope("b"));
        channel.Writer.TryWrite(NewTextEnvelope("c"));
        channel.Writer.Complete();

        var sentSeqsInCallOrder = new List<long>();

        Task<AcpBatchAck> Send(AcpEventEnvelope[] batch, CancellationToken _) {
            sentSeqsInCallOrder.AddRange(batch.Select(e => e.Seq));
            return Task.FromResult(new AcpBatchAck(batch[^1].Seq, batch[^1].Seq));
        }

        var forwarder = NewForwarder(Send, channel.Reader);

        await forwarder.RunAsync(CancellationToken.None).WaitAsync(HangGuard);

        // Batching granularity isn't asserted (opportunistic draining may put 1..N envelopes in a
        // call) — only that every seq the forwarder ever sent, IN SEND ORDER, is exactly 0,1,2,3.
        await Assert.That(sentSeqsInCallOrder).IsEquivalentTo(new long[] { 0, 1, 2, 3 });
    }

    // ── §2.7 B4: resume/rebind seq initialization ─────────────────────────────────────────────────

    [Test]
    public async Task Resume_mode_suppresses_SessionStarted_and_numbers_new_events_from_resumeFromSeq_plus_one() {
        // A parked-reviewer rebind: initialEnvelope null + resumeFromSeq = the canonical AcceptedSeq. The
        // forwarder must send NO SessionStarted and number round-2's first new event at AcceptedSeq+1.
        // Pre-fix the forwarder ALWAYS started at seq 0, so seq 6/7 here would be <= AcceptedSeq(5) and the
        // server would silently dedup (LOSE) them — this assertion is what catches that.
        const long resumeFromSeq = 5;

        var channel = NewChannel();
        channel.Writer.TryWrite(NewTextEnvelope("a"));
        channel.Writer.TryWrite(NewTextEnvelope("b"));
        channel.Writer.Complete();

        var sent = new List<AcpEventEnvelope>();

        Task<AcpBatchAck> Send(AcpEventEnvelope[] batch, CancellationToken _) {
            sent.AddRange(batch);
            return Task.FromResult(new AcpBatchAck(batch[^1].Seq, batch[^1].Seq));
        }

        var forwarder = new AcpTranscriptForwarder(
            send: Send, initialEnvelope: null, envelopes: channel.Reader, logger: NullLogger.Instance,
            initialSendRetryDelay: FastRetryDelay, maxSendRetryDelay: FastRetryDelay, resumeFromSeq: resumeFromSeq);

        await forwarder.RunAsync(CancellationToken.None).WaitAsync(HangGuard);

        await Assert.That(sent[0].Seq).IsEqualTo(resumeFromSeq + 1);
        await Assert.That(sent.Select(e => e.Seq)).IsEquivalentTo(new[] { resumeFromSeq + 1, resumeFromSeq + 2 });
        // No SessionStarted, and nothing at seq 0, is ever sent on a resume.
        await Assert.That(sent.Any(e => e.Seq == 0)).IsFalse();
        await Assert.That(sent.Any(e => e.Kind == AcpEventKind.SessionStarted)).IsFalse();
        await Assert.That(forwarder.IsTerminal).IsFalse();
        await Assert.That(forwarder.UnackedCount).IsEqualTo(0);
    }

    [Test]
    public async Task Fresh_mode_still_sends_SessionStarted_at_seq_0_first() {
        // The discriminating companion: with resumeFromSeq null (default) and a non-null initialEnvelope,
        // today's behaviour is unchanged — SessionStarted rides seq 0 as the very first thing on the wire.
        var channel = NewChannel();
        channel.Writer.TryWrite(NewTextEnvelope("a"));
        channel.Writer.Complete();

        var sent = new List<AcpEventEnvelope>();

        Task<AcpBatchAck> Send(AcpEventEnvelope[] batch, CancellationToken _) {
            sent.AddRange(batch);
            return Task.FromResult(new AcpBatchAck(batch[^1].Seq, batch[^1].Seq));
        }

        var forwarder = NewForwarder(Send, channel.Reader); // resumeFromSeq null, initialEnvelope non-null

        await forwarder.RunAsync(CancellationToken.None).WaitAsync(HangGuard);

        await Assert.That(sent[0].Seq).IsEqualTo(0L);
        await Assert.That(sent[0].Kind).IsEqualTo(AcpEventKind.SessionStarted);
    }

    // ── Unacked buffer ───────────────────────────────────────────────────────────────────────────

    [Test]
    public async Task Normal_ack_drops_acked_entries_so_the_buffer_only_ever_holds_unacked_envelopes() {
        var channel = NewChannel();
        channel.Writer.TryWrite(NewTextEnvelope("a"));
        channel.Writer.TryWrite(NewTextEnvelope("b"));
        channel.Writer.Complete();

        var unackedCountAtEachSend = new List<int>();
        AcpTranscriptForwarder? forwarderRef = null;

        Task<AcpBatchAck> Send(AcpEventEnvelope[] batch, CancellationToken _) {
            unackedCountAtEachSend.Add(forwarderRef!.UnackedCount);
            return Task.FromResult(new AcpBatchAck(batch[^1].Seq, batch[^1].Seq));
        }

        var forwarder = NewForwarder(Send, channel.Reader);
        forwarderRef = forwarder;

        await forwarder.RunAsync(CancellationToken.None).WaitAsync(HangGuard);

        // Call 1 = the initial envelope alone (buffer holds exactly it: size 1).
        // Call 2 = the two channel envelopes batched together (buffer holds exactly THOSE: size 2,
        // not 3) — proving call 1's normal ack actually dropped the initial envelope from the buffer
        // rather than merely coinciding with an empty buffer at the very end.
        await Assert.That(unackedCountAtEachSend).IsEquivalentTo(new[] { 1, 2 });
        await Assert.That(forwarder.UnackedCount).IsEqualTo(0);
    }

    // ── Gap ──────────────────────────────────────────────────────────────────────────────────────

    [Test]
    public async Task Gap_ack_resends_from_ExpectedNextSeq_using_the_buffer() {
        var channel = NewChannel();
        channel.Writer.TryWrite(NewTextEnvelope("a")); // seq 1
        channel.Writer.TryWrite(NewTextEnvelope("b")); // seq 2
        channel.Writer.Complete();

        var callBatches = new List<long[]>();
        var callCount   = 0;

        Task<AcpBatchAck> Send(AcpEventEnvelope[] batch, CancellationToken _) {
            callCount++;
            callBatches.Add(batch.Select(e => e.Seq).ToArray());

            return callCount switch {
                1 => Task.FromResult(new AcpBatchAck(0, 0)),                    // initial envelope — normal ack
                2 => Task.FromResult(new AcpBatchAck(0, 0, ExpectedNextSeq: 1)), // gap — server never saw seq 1
                _ => Task.FromResult(new AcpBatchAck(batch[^1].Seq, batch[^1].Seq)), // resend — accepted fully
            };
        }

        var forwarder = NewForwarder(Send, channel.Reader);

        await forwarder.RunAsync(CancellationToken.None).WaitAsync(HangGuard);

        await Assert.That(callBatches[0]).IsEquivalentTo(new long[] { 0 });
        await Assert.That(callBatches[1]).IsEquivalentTo(new long[] { 1, 2 });
        // The gap resend uses the BUFFER (both seq 1 and 2 were still unacked) starting at
        // ExpectedNextSeq=1 — not a fresh drain of the (already-empty) channel.
        await Assert.That(callBatches[2]).IsEquivalentTo(new long[] { 1, 2 });
        await Assert.That(callCount).IsEqualTo(3);
        await Assert.That(forwarder.IsTerminal).IsFalse();
        await Assert.That(forwarder.UnackedCount).IsEqualTo(0);
    }

    // ── Terminal-drop ────────────────────────────────────────────────────────────────────────────

    [Test]
    public async Task Terminal_drop_ack_stops_the_loop_and_clears_the_buffer() {
        var channel = NewChannel();
        channel.Writer.TryWrite(NewTextEnvelope("a")); // seq 1
        channel.Writer.TryWrite(NewTextEnvelope("b")); // seq 2
        // Deliberately never completed: if the forwarder kept looping past the terminal-drop it
        // would block on WaitToReadAsync forever, and the WaitAsync(HangGuard) below would time out
        // and fail the test — that's the proof the loop actually stopped.

        var callCount = 0;

        Task<AcpBatchAck> Send(AcpEventEnvelope[] batch, CancellationToken _) {
            callCount++;

            // Call 1: initial envelope (seq 0) — normal ack. Call 2: the [seq1,seq2] batch — the
            // server reports AcceptedSeq=0 (unchanged — both were silently dropped by an
            // already-terminal binding) with ExpectedNextSeq null: AcceptedSeq(0) < highest-sent(2)
            // AND ExpectedNextSeq==null is exactly the terminal-drop signature (design spec §2.3).
            return Task.FromResult(new AcpBatchAck(0, 0));
        }

        var forwarder = NewForwarder(Send, channel.Reader);

        await forwarder.RunAsync(CancellationToken.None).WaitAsync(HangGuard);

        await Assert.That(forwarder.IsTerminal).IsTrue();
        await Assert.That(forwarder.UnackedCount).IsEqualTo(0);
        await Assert.That(callCount).IsEqualTo(2); // never retried/resent once terminal
    }

    // ── server rejection ack terminalizes the forwarder ────────────────────

    /// <summary>
    /// the server returns the canonical rejection ack for a stale/foreign/unbound binding.
    /// The forwarder must stop on the very first such ack — via the explicit <see cref="AcpBatchAck.Rejected"/>
    /// flag — without retrying.
    /// </summary>
    [Test]
    public async Task Rejected_ack_stops_the_loop_on_the_first_ack() {
        var channel = NewChannel();
        channel.Writer.TryWrite(NewTextEnvelope("a")); // seq 1 (never completed — a still-looping forwarder would hang)

        var callCount = 0;

        Task<AcpBatchAck> Send(AcpEventEnvelope[] batch, CancellationToken _) {
            callCount++;

            return Task.FromResult(new AcpBatchAck(-1, -1, ExpectedNextSeq: null, Rejected: true));
        }

        var forwarder = NewForwarder(Send, channel.Reader);

        await forwarder.RunAsync(CancellationToken.None).WaitAsync(HangGuard);

        await Assert.That(forwarder.IsTerminal).IsTrue();
        await Assert.That(forwarder.UnackedCount).IsEqualTo(0);
        await Assert.That(callCount).IsEqualTo(1); // stopped immediately, no retry
    }

    /// <summary>
    /// old-daemon compatibility: even an un-upgraded forwarder shape (one that ignores the new
    /// <see cref="AcpBatchAck.Rejected"/> flag) stops on the canonical rejection ack, because
    /// <see cref="AcpBatchAck.AcceptedSeq"/> == -1 is below any real highest-sent seq and trips the
    /// existing terminal-drop path.
    /// </summary>
    [Test]
    public async Task Rejection_ack_minus_one_stops_via_terminal_drop_even_ignoring_the_Rejected_flag() {
        var channel = NewChannel();
        channel.Writer.TryWrite(NewTextEnvelope("a")); // seq 1

        var callCount = 0;

        Task<AcpBatchAck> Send(AcpEventEnvelope[] batch, CancellationToken _) {
            callCount++;

            // Rejected deliberately left at its default false — proves the AcceptedSeq == -1 sentinel
            // alone drives the terminal-drop stop path.
            return Task.FromResult(new AcpBatchAck(-1, -1));
        }

        var forwarder = NewForwarder(Send, channel.Reader);

        await forwarder.RunAsync(CancellationToken.None).WaitAsync(HangGuard);

        await Assert.That(forwarder.IsTerminal).IsTrue();
        await Assert.That(callCount).IsEqualTo(1);
    }

    // ── Hot-loop guard: stalled gap → terminal ─────────────────────────────────────

    [Test]
    public async Task Stalled_gap_with_no_progress_stops_and_marks_terminal_after_the_cap() {
        var channel = NewChannel();
        channel.Writer.TryWrite(NewTextEnvelope("a")); // seq 1
        channel.Writer.TryWrite(NewTextEnvelope("b")); // seq 2
        // Deliberately never completed: without the guard the gap path would resend from the SAME
        // ExpectedNextSeq forever (a hot spin) and WaitAsync(HangGuard) below would time out — the
        // fact that RunAsync returns is the proof the guard stopped it.

        const int cap        = 3;
        var       gapResends = 0;

        Task<AcpBatchAck> Send(AcpEventEnvelope[] batch, CancellationToken _) {
            // Initial (seq 0) → normal ack. Every batch after that → the SAME gap (ExpectedNextSeq=1,
            // AcceptedSeq never advancing): the pathological already-terminal-binding signature that's
            // indistinguishable on the wire from a genuine gap.
            if (batch[0].Seq == 0)
                return Task.FromResult(new AcpBatchAck(0, 0));

            gapResends++;
            return Task.FromResult(new AcpBatchAck(0, 0, ExpectedNextSeq: 1));
        }

        var forwarder = new AcpTranscriptForwarder(
            Send, InitialEnvelope, channel.Reader, NullLogger.Instance,
            FastRetryDelay, FastRetryDelay, maxStalledGapResends: cap, stalledGapResendDelay: FastRetryDelay);

        await forwarder.RunAsync(CancellationToken.None).WaitAsync(HangGuard);

        await Assert.That(forwarder.IsTerminal).IsTrue();
        await Assert.That(forwarder.UnackedCount).IsEqualTo(0);
        // Bounded, not infinite: the guard stops after ~cap consecutive no-progress resends.
        await Assert.That(gapResends).IsLessThanOrEqualTo(cap + 1);
    }

    [Test]
    public async Task Gaps_that_make_progress_do_not_trip_the_stall_guard() {
        var channel = NewChannel();
        channel.Writer.TryWrite(NewTextEnvelope("a")); // seq 1
        channel.Writer.TryWrite(NewTextEnvelope("b")); // seq 2
        channel.Writer.TryWrite(NewTextEnvelope("c")); // seq 3
        channel.Writer.Complete();

        var callCount = 0;

        Task<AcpBatchAck> Send(AcpEventEnvelope[] batch, CancellationToken _) {
            callCount++;
            // A DIFFERENT (advancing) ExpectedNextSeq each round = genuine progress — the guard must
            // reset its no-progress counter and never trip, even though there are several gaps in a row.
            return callCount switch {
                1 => Task.FromResult(new AcpBatchAck(0, 0)),                     // initial seq 0 — normal
                2 => Task.FromResult(new AcpBatchAck(0, 0, ExpectedNextSeq: 1)), // gap, wants 1
                3 => Task.FromResult(new AcpBatchAck(1, 1, ExpectedNextSeq: 2)), // progress: accepted 1, wants 2
                4 => Task.FromResult(new AcpBatchAck(2, 2, ExpectedNextSeq: 3)), // progress: accepted 2, wants 3
                _ => Task.FromResult(new AcpBatchAck(batch[^1].Seq, batch[^1].Seq)), // accepted fully
            };
        }

        var forwarder = new AcpTranscriptForwarder(
            Send, InitialEnvelope, channel.Reader, NullLogger.Instance,
            FastRetryDelay, FastRetryDelay, maxStalledGapResends: 3, stalledGapResendDelay: FastRetryDelay);

        await forwarder.RunAsync(CancellationToken.None).WaitAsync(HangGuard);

        await Assert.That(forwarder.IsTerminal).IsFalse(); // advancing gaps never trip the guard
        await Assert.That(forwarder.UnackedCount).IsEqualTo(0);
    }

    // ── Send-throw-then-recover ──────────────────────────────────────────────────────────────────

    [Test]
    public async Task Send_throw_then_recover_retries_the_same_batch_without_skipping_or_duplicating_seq() {
        var channel = NewChannel();
        channel.Writer.TryWrite(NewTextEnvelope("a")); // seq 1
        channel.Writer.Complete();

        var seq1Attempts = 0;
        var allBatches   = new List<long[]>();

        Task<AcpBatchAck> Send(AcpEventEnvelope[] batch, CancellationToken _) {
            allBatches.Add(batch.Select(e => e.Seq).ToArray());

            if (batch[0].Seq == 1) {
                seq1Attempts++;
                if (seq1Attempts == 1)
                    throw new InvalidOperationException("simulated transport drop");
            }

            return Task.FromResult(new AcpBatchAck(batch[^1].Seq, batch[^1].Seq));
        }

        var forwarder = NewForwarder(Send, channel.Reader);

        await forwarder.RunAsync(CancellationToken.None).WaitAsync(HangGuard);

        // seq 1 was attempted exactly twice (throw, then success) — the SAME seq both times, never
        // skipped ahead to seq 2-that-doesn't-exist nor duplicated under a different seq value.
        await Assert.That(seq1Attempts).IsEqualTo(2);
        var seq1Calls = allBatches.Where(b => b.Contains(1L)).ToArray();
        await Assert.That(seq1Calls.Length).IsEqualTo(2);
        await Assert.That(seq1Calls[0]).IsEquivalentTo(new long[] { 1 });
        await Assert.That(seq1Calls[1]).IsEquivalentTo(new long[] { 1 });
        await Assert.That(forwarder.UnackedCount).IsEqualTo(0);
        await Assert.That(forwarder.IsTerminal).IsFalse();
    }

    // ── Completion / cancellation ────────────────────────────────────────────────────────────────

    [Test]
    public async Task Channel_complete_with_everything_acked_returns_from_RunAsync() {
        var channel = NewChannel();
        channel.Writer.TryWrite(NewTextEnvelope("a"));
        channel.Writer.Complete();

        static Task<AcpBatchAck> Send(AcpEventEnvelope[] batch, CancellationToken _) =>
            Task.FromResult(new AcpBatchAck(batch[^1].Seq, batch[^1].Seq));

        var forwarder = NewForwarder(Send, channel.Reader);

        // The real assertion is that this doesn't hang — WaitAsync throws TimeoutException if
        // RunAsync never returns.
        await forwarder.RunAsync(CancellationToken.None).WaitAsync(HangGuard);

        await Assert.That(forwarder.UnackedCount).IsEqualTo(0);
        await Assert.That(forwarder.IsTerminal).IsFalse();
    }

    [Test]
    public async Task Cancellation_returns_promptly_without_throwing() {
        var channel = NewChannel(); // never completed, never written to — RunAsync would hang on it
        using var cts = new CancellationTokenSource();

        static Task<AcpBatchAck> Send(AcpEventEnvelope[] batch, CancellationToken _) =>
            Task.FromResult(new AcpBatchAck(batch[^1].Seq, batch[^1].Seq));

        var forwarder = NewForwarder(Send, channel.Reader);

        var runTask = forwarder.RunAsync(cts.Token);
        cts.CancelAfter(TimeSpan.FromMilliseconds(50));

        // RunAsync swallows its own OperationCanceledException (mirrors AcpHostedAgentRuntime's
        // RunTurnWorkerAsync convention) — the task completes successfully, promptly, not hung.
        await runTask.WaitAsync(HangGuard);
    }

    // ── Ephemeral envelopes consume no canonical sequence number ───────────────────────

    /// <summary>Ephemeral envelopes ride the batch in arrival order but are NOT numbered: numbering
    /// them would both break canonical contiguity (the server reads a non-contiguous canonical seq as
    /// a gap) and inflate the high-water mark the ack is compared against.</summary>
    [Test]
    public async Task Ephemeral_envelopes_do_not_consume_a_canonical_seq() {
        var channel = NewChannel();
        channel.Writer.TryWrite(NewTextEnvelope("a"));       // canonical -> seq 1
        channel.Writer.TryWrite(NewEphemeralEnvelope("..")); // ephemeral -> unnumbered
        channel.Writer.TryWrite(NewEphemeralEnvelope("...."));// ephemeral -> unnumbered
        channel.Writer.TryWrite(NewTextEnvelope("b"));       // canonical -> seq 2 (contiguous)
        channel.Writer.Complete();

        var observed = new List<AcpEventEnvelope[]>();
        var forwarder = NewForwarder(ServerAccurateSend(observed), channel.Reader);

        await forwarder.RunAsync(CancellationToken.None).WaitAsync(HangGuard);

        var sent = observed.SelectMany(static b => b).ToArray();
        await Assert.That(sent.Where(static e => !e.Ephemeral).Select(static e => e.Seq))
            .IsEquivalentTo(new long[] { 0, 1, 2 }); // initial envelope + two contiguous canonicals
        await Assert.That(sent.Where(static e => e.Ephemeral).All(static e => e.Seq == 0)).IsTrue();
        // Ephemerals still travel, in arrival order, in the same batch.
        await Assert.That(sent.Count(static e => e.Ephemeral)).IsEqualTo(2);
    }

    /// <summary>Against a server that sequences canonical envelopes only, an ephemeral-heavy batch
    /// must not read as a terminal-drop: the high-water mark the ack is compared against counts
    /// canonical envelopes only, so AcceptedSeq can reach it. Every canonical envelope still lands.</summary>
    [Test]
    public async Task An_ack_counting_only_canonical_envelopes_is_not_a_terminal_drop() {
        var channel = NewChannel();
        channel.Writer.TryWrite(NewTextEnvelope("a"));         // canonical -> seq 1
        channel.Writer.TryWrite(NewEphemeralEnvelope("x"));    // ephemeral
        channel.Writer.TryWrite(NewEphemeralEnvelope("xy"));   // ephemeral
        channel.Writer.TryWrite(NewEphemeralEnvelope("xyz"));  // ephemeral
        channel.Writer.Complete();

        var observed = new List<AcpEventEnvelope[]>();
        var forwarder = NewForwarder(ServerAccurateSend(observed), channel.Reader);

        await forwarder.RunAsync(CancellationToken.None).WaitAsync(HangGuard);

        await Assert.That(forwarder.IsTerminal).IsFalse();
        await Assert.That(forwarder.UnackedCount).IsEqualTo(0);
        // Every canonical envelope reached the server and was acked.
        await Assert.That(observed.SelectMany(static b => b).Count(static e => !e.Ephemeral)).IsEqualTo(2);
    }

    /// <summary>An ALL-ephemeral batch leaves the canonical cursor untouched, so its ack (AcceptedSeq
    /// unchanged) reads as a normal ack rather than a terminal-drop, and a later canonical envelope is
    /// still forwarded at the next seq. The canonical envelope is deliberately written only AFTER the
    /// ephemeral-only batch has been observed: writing both up front lets the opportunistic drain
    /// coalesce them into one MIXED batch, which would exercise the mixed case and leave an
    /// all-ephemeral regression undetected.</summary>
    [Test]
    public async Task An_all_ephemeral_batch_leaves_the_cursor_untouched_and_does_not_stop_the_loop() {
        var channel = NewChannel();
        channel.Writer.TryWrite(NewEphemeralEnvelope("x"));
        channel.Writer.TryWrite(NewEphemeralEnvelope("xy"));

        var observed        = new List<AcpEventEnvelope[]>();
        var inner           = ServerAccurateSend(observed);
        var ephemeralOnly   = new TaskCompletionSource();

        Task<AcpBatchAck> Send(AcpEventEnvelope[] batch, CancellationToken ct) {
            var ack = inner(batch, ct);
            // Signal (never await) from inside the delegate: the forwarder is single-in-flight, so by
            // the time this batch is acked the next drain is what will pick up the canonical below.
            if (batch.Length > 0 && batch.All(static e => e.Ephemeral)) ephemeralOnly.TrySetResult();

            return ack;
        }

        var forwarder = NewForwarder(Send, channel.Reader);
        var run       = forwarder.RunAsync(CancellationToken.None);

        await ephemeralOnly.Task.WaitAsync(HangGuard);
        channel.Writer.TryWrite(NewTextEnvelope("after"));
        channel.Writer.Complete();

        await run.WaitAsync(HangGuard);

        await Assert.That(forwarder.IsTerminal).IsFalse();

        // The batch that carried only ephemerals really was ephemeral-only...
        var ephemeralOnlyBatches = observed.Where(static b => b.Length > 0 && b.All(static e => e.Ephemeral)).ToArray();
        await Assert.That(ephemeralOnlyBatches.Length).IsEqualTo(1);
        await Assert.That(ephemeralOnlyBatches[0].Length).IsEqualTo(2);

        // ...and it consumed no sequence number: the later canonical still lands at seq 1.
        var canonicalAfterInitial = observed.SelectMany(static b => b)
            .Where(static e => !e.Ephemeral && e.Seq > 0).ToArray();
        await Assert.That(canonicalAfterInitial.Length).IsEqualTo(1);
        await Assert.That(canonicalAfterInitial[0].Seq).IsEqualTo(1L);
    }

    /// <summary>Excluding ephemerals from the unacked buffer must not break the gap-resend path: a
    /// server-reported gap replays from the buffer, and what comes back is CANONICAL ONLY (ephemerals
    /// are fire-and-forget and are never retained for resend). Models the server's gap rule directly —
    /// report ExpectedNextSeq once, then accept — so the resend is exercised end to end rather than
    /// asserted about.</summary>
    [Test]
    public async Task A_gap_resend_replays_canonical_envelopes_only() {
        var channel = NewChannel();
        channel.Writer.TryWrite(NewTextEnvelope("a"));      // canonical -> seq 1
        channel.Writer.TryWrite(NewEphemeralEnvelope("x")); // ephemeral -> unnumbered, not retained
        channel.Writer.TryWrite(NewTextEnvelope("b"));      // canonical -> seq 2
        channel.Writer.Complete();

        var observed     = new List<AcpEventEnvelope[]>();
        var gapReported  = false;

        Task<AcpBatchAck> Send(AcpEventEnvelope[] batch, CancellationToken _) {
            observed.Add(batch);

            // Batch 1 is the initial envelope (seq 0) — accept it. On the first batch carrying
            // canonical seq 1, report a gap at 1 exactly once, forcing a resend from the buffer.
            if (!gapReported && batch.Any(static e => !e.Ephemeral && e.Seq == 1)) {
                gapReported = true;

                return Task.FromResult(new AcpBatchAck(0, 0, ExpectedNextSeq: 1));
            }

            var highestCanonical = batch.Where(static e => !e.Ephemeral)
                .Select(static e => e.Seq).DefaultIfEmpty(0L).Max();

            return Task.FromResult(new AcpBatchAck(highestCanonical, highestCanonical));
        }

        var forwarder = NewForwarder(Send, channel.Reader);

        await forwarder.RunAsync(CancellationToken.None).WaitAsync(HangGuard);

        await Assert.That(gapReported).IsTrue();          // the gap path really was taken
        await Assert.That(forwarder.IsTerminal).IsFalse(); // and it recovered rather than stopping
        await Assert.That(forwarder.UnackedCount).IsEqualTo(0);

        // The resend (the batch after the gap ack) replayed the buffer: canonical only, from seq 1.
        var resend = observed[^1];
        await Assert.That(resend.All(static e => !e.Ephemeral)).IsTrue();
        await Assert.That(resend.Select(static e => e.Seq)).IsEquivalentTo(new long[] { 1, 2 });
    }
}
