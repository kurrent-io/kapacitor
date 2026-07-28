using System.Collections.Concurrent;
using Capacitor.Cli.Core;
using Capacitor.Cli.Daemon.Services;
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
