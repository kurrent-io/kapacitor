using System.Collections.Concurrent;
using Capacitor.Cli.Core;
using Capacitor.Cli.Daemon.Services;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Capacitor.Cli.Tests.Unit.Daemon;

public class SequencedCommandProcessorTests {
    sealed class Harness {
        public readonly List<CommandAck> Acks = [];
        public readonly List<CommandRejected> Rejects = [];
        public readonly ConcurrentQueue<long> ExecOrder = new();
        public SequencedCommandProcessor P(string epoch = "e1", int bound = 256) => new(
            epoch, _ => AgentLiveness.Live,
            a => { lock (Acks) Acks.Add(a); return Task.CompletedTask; },
            r => { lock (Rejects) Rejects.Add(r); return Task.CompletedTask; },
            NullLogger.Instance, bound);
        public SequencedItem Launch(long seq, string epoch = "e1", string id = "cmd", string agent = "a")
            => new(SequencedKind.Launch, epoch, seq, id + seq, agent + seq);
    }

    [Test] public async Task Exact_next_commands_execute_serially_and_advance_the_watermark() {
        var h = new Harness(); await using var p = h.P();
        await p.SubmitAsync(h.Launch(1), () => { h.ExecOrder.Enqueue(1); return Task.FromResult(new CommandOutcome(CommandOutcomeKind.LaunchExecuted)); });
        await p.SubmitAsync(h.Launch(2), () => { h.ExecOrder.Enqueue(2); return Task.FromResult(new CommandOutcome(CommandOutcomeKind.LaunchExecuted)); });
        await Assert.That(p.LastProcessedSeq).IsEqualTo(2L);
        await Assert.That(h.ExecOrder.ToArray()).IsEquivalentTo(new[] { 1L, 2L });
    }

    [Test] public async Task Out_of_order_command_is_not_accepted() {
        var h = new Harness(); await using var p = h.P();
        var ran = false;
        await p.SubmitAsync(h.Launch(2), () => { ran = true; return Task.FromResult(new CommandOutcome(CommandOutcomeKind.LaunchExecuted)); });
        await Assert.That(p.HighestAcceptedSeq).IsEqualTo(0L); // Seq 2 while next is 1 -> not accepted
        await Assert.That(ran).IsFalse();
    }

    [Test] public async Task Execute_fault_becomes_internal_error_and_still_advances_the_watermark() {
        var h = new Harness(); await using var p = h.P();
        await p.SubmitAsync(h.Launch(1), () => throw new InvalidOperationException("boom"));
        await Assert.That(p.LastProcessedSeq).IsEqualTo(1L);
        await Assert.That(h.Rejects.Single().Reason).IsEqualTo(CommandRejectedReason.InternalError);
    }

    [Test] public async Task Forced_item_creation_failure_synthesizes_a_terminal_item_and_advances_monotonically() {
        // Parent §8: forced item-creation failure AFTER counter reservation -> synthesized errored terminal
        // item at N, watermark advances. AND the monotonicity hazard: when the lane is completing while an
        // earlier accepted item is still draining, the synthesized advance must NOT jump past it (which the
        // draining item's later advance would then regress below).
        var h = new Harness(); await using var p = h.P();
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var gate    = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        // Item 1 accepted + enqueued; its execute BLOCKS mid-flight (still draining).
        var t1 = p.SubmitAsync(h.Launch(1),
            async () => { started.SetResult(); await gate.Task; return new CommandOutcome(CommandOutcomeKind.LaunchExecuted); });
        await started.Task;                 // item 1 is dequeued and executing

        // Complete the lane while item 1 drains, then submit item 2 -> TryWrite fails -> SynthesizeErrorLocked.
        p.CompleteLaneForTest();
        var t2 = p.SubmitAsync(h.Launch(2), () => Task.FromResult(new CommandOutcome(CommandOutcomeKind.LaunchExecuted)));
        await t2;                           // the synthesized terminal completes immediately
        var afterSynth = p.LastProcessedSeq;
        await Assert.That(afterSynth).IsEqualTo(0L);   // synth at N=2 did NOT skip past the still-draining N=1

        gate.SetResult();                   // item 1 drains; contiguous prefix now reaches 1 then 2
        await t1;
        await Assert.That(afterSynth).IsLessThanOrEqualTo(p.LastProcessedSeq); // monotonic — never regressed
        await Assert.That(p.LastProcessedSeq).IsEqualTo(2L);                    // contiguous prefix reaches 2
        await Assert.That(h.Rejects.Single().Reason).IsEqualTo(CommandRejectedReason.InternalError); // synth emitted the reject
    }

    // Settlement-admission design (§3.2 F): a FRESH terminal command is now acked proactively at the
    // end of the lane, so the server retires its one-nonterminal slot within milliseconds of execution
    // completing instead of waiting for the 60s status-report reconcile. Exactly one ack per settle.
    [Test] public async Task Fresh_terminal_command_emits_exactly_one_proactive_processed_ack() {
        var h = new Harness(); await using var p = h.P();
        await p.SubmitAsync(h.Launch(1), () => Task.FromResult(new CommandOutcome(CommandOutcomeKind.LaunchExecuted, "a", "sess")));

        var ack = h.Acks.Single();                                               // exactly one, no duplicate involved
        await Assert.That(ack.Epoch).IsEqualTo("e1");
        await Assert.That(ack.Seq).IsEqualTo(1L);
        await Assert.That(ack.CommandId).IsEqualTo("cmd1");
        await Assert.That(ack.State).IsEqualTo(CommandAckState.Processed);
        await Assert.That(ack.OutcomeKind).IsEqualTo(CommandOutcomeKind.LaunchExecuted);
        await Assert.That(ack.CurrentState).IsEqualTo(AgentLiveness.Live);
        await Assert.That(ack.AgentId).IsEqualTo("a");
        await Assert.That(ack.SessionId).IsEqualTo("sess");
        await Assert.That(ack.RejectionReason).IsNull();
    }

    // The proactive ack is best-effort telemetry to the server: a send failure (server gone /
    // reconnecting) must never fault the lane, block the watermark, or lose the Done completion.
    [Test] public async Task Proactive_ack_send_failure_does_not_fault_the_lane() {
        var acks = 0;
        await using var p = new SequencedCommandProcessor(
            "e1", _ => AgentLiveness.Live,
            _ => { acks++; throw new InvalidOperationException("server gone"); },
            _ => Task.CompletedTask, NullLogger.Instance);

        await p.SubmitAsync(new SequencedItem(SequencedKind.Launch, "e1", 1, "cmd1", "a1"),
            () => Task.FromResult(new CommandOutcome(CommandOutcomeKind.LaunchExecuted)));
        await Assert.That(p.LastProcessedSeq).IsEqualTo(1L);   // settled despite the throwing ack

        // The lane is still alive: a second command still executes and advances.
        await p.SubmitAsync(new SequencedItem(SequencedKind.Launch, "e1", 2, "cmd2", "a2"),
            () => Task.FromResult(new CommandOutcome(CommandOutcomeKind.LaunchExecuted)));
        await Assert.That(p.LastProcessedSeq).IsEqualTo(2L);
        await Assert.That(acks).IsEqualTo(2);
    }

    [Test] public async Task Duplicate_of_a_processed_command_is_acked_with_outcome_and_live_state_not_reexecuted() {
        var h = new Harness(); await using var p = h.P();
        var runs = 0;
        var item = h.Launch(1);
        await p.SubmitAsync(item, () => { runs++; return Task.FromResult(new CommandOutcome(CommandOutcomeKind.LaunchExecuted, "a", "sess")); });
        await p.SubmitAsync(item, () => { runs++; return Task.FromResult(new CommandOutcome(CommandOutcomeKind.LaunchExecuted)); });
        await Assert.That(runs).IsEqualTo(1);                                    // no re-execution

        // Two acks now: [0] the proactive settle ack from the lane, [1] the duplicate-replay ack.
        // Both are Processed and carry the SAME cached outcome — the duplicate path is unchanged.
        await Assert.That(h.Acks).HasCount().EqualTo(2);
        await Assert.That(h.Acks[0].State).IsEqualTo(CommandAckState.Processed);  // proactive
        var ack = h.Acks[1];                                                      // duplicate replay
        await Assert.That(ack.State).IsEqualTo(CommandAckState.Processed);
        await Assert.That(ack.OutcomeKind).IsEqualTo(CommandOutcomeKind.LaunchExecuted);
        await Assert.That(ack.CurrentState).IsEqualTo(AgentLiveness.Live);       // read live at ack time
    }

    [Test] public async Task Different_command_id_at_an_accepted_seq_is_a_duplicate_collision() {
        var h = new Harness(); await using var p = h.P();
        await p.SubmitAsync(h.Launch(1), () => Task.FromResult(new CommandOutcome(CommandOutcomeKind.LaunchExecuted)));
        // Same Seq, different CommandId:
        await p.SubmitAsync(new SequencedItem(SequencedKind.Launch, "e1", 1, "OTHER", "a1"),
            () => Task.FromResult(new CommandOutcome(CommandOutcomeKind.LaunchExecuted)));
        await Assert.That(h.Rejects.Single().Reason).IsEqualTo(CommandRejectedReason.DuplicateCollision);
    }

    [Test] public async Task Backpressure_rejects_when_the_cache_is_full_and_ack_prefix_reopens_capacity() {
        var h = new Harness(); await using var p = h.P(bound: 2);
        await p.SubmitAsync(h.Launch(1), () => Task.FromResult(new CommandOutcome(CommandOutcomeKind.LaunchExecuted)));
        await p.SubmitAsync(h.Launch(2), () => Task.FromResult(new CommandOutcome(CommandOutcomeKind.LaunchExecuted)));
        await p.SubmitAsync(h.Launch(3), () => Task.FromResult(new CommandOutcome(CommandOutcomeKind.LaunchExecuted)));
        await Assert.That(h.Rejects.Single().Reason).IsEqualTo(CommandRejectedReason.Backpressure);
        await Assert.That(p.HighestAcceptedSeq).IsEqualTo(2L);       // 3 not accepted (unacked identity kept)

        p.AckPrefix(new AckProcessedPrefix("e1", 2));                // retire <= 2
        await p.SubmitAsync(h.Launch(3), () => Task.FromResult(new CommandOutcome(CommandOutcomeKind.LaunchExecuted)));
        await Assert.That(p.LastProcessedSeq).IsEqualTo(3L);
    }

    [Test] public async Task AckPrefix_rejects_over_ahead_regressing_and_stale_epoch_without_eviction() {
        var h = new Harness(); await using var p = h.P();
        await p.SubmitAsync(h.Launch(1), () => Task.FromResult(new CommandOutcome(CommandOutcomeKind.LaunchExecuted)));
        p.AckPrefix(new AckProcessedPrefix("e1", 5));   // over-ahead (> LastProcessedSeq) -> ignored
        p.AckPrefix(new AckProcessedPrefix("WRONG", 1));// stale epoch -> ignored
        await Assert.That(h.Acks.Count).IsEqualTo(1);   // only the proactive settle ack so far
        // A duplicate is still answerable (identity not evicted) — that adds the SECOND ack:
        await p.SubmitAsync(h.Launch(1), () => Task.FromResult(new CommandOutcome(CommandOutcomeKind.LaunchExecuted)));
        await Assert.That(h.Acks.Count).IsEqualTo(2);
        await Assert.That(h.Acks[1].State).IsEqualTo(CommandAckState.Processed);
    }

    [Test] public async Task Non_next_future_seq_is_rejected_wrong_next_without_accepting() {
        var h = new Harness(); await using var p = h.P();
        await p.SubmitAsync(h.Launch(1), () => Task.FromResult(new CommandOutcome(CommandOutcomeKind.LaunchExecuted)));
        await p.SubmitAsync(h.Launch(3), () => Task.FromResult(new CommandOutcome(CommandOutcomeKind.LaunchExecuted))); // gap
        await Assert.That(h.Rejects.Single().Reason).IsEqualTo(CommandRejectedReason.WrongNext);
        await Assert.That(p.HighestAcceptedSeq).IsEqualTo(1L);
    }

    [Test] public async Task Execution_time_daemon_capacity_rejection_advances_watermark_and_emits_reject() {
        var h = new Harness(); await using var p = h.P();
        await p.SubmitAsync(h.Launch(1), () => Task.FromResult(
            new CommandOutcome(CommandOutcomeKind.LaunchRejected, "a", RejectReason: CommandRejectedReason.DaemonCapacity)));
        await Assert.That(p.LastProcessedSeq).IsEqualTo(1L);        // rejected-as-item is terminal
        await Assert.That(h.Rejects.Single().Reason).IsEqualTo(CommandRejectedReason.DaemonCapacity);
    }

    // Phase B2-b (sequenced-settlement design §5.5): a retransmitted duplicate of a LaunchRejected command
    // must carry the CACHED rejection reason on its processed CommandAck, as the SAME wire token
    // CommandRejected.Reason serializes to, so the server can tell daemon_capacity (requeue) from semantic
    // (fail) for exactly the lost-rejection case the identity cache exists to answer.
    [Test] public async Task Duplicate_of_a_capacity_rejected_launch_carries_the_daemon_capacity_wire_token() {
        var h = new Harness(); await using var p = h.P();
        var item = h.Launch(1);
        await p.SubmitAsync(item, () => Task.FromResult(
            new CommandOutcome(CommandOutcomeKind.LaunchRejected, "a", RejectReason: CommandRejectedReason.DaemonCapacity)));
        await p.SubmitAsync(item, () => Task.FromResult(new CommandOutcome(CommandOutcomeKind.LaunchExecuted)));

        // [0] is the proactive settle ack, [1] the duplicate replay — BOTH must carry the wire token,
        // since they are built from the same cached outcome by the one shared builder.
        await Assert.That(h.Acks).HasCount().EqualTo(2);
        await Assert.That(h.Acks[0].RejectionReason).IsEqualTo("daemon_capacity");   // proactive
        var ack = h.Acks[1];                                                          // duplicate replay
        await Assert.That(ack.State).IsEqualTo(CommandAckState.Processed);
        await Assert.That(ack.OutcomeKind).IsEqualTo(CommandOutcomeKind.LaunchRejected);
        await Assert.That(ack.RejectionReason).IsEqualTo("daemon_capacity");
    }

    [Test] public async Task Duplicate_of_a_semantically_rejected_launch_carries_the_semantic_wire_token() {
        var h = new Harness(); await using var p = h.P();
        var item = h.Launch(1);
        await p.SubmitAsync(item, () => Task.FromResult(
            new CommandOutcome(CommandOutcomeKind.LaunchRejected, "a", RejectReason: CommandRejectedReason.Semantic)));
        await p.SubmitAsync(item, () => Task.FromResult(new CommandOutcome(CommandOutcomeKind.LaunchExecuted)));
        await Assert.That(h.Acks).HasCount().EqualTo(2);                              // proactive + duplicate replay
        await Assert.That(h.Acks[0].RejectionReason).IsEqualTo("semantic");
        await Assert.That(h.Acks[1].RejectionReason).IsEqualTo("semantic");
    }

    // The liveness read is a delegate over the orchestrator's live lifecycle collections, and the proactive
    // settle ack puts it on EVERY settled command (not just a duplicate replay). Holding _lock across it
    // would push that read onto the critical path of concurrent SubmitAsync/AckPrefix callers, so both ack
    // paths must build the ack after releasing the lock.
    [Test] public async Task Liveness_is_never_read_while_the_processor_lock_is_held() {
        SequencedCommandProcessor? proc = null;
        var reads = 0;
        var readsUnderLock = 0;

        await using var p = proc = new SequencedCommandProcessor(
            "e1",
            _ => {
                reads++;
                if (proc!.LockHeldByCurrentThreadForTest) readsUnderLock++;
                return AgentLiveness.Live;
            },
            _ => Task.CompletedTask, _ => Task.CompletedTask, NullLogger.Instance);

        var item = new SequencedItem(SequencedKind.Launch, "e1", 1, "cmd1", "a1");
        await p.SubmitAsync(item, () => Task.FromResult(new CommandOutcome(CommandOutcomeKind.LaunchExecuted, "a1", "sess")));
        await p.SubmitAsync(item, () => Task.FromResult(new CommandOutcome(CommandOutcomeKind.LaunchExecuted)));

        await Assert.That(reads).IsEqualTo(2);          // proactive settle ack + duplicate replay ack
        await Assert.That(readsUnderLock).IsEqualTo(0); // neither one held _lock
    }

    // The ordering the lock move must NOT break: the cache records the outcome BEFORE the ack is built, so
    // an ack can never advertise an outcome a concurrently-arriving duplicate would not yet see.
    // NOT a regression net for the lock placement itself — it passes either way, since Monitor is
    // reentrant and LastProcessedSeq would observe the same already-updated value from inside the lock.
    // Lock placement is pinned by the test above; this one pins the ordering, which matters regardless.
    [Test] public async Task Proactive_ack_is_built_only_after_the_outcome_is_recorded_in_the_cache() {
        SequencedCommandProcessor? proc = null;
        long watermarkAtAckTime = -1;

        await using var p = proc = new SequencedCommandProcessor(
            "e1",
            _ => { watermarkAtAckTime = proc!.LastProcessedSeq; return AgentLiveness.Live; },
            _ => Task.CompletedTask, _ => Task.CompletedTask, NullLogger.Instance);

        await p.SubmitAsync(new SequencedItem(SequencedKind.Launch, "e1", 1, "cmd1", "a1"),
            () => Task.FromResult(new CommandOutcome(CommandOutcomeKind.LaunchExecuted)));

        // The watermark only advances once the entry is marked Processed, so seeing 1 here proves the
        // cache write happened first.
        await Assert.That(watermarkAtAckTime).IsEqualTo(1L);
    }

    // Ack construction lives INSIDE the send containment, not at the call site. readLiveness walks the
    // orchestrator's live lifecycle collections, so it can throw; if it did, Send(Build(..)) would have
    // evaluated Build before entering the try. From the lane that faults the consumer loop and leaves the
    // item's Done unresolved — the submitter waits forever and every later command is stranded, which on
    // the server side reads as a permanently held daemon capacity slot.
    [Test] public async Task A_throwing_liveness_read_neither_faults_the_lane_nor_strands_the_submitter() {
        var h = new Harness();
        await using var p = new SequencedCommandProcessor(
            "e1",
            _ => throw new InvalidOperationException("liveness read blew up"),
            a => { lock (h.Acks) h.Acks.Add(a); return Task.CompletedTask; },
            r => { lock (h.Rejects) h.Rejects.Add(r); return Task.CompletedTask; },
            NullLogger.Instance);

        // Bounded, deliberately: without the fix this does not FAIL, it HANGS — the lane faults on the
        // throw and never resolves Done. Verified by reverting the fix, where an unbounded await pinned
        // the whole suite until the harness timeout. A CI job that times out is a much worse signal than
        // a named assertion, so the wait is capped and the failure message says what it means.
        var settled = p.SubmitAsync(h.Launch(1), () => Task.FromResult(new CommandOutcome(CommandOutcomeKind.LaunchExecuted)));
        var finished = await Task.WhenAny(settled, Task.Delay(TimeSpan.FromSeconds(10)));
        await Assert.That(finished == settled)
            .IsTrue().Because("the lane faulted on the liveness throw and never resolved the submitter's Done");
        await settled;

        // The terminal FACT is still recorded — only the best-effort ack was lost.
        await Assert.That(p.LastProcessedSeq).IsEqualTo(1L);

        // The lane is still alive: a following command still executes and advances the watermark.
        var second = false;
        await p.SubmitAsync(h.Launch(2), () => { second = true; return Task.FromResult(new CommandOutcome(CommandOutcomeKind.LaunchExecuted)); });
        await Assert.That(second).IsTrue();
        await Assert.That(p.LastProcessedSeq).IsEqualTo(2L);
    }

    // The same containment on the RECOVERY path: a duplicate replay is what the server sends when it never
    // got the terminal ack, and SubmitAsync is called from the hub — an escaping throw would surface there.
    [Test] public async Task A_throwing_liveness_read_does_not_escape_a_duplicate_replay() {
        var h = new Harness();
        var throwOnRead = false;
        await using var p = new SequencedCommandProcessor(
            "e1",
            _ => throwOnRead ? throw new InvalidOperationException("liveness read blew up") : AgentLiveness.Live,
            a => { lock (h.Acks) h.Acks.Add(a); return Task.CompletedTask; },
            r => { lock (h.Rejects) h.Rejects.Add(r); return Task.CompletedTask; },
            NullLogger.Instance);

        var item = h.Launch(1);
        await p.SubmitAsync(item, () => Task.FromResult(new CommandOutcome(CommandOutcomeKind.LaunchExecuted)));

        throwOnRead = true;
        // The replay must not throw out of SubmitAsync into the hub.
        await p.SubmitAsync(item, () => Task.FromResult(new CommandOutcome(CommandOutcomeKind.LaunchExecuted)));

        await Assert.That(p.LastProcessedSeq).IsEqualTo(1L);
    }

    // A faulting SEND (as opposed to a faulting build) must be equally contained on the replay path — it
    // used to be a bare `_ = _sendAck(...)`, so a synchronous throw escaped into the hub.
    [Test] public async Task A_throwing_ack_send_does_not_escape_a_duplicate_replay() {
        var sends = 0;
        await using var p = new SequencedCommandProcessor(
            "e1", _ => AgentLiveness.Live,
            _ => { sends++; throw new InvalidOperationException("send blew up"); },
            _ => Task.CompletedTask, NullLogger.Instance);

        var item = new SequencedItem(SequencedKind.Launch, "e1", 1, "cmd1", "a1");
        await p.SubmitAsync(item, () => Task.FromResult(new CommandOutcome(CommandOutcomeKind.LaunchExecuted)));
        await p.SubmitAsync(item, () => Task.FromResult(new CommandOutcome(CommandOutcomeKind.LaunchExecuted)));

        await Assert.That(sends).IsEqualTo(2);           // proactive + replay both attempted
        await Assert.That(p.LastProcessedSeq).IsEqualTo(1L);
    }

    // A rejection is a NOTIFICATION, never the settlement fact. A throwing _sendRejected used to escape
    // RunLaneAsync -- killing the single serial consumer, leaving the command nonterminal, stranding its
    // submitter, and blocking every queued command. Same stranded-capacity class as the ack case.
    [Test] public async Task A_throwing_rejection_send_neither_faults_the_lane_nor_loses_the_settlement() {
        var h = new Harness();
        await using var p = new SequencedCommandProcessor(
            "e1", _ => AgentLiveness.Live,
            a => { lock (h.Acks) h.Acks.Add(a); return Task.CompletedTask; },
            _ => throw new InvalidOperationException("rejection send blew up"),
            NullLogger.Instance);

        // A LaunchRejected outcome takes the rejection-send path.
        var settled = p.SubmitAsync(h.Launch(1), () => Task.FromResult(
            new CommandOutcome(CommandOutcomeKind.LaunchRejected, "a1", null, CommandRejectedReason.DaemonCapacity)));
        var finished = await Task.WhenAny(settled, Task.Delay(TimeSpan.FromSeconds(10)));
        await Assert.That(finished == settled)
            .IsTrue().Because("the lane faulted on the rejection-send throw and never resolved the submitter's Done");
        await settled;

        // The settlement fact survived the failed announcement, and the terminal ack still went out.
        await Assert.That(p.LastProcessedSeq).IsEqualTo(1L);
        await Assert.That(h.Acks.Count(a => a.State == CommandAckState.Processed)).IsEqualTo(1);

        // The lane is still alive for the next command.
        await p.SubmitAsync(h.Launch(2), () => Task.FromResult(new CommandOutcome(CommandOutcomeKind.LaunchExecuted)));
        await Assert.That(p.LastProcessedSeq).IsEqualTo(2L);
    }

    // The in-progress duplicate answer: sent outside the lock and contained, so a transport throw cannot
    // escape SubmitAsync into the hub or run inside the processor's critical section.
    [Test] public async Task A_throwing_accepted_ack_send_does_not_escape_an_in_progress_duplicate() {
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var p = new SequencedCommandProcessor(
            "e1", _ => AgentLiveness.Live,
            _ => throw new InvalidOperationException("ack send blew up"),
            _ => Task.CompletedTask, NullLogger.Instance);

        var item = new SequencedItem(SequencedKind.Launch, "e1", 1, "cmd1", "a1");
        var first = p.SubmitAsync(item, async () => {
            await release.Task;
            return new CommandOutcome(CommandOutcomeKind.LaunchExecuted);
        });

        // While the first is still executing, the duplicate takes the !Processed (Accepted) arm.
        await p.SubmitAsync(item, () => Task.FromResult(new CommandOutcome(CommandOutcomeKind.LaunchExecuted)));

        release.SetResult();
        await first;
        await Assert.That(p.LastProcessedSeq).IsEqualTo(1L);
    }

    // The gap the outer try/finally did NOT close: a throwing ILogger provider. The execute-fault arm
    // logs a warning, so a logger that throws used to fault the lane AFTER finally had already told the
    // submitter the command completed -- success reported for a command left nonterminal, and every
    // later command stranded. Both new tests use a throwing logger precisely because NullLogger cannot
    // reach this path.
    sealed class ThrowingLogger : ILogger {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
                Func<TState, Exception?, string> formatter) =>
            throw new InvalidOperationException("logger provider blew up");
    }

    [Test] public async Task A_throwing_logger_neither_faults_the_lane_nor_leaves_the_command_nonterminal() {
        var h = new Harness();
        await using var p = new SequencedCommandProcessor(
            "e1", _ => AgentLiveness.Live,
            a => { lock (h.Acks) h.Acks.Add(a); return Task.CompletedTask; },
            _ => Task.CompletedTask,
            new ThrowingLogger());

        // An execution fault takes the LogWarning path, where the logger throws.
        var settled = p.SubmitAsync(h.Launch(1), () => throw new InvalidOperationException("boom"));
        var finished = await Task.WhenAny(settled, Task.Delay(TimeSpan.FromSeconds(10)));
        await Assert.That(finished == settled)
            .IsTrue().Because("the lane faulted on the logger throw and never resolved the submitter's Done");
        await settled;

        // The command is TERMINAL despite the fault -- the submitter was not told "done" over a
        // still-nonterminal cache entry.
        await Assert.That(p.LastProcessedSeq).IsEqualTo(1L);

        // And the lane survived for the next command.
        await p.SubmitAsync(h.Launch(2), () => Task.FromResult(new CommandOutcome(CommandOutcomeKind.LaunchExecuted)));
        await Assert.That(p.LastProcessedSeq).IsEqualTo(2L);
    }

    // Rejections are captured under _lock and sent after release, so no transport delegate runs inside
    // the processor's critical section. Asserted directly rather than by timing.
    [Test] public async Task Rejection_sends_do_not_run_inside_the_processor_lock() {
        SequencedCommandProcessor? proc = null;
        var sends = 0;
        var sendsUnderLock = 0;

        await using var p = proc = new SequencedCommandProcessor(
            "e1", _ => AgentLiveness.Live,
            _ => Task.CompletedTask,
            _ => { sends++; if (proc!.LockHeldByCurrentThreadForTest) sendsUnderLock++; return Task.CompletedTask; },
            NullLogger.Instance);

        // Stale epoch, then a gap -- two DIFFERENT locked reject paths.
        await p.SubmitAsync(new SequencedItem(SequencedKind.Launch, "other-epoch", 1, "c1", "a1"),
            () => Task.FromResult(new CommandOutcome(CommandOutcomeKind.LaunchExecuted)));
        await p.SubmitAsync(new SequencedItem(SequencedKind.Launch, "e1", 7, "c2", "a2"),
            () => Task.FromResult(new CommandOutcome(CommandOutcomeKind.LaunchExecuted)));

        await Assert.That(sends).IsEqualTo(2);
        await Assert.That(sendsUnderLock).IsEqualTo(0);
    }

    // NOTE on the `settled` ordering in RunLaneAsync (set at the cache+watermark commit, before any
    // notification): there is deliberately NO test for it, because none can fail. With both diagnostics
    // in SendContained guarded, no post-commit path can escape into the outer catch, so the overwrite it
    // prevents is currently unreachable. The ordering stays as defence-in-depth against a future
    // unguarded call on that path -- it is free and it removes a latent way to replay a cached
    // LaunchRejected as a contradictory InternalError -- but shipping a test that passes with or without
    // it would be worse than none: it would claim coverage that is not there.

    // The hub-side callers have no outer per-item catch, so a transport throw followed by a LOGGING
    // throw used to escape SubmitAsync entirely. Both diagnostics inside SendContained are guarded now.
    [Test] public async Task A_throwing_transport_and_a_throwing_logger_do_not_escape_the_hub_paths() {
        await using var p = new SequencedCommandProcessor(
            "e1", _ => AgentLiveness.Live,
            _ => throw new InvalidOperationException("ack send blew up"),
            _ => throw new InvalidOperationException("rejection send blew up"),
            new ThrowingLogger());

        // Stale epoch and a gap: two locked reject paths, both reached straight from SubmitAsync.
        await p.SubmitAsync(new SequencedItem(SequencedKind.Launch, "other-epoch", 1, "c1", "a1"),
            () => Task.FromResult(new CommandOutcome(CommandOutcomeKind.LaunchExecuted)));
        await p.SubmitAsync(new SequencedItem(SequencedKind.Launch, "e1", 9, "c2", "a2"),
            () => Task.FromResult(new CommandOutcome(CommandOutcomeKind.LaunchExecuted)));

        // A real command, then its duplicate replay -- the processed-replay ack path.
        var item = new SequencedItem(SequencedKind.Launch, "e1", 1, "c3", "a3");
        await p.SubmitAsync(item, () => Task.FromResult(new CommandOutcome(CommandOutcomeKind.LaunchExecuted)));
        await p.SubmitAsync(item, () => Task.FromResult(new CommandOutcome(CommandOutcomeKind.LaunchExecuted)));

        await Assert.That(p.LastProcessedSeq).IsEqualTo(1L);
    }

    // A duplicate of a non-rejected (executed) command has no cached reject reason -> null RejectionReason.
    [Test] public async Task Duplicate_of_an_executed_launch_has_no_rejection_reason() {
        var h = new Harness(); await using var p = h.P();
        var item = h.Launch(1);
        await p.SubmitAsync(item, () => Task.FromResult(new CommandOutcome(CommandOutcomeKind.LaunchExecuted, "a", "sess")));
        await p.SubmitAsync(item, () => Task.FromResult(new CommandOutcome(CommandOutcomeKind.LaunchExecuted)));
        await Assert.That(h.Acks).HasCount().EqualTo(2);                              // proactive + duplicate replay
        await Assert.That(h.Acks[0].RejectionReason).IsNull();
        await Assert.That(h.Acks[1].RejectionReason).IsNull();
    }
}
