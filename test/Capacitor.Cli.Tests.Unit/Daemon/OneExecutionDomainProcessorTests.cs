using System.Collections.Concurrent;
using Capacitor.Cli.Core;
using Capacitor.Cli.Daemon.Services;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;

namespace Capacitor.Cli.Tests.Unit.Daemon;

/// <summary>
/// Spec §3.3 (ONE execution domain), processor level: <c>SubmitUnsequenced</c>, cross-format ordering,
/// stop admission, coalescing + key lifecycle, the queued-stop hysteresis alarm, active-launch instance
/// tracking, fault isolation, and the lane's shutdown order. These live here rather than in an
/// orchestrator test because the lane is the thing under test — a raw processor lets a test drive exact
/// interleavings (park item 1, submit items 2..N, assert queue depth) with no launch machinery in the way.
///
/// <para>Every wait that a regression could turn infinite is bounded, so a broken invariant FAILS with a
/// named assertion instead of hanging the suite.</para>
/// </summary>
public class OneExecutionDomainProcessorTests {
    static readonly TimeSpan Bounded = TimeSpan.FromSeconds(30);

    sealed class CapturingLogger : ILogger {
        public readonly ConcurrentQueue<(LogLevel Level, string Message)> Entries = new();
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel level, EventId id, TState state, Exception? ex,
                Func<TState, Exception?, string> formatter) =>
            Entries.Enqueue((level, formatter(state, ex)));

        public int ErrorCount => Entries.Count(e => e.Level == LogLevel.Error);
    }

    sealed class Harness {
        public readonly List<CommandAck> Acks = [];
        public readonly List<CommandRejected> Rejects = [];
        public readonly ConcurrentQueue<string> ExecOrder = new();
        public readonly HashSet<string> KnownTargets = new(StringComparer.Ordinal);
        public readonly CapturingLogger Logger = new();
        public readonly FakeTimeProvider Time = new();

        public SequencedCommandProcessor P(string epoch = "e1", int bound = 256, Task? startBarrier = null) => new(
            epoch, _ => AgentLiveness.Live,
            a => { lock (Acks) Acks.Add(a); return Task.CompletedTask; },
            r => { lock (Rejects) Rejects.Add(r); return Task.CompletedTask; },
            Logger, bound,
            isKnownStopTarget: id => { lock (KnownTargets) return KnownTargets.Contains(id); },
            time: Time, startBarrier: startBarrier);

        public static SequencedItem SeqLaunch(long seq, string agent, string epoch = "e1") =>
            new(SequencedKind.Launch, epoch, seq, "cmd" + seq, agent);

        public static SequencedItem SeqStop(long seq, string agent, string epoch = "e1") =>
            new(SequencedKind.Stop, epoch, seq, "cmd" + seq, agent);

        /// <summary>An un-seq'd item that records its execution under a label and then completes.</summary>
        public UnsequencedItem Unseq(UnsequencedKind kind, string agent, string label, string payload = "stop",
                Func<Task>? body = null) =>
            new(kind, agent, payload, async () => {
                ExecOrder.Enqueue(label);
                if (body is not null) await body();
            });

        /// <summary>Stands in for the orchestrator's registry + PID-record surface, i.e. what
        /// <c>isKnownStopTarget</c> answers for.</summary>
        public void Know(params string[] ids) { lock (KnownTargets) foreach (var id in ids) KnownTargets.Add(id); }
    }

    /// <summary>A parked delegate: signals when it starts, then blocks until the test releases it.
    ///
    /// <para>DISPOSABLE ON PURPOSE, and every test declares it with <c>using var</c> AFTER its
    /// <c>await using var p</c>: locals dispose in reverse declaration order, so a FAILING assertion
    /// releases the park first and the processor's drain then completes. Without that, a failed assertion
    /// would leave the lane executing a delegate nobody ever releases and <c>DisposeAsync</c> — which awaits
    /// the in-flight item, exactly as shipped — would hang the suite instead of reporting the failure.</para></summary>
    sealed class Park : IDisposable {
        public readonly TaskCompletionSource Started = new(TaskCreationOptions.RunContinuationsAsynchronously);
        readonly TaskCompletionSource _release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public async Task RunAsync() { Started.TrySetResult(); await _release.Task; }
        public void Release() => _release.TrySetResult();
        public void Dispose() => Release();
    }

    static async Task WaitBounded(Task task, string because) {
        var finished = await Task.WhenAny(task, Task.Delay(Bounded));
        await Assert.That(finished == task).IsTrue().Because(because);
        await task;
    }

    static async Task SpinUntil(Func<bool> condition, string because) {
        var deadline = DateTime.UtcNow + Bounded;
        while (!condition() && DateTime.UtcNow < deadline) await Task.Delay(5);
        await Assert.That(condition()).IsTrue().Because(because);
    }

    // ══ One-domain ordering ══════════════════════════════════════════════════════════════════════════

    [Test]
    public async Task An_unsequenced_stop_enqueued_after_an_unsequenced_launch_executes_after_it() {
        var h = new Harness();
        await using var p = h.P();
        using var park = new Park();

        await Assert.That(p.SubmitUnsequenced(h.Unseq(UnsequencedKind.Launch, "x", "launch", "launch", park.RunAsync)))
            .IsEqualTo(SubmitOutcome.Committed);
        await WaitBounded(park.Started.Task, "the launch was never dequeued and executing");

        // The launch's active instance makes x admissible even though nothing registered it.
        await Assert.That(p.SubmitUnsequenced(h.Unseq(UnsequencedKind.Stop, "x", "stop")))
            .IsEqualTo(SubmitOutcome.Committed);
        await Assert.That(h.ExecOrder.ToArray()).IsEquivalentTo(new[] { "launch" }); // stop has NOT run

        park.Release();
        await WaitBounded(p.WhenIdleForTest(), "the lane never drained the launch and its queued stop");
        await Assert.That(h.ExecOrder.ToArray()).IsEquivalentTo(new[] { "launch", "stop" });
    }

    [Test]
    public async Task An_unsequenced_stop_enqueued_after_a_sequenced_launch_executes_after_it_settles() {
        var h = new Harness();
        await using var p = h.P();
        using var park = new Park();

        _ = p.SubmitAsync(Harness.SeqLaunch(1, "x"), async () => {
            h.ExecOrder.Enqueue("seq-launch");
            await park.RunAsync();
            return new CommandOutcome(CommandOutcomeKind.LaunchExecuted, "x");
        });
        await WaitBounded(park.Started.Task, "the sequenced launch never started executing");

        // Cross-format direction A: un-seq'd stop behind a SEQUENCED launch for the same agent.
        await Assert.That(p.SubmitUnsequenced(h.Unseq(UnsequencedKind.Stop, "x", "unseq-stop")))
            .IsEqualTo(SubmitOutcome.Committed);
        await Assert.That(h.ExecOrder.ToArray()).IsEquivalentTo(new[] { "seq-launch" });

        park.Release();
        await WaitBounded(p.WhenIdleForTest(), "the lane never drained the sequenced launch and its queued stop");
        await Assert.That(h.ExecOrder.ToArray()).IsEquivalentTo(new[] { "seq-launch", "unseq-stop" });
        await Assert.That(p.LastProcessedSeq).IsEqualTo(1L);
    }

    [Test]
    public async Task A_sequenced_item_enqueued_after_an_unsequenced_item_executes_after_it() {
        var h = new Harness();
        await using var p = h.P();
        using var park = new Park();
        h.Know("x");

        await Assert.That(p.SubmitUnsequenced(h.Unseq(UnsequencedKind.Stop, "x", "unseq-stop", "stop", park.RunAsync)))
            .IsEqualTo(SubmitOutcome.Committed);
        await WaitBounded(park.Started.Task, "the un-sequenced stop never started executing");

        // Cross-format direction B: a SEQUENCED item behind an un-seq'd one.
        var seq = p.SubmitAsync(Harness.SeqLaunch(1, "y"), () => {
            h.ExecOrder.Enqueue("seq-launch");
            return Task.FromResult(new CommandOutcome(CommandOutcomeKind.LaunchExecuted, "y"));
        });
        await Assert.That(p.HighestAcceptedSeq).IsEqualTo(1L);                      // accepted immediately
        await Assert.That(h.ExecOrder.ToArray()).IsEquivalentTo(new[] { "unseq-stop" }); // but not executed

        park.Release();
        await WaitBounded(seq, "the sequenced item queued behind an un-sequenced one never settled");
        await Assert.That(h.ExecOrder.ToArray()).IsEquivalentTo(new[] { "unseq-stop", "seq-launch" });
    }

    [Test]
    public async Task Two_launches_never_execute_concurrently_on_the_lane() {
        var h = new Harness();
        await using var p = h.P();
        var concurrent = 0;
        var maxConcurrent = 0;

        Func<Task> body = async () => {
            var now = Interlocked.Increment(ref concurrent);
            InterlockedMax(ref maxConcurrent, now);
            await Task.Delay(20);
            Interlocked.Decrement(ref concurrent);
        };

        for (var i = 0; i < 5; i++)
            await Assert.That(p.SubmitUnsequenced(h.Unseq(UnsequencedKind.Launch, "a" + i, "l" + i, "launch", body)))
                .IsEqualTo(SubmitOutcome.Committed);

        await WaitBounded(p.WhenIdleForTest(), "the lane never drained its five launches");
        await Assert.That(maxConcurrent).IsEqualTo(1);
    }

    static void InterlockedMax(ref int target, int value) {
        int seen;
        while ((seen = Volatile.Read(ref target)) < value && Interlocked.CompareExchange(ref target, value, seen) != seen) { }
    }

    [Test]
    public async Task SubmitUnsequenced_commits_synchronously_with_no_yield_before_the_lane_write() {
        var h = new Harness();
        await using var p = h.P();
        using var park = new Park();
        h.Know("x", "y");

        // Park the lane so nothing can drain, then assert the SECOND submission is already queued the
        // instant SubmitUnsequenced returns — the same synchronous-commit contract sequenced acceptance has.
        p.SubmitUnsequenced(h.Unseq(UnsequencedKind.Stop, "x", "first", "stop", park.RunAsync));
        await WaitBounded(park.Started.Task, "the first stop never started executing");

        p.SubmitUnsequenced(h.Unseq(UnsequencedKind.Stop, "y", "second"));
        await Assert.That(p.QueuedStopDepth).IsEqualTo(1); // visible with no await in between

        park.Release();
        await WaitBounded(p.WhenIdleForTest(), "the lane never drained");
    }

    // ══ Stop admission ══════════════════════════════════════════════════════════════════════════════

    [Test]
    public async Task Distinct_unknown_target_stops_are_dropped_at_admission_without_touching_the_queue() {
        var h = new Harness();
        await using var p = h.P();
        using var park = new Park();
        h.Know("known");

        p.SubmitUnsequenced(h.Unseq(UnsequencedKind.Stop, "known", "known-stop", "stop", park.RunAsync));
        await WaitBounded(park.Started.Task, "the known-target stop never started executing");
        await Assert.That(p.QueuedStopDepth).IsEqualTo(0); // dequeued to start, so its key already retired

        for (var i = 0; i < 12; i++)
            await Assert.That(p.SubmitUnsequenced(h.Unseq(UnsequencedKind.Stop, "ghost" + i, "ghost" + i)))
                .IsEqualTo(SubmitOutcome.DroppedUnknownTarget);

        await Assert.That(p.QueuedStopDepth).IsEqualTo(0);
        park.Release();
        await WaitBounded(p.WhenIdleForTest(), "the lane never drained");
        await Assert.That(h.ExecOrder.ToArray()).IsEquivalentTo(new[] { "known-stop" }); // no ghost ran
    }

    [Test]
    public async Task A_stop_for_a_dequeued_and_parked_launch_is_admitted_and_executes_after_it_unsequenced_launch() =>
        await StopForParkedLaunchAsync(sequencedLaunch: false);

    [Test]
    public async Task A_stop_for_a_dequeued_and_parked_launch_is_admitted_and_executes_after_it_sequenced_launch() =>
        await StopForParkedLaunchAsync(sequencedLaunch: true);

    /// <summary>The active-set pin, both formats: a launch parked at the consent gate has created no
    /// registry entry anywhere, so ONLY its active launch instance can make the stop admissible.</summary>
    static async Task StopForParkedLaunchAsync(bool sequencedLaunch) {
        var h = new Harness();
        await using var p = h.P();
        using var park = new Park();
        // Deliberately NOT known: nothing has registered "x" — the launch is still parked.

        if (sequencedLaunch)
            _ = p.SubmitAsync(Harness.SeqLaunch(1, "x"), async () => {
                h.ExecOrder.Enqueue("launch");
                await park.RunAsync();
                return new CommandOutcome(CommandOutcomeKind.LaunchExecuted, "x");
            });
        else
            p.SubmitUnsequenced(h.Unseq(UnsequencedKind.Launch, "x", "launch", "launch", park.RunAsync));

        await WaitBounded(park.Started.Task, "the parked launch never started executing");
        await Assert.That(p.IsActiveLaunchTargetForTest("x")).IsTrue();

        await Assert.That(p.SubmitUnsequenced(h.Unseq(UnsequencedKind.Stop, "x", "stop")))
            .IsEqualTo(SubmitOutcome.Committed);

        park.Release();
        await WaitBounded(p.WhenIdleForTest(), "the lane never drained the parked launch and its stop");
        await Assert.That(h.ExecOrder.ToArray()).IsEquivalentTo(new[] { "launch", "stop" });
        await Assert.That(p.IsActiveLaunchTargetForTest("x")).IsFalse(); // instance retired at settlement
    }

    // ══ Coalescing + key lifecycle ══════════════════════════════════════════════════════════════════

    [Test]
    public async Task Duplicate_unsequenced_stops_for_one_agent_collapse_to_one_queued_entry_and_still_execute() {
        var h = new Harness();
        await using var p = h.P();
        using var park = new Park();
        h.Know("x", "blocker");

        p.SubmitUnsequenced(h.Unseq(UnsequencedKind.Stop, "blocker", "blocker", "stop", park.RunAsync));
        await WaitBounded(park.Started.Task, "the blocker stop never started executing");

        await Assert.That(p.SubmitUnsequenced(h.Unseq(UnsequencedKind.Stop, "x", "x-stop")))
            .IsEqualTo(SubmitOutcome.Committed);
        for (var i = 0; i < 9; i++)
            await Assert.That(p.SubmitUnsequenced(h.Unseq(UnsequencedKind.Stop, "x", "x-stop-dup" + i)))
                .IsEqualTo(SubmitOutcome.Coalesced);

        await Assert.That(p.QueuedStopDepth).IsEqualTo(1); // ten submissions, one queued entry

        park.Release();
        await WaitBounded(p.WhenIdleForTest(), "the lane never drained");
        await Assert.That(h.ExecOrder.ToArray()).IsEquivalentTo(new[] { "blocker", "x-stop" }); // executed once
    }

    [Test]
    public async Task Distinct_stop_payload_classes_for_one_target_queue_separately() {
        var h = new Harness();
        await using var p = h.P();
        using var park = new Park();
        h.Know("x", "blocker");

        p.SubmitUnsequenced(h.Unseq(UnsequencedKind.Stop, "blocker", "blocker", "stop", park.RunAsync));
        await WaitBounded(park.Started.Task, "the blocker stop never started executing");

        // Coalescing is per (target, payload CLASS) — a force flag is a different class, so it queues.
        await Assert.That(p.SubmitUnsequenced(h.Unseq(UnsequencedKind.Stop, "x", "plain", "stop")))
            .IsEqualTo(SubmitOutcome.Committed);
        await Assert.That(p.SubmitUnsequenced(h.Unseq(UnsequencedKind.Stop, "x", "forced", "stop:force")))
            .IsEqualTo(SubmitOutcome.Committed);
        await Assert.That(p.QueuedStopDepth).IsEqualTo(2);

        park.Release();
        await WaitBounded(p.WhenIdleForTest(), "the lane never drained");
        await Assert.That(h.ExecOrder.ToArray()).IsEquivalentTo(new[] { "blocker", "plain", "forced" });
    }

    [Test]
    public async Task Stop_then_launch_then_stop_executes_the_second_stop_after_the_launch_unsequenced_launch() =>
        await StopLaunchStopAsync(sequencedLaunch: false, preFillToAlarm: false);

    [Test]
    public async Task Stop_then_launch_then_stop_executes_the_second_stop_after_the_launch_sequenced_launch() =>
        await StopLaunchStopAsync(sequencedLaunch: true, preFillToAlarm: false);

    [Test]
    public async Task Stop_then_launch_then_stop_holds_with_the_queue_prefilled_to_the_alarm_threshold() =>
        await StopLaunchStopAsync(sequencedLaunch: false, preFillToAlarm: true);

    /// <summary>Launch-aware coalescing: a launch COMMIT for X clears X's pending-stop keys, so the stop
    /// that arrives AFTER the launch cannot be absorbed by the one that arrived BEFORE it. Also run with the
    /// stop queue pre-filled past the alarm threshold — saturation must not reorder or drop anything, since
    /// the threshold is an alarm, never a cap.</summary>
    static async Task StopLaunchStopAsync(bool sequencedLaunch, bool preFillToAlarm) {
        var h = new Harness();
        await using var p = h.P();
        using var park = new Park();
        h.Know("x", "blocker");

        p.SubmitUnsequenced(h.Unseq(UnsequencedKind.Stop, "blocker", "blocker", "stop", park.RunAsync));
        await WaitBounded(park.Started.Task, "the blocker stop never started executing");

        if (preFillToAlarm) {
            for (var i = 0; i < SequencedCommandProcessor.StopQueueAlarmThreshold; i++) {
                h.Know("filler" + i);
                await Assert.That(p.SubmitUnsequenced(h.Unseq(UnsequencedKind.Stop, "filler" + i, "filler" + i)))
                    .IsEqualTo(SubmitOutcome.Committed);
            }
            await Assert.That(p.QueuedStopDepth).IsEqualTo(SequencedCommandProcessor.StopQueueAlarmThreshold);
        }

        await Assert.That(p.SubmitUnsequenced(h.Unseq(UnsequencedKind.Stop, "x", "stop-1"))).IsEqualTo(SubmitOutcome.Committed);

        if (sequencedLaunch)
            _ = p.SubmitAsync(Harness.SeqLaunch(1, "x"), () => {
                h.ExecOrder.Enqueue("launch");
                return Task.FromResult(new CommandOutcome(CommandOutcomeKind.LaunchExecuted, "x"));
            });
        else
            p.SubmitUnsequenced(h.Unseq(UnsequencedKind.Launch, "x", "launch", "launch"));

        // The launch commit cleared stop-1's key, so this is a FRESH entry rather than a coalesce.
        await Assert.That(p.SubmitUnsequenced(h.Unseq(UnsequencedKind.Stop, "x", "stop-2"))).IsEqualTo(SubmitOutcome.Committed);

        park.Release();
        await WaitBounded(p.WhenIdleForTest(), "the lane never drained the stop/launch/stop sequence");

        var order = h.ExecOrder.ToArray().Where(l => l is "stop-1" or "launch" or "stop-2").ToArray();
        await Assert.That(order).IsEquivalentTo(new[] { "stop-1", "launch", "stop-2" });
        await Assert.That(p.QueuedStopDepth).IsEqualTo(0);
    }

    [Test]
    public async Task A_throwing_stop_followed_by_a_same_payload_retry_commits_and_executes_the_retry() {
        var h = new Harness();
        await using var p = h.P();
        h.Know("x");
        var attempts = 0;

        // The key is retired when the item is DEQUEUED to start, so a retry after a FAULTED stop is a fresh
        // item rather than a swallowed duplicate — retry semantics survive teardown failure.
        p.SubmitUnsequenced(new UnsequencedItem(UnsequencedKind.Stop, "x", "stop", () => {
            Interlocked.Increment(ref attempts);
            h.ExecOrder.Enqueue("attempt");
            throw new InvalidOperationException("teardown blew up");
        }));

        await SpinUntil(() => Volatile.Read(ref attempts) == 1, "the first stop never executed");
        await WaitBounded(p.WhenIdleForTest(), "the faulting stop never finalized");

        await Assert.That(p.SubmitUnsequenced(h.Unseq(UnsequencedKind.Stop, "x", "retry")))
            .IsEqualTo(SubmitOutcome.Committed);
        await WaitBounded(p.WhenIdleForTest(), "the retry never executed");
        await Assert.That(h.ExecOrder.ToArray()).IsEquivalentTo(new[] { "attempt", "retry" });
    }

    [Test]
    public async Task An_older_stop_starting_after_launch_aware_clearing_does_not_erase_the_newer_key() {
        var h = new Harness();
        await using var p = h.P();
        using var park = new Park();
        h.Know("x", "blocker");

        p.SubmitUnsequenced(h.Unseq(UnsequencedKind.Stop, "blocker", "blocker", "stop", park.RunAsync));
        await WaitBounded(park.Started.Task, "the blocker stop never started executing");

        p.SubmitUnsequenced(h.Unseq(UnsequencedKind.Stop, "x", "old"));            // segment 1
        p.SubmitUnsequenced(h.Unseq(UnsequencedKind.Launch, "x", "launch", "launch")); // clears segment 1's key
        p.SubmitUnsequenced(h.Unseq(UnsequencedKind.Stop, "x", "new"));            // segment 2's key
        await Assert.That(p.PendingStopKeyCountForTest).IsEqualTo(1);              // only the newer one

        park.Release();

        // The OLD item now starts. Its identity-guarded retire must leave the NEWER item's key alone, so a
        // same-payload submission arriving in this window still coalesces onto the newer queued entry
        // instead of duplicating it.
        await SpinUntil(() => h.ExecOrder.Contains("old"), "the older stop never started");
        await WaitBounded(p.WhenIdleForTest(), "the lane never drained");
        await Assert.That(h.ExecOrder.ToArray().Where(l => l is "old" or "launch" or "new").ToArray())
            .IsEquivalentTo(new[] { "old", "launch", "new" });
        await Assert.That(p.PendingStopKeyCountForTest).IsEqualTo(0);
    }

    // ══ Instance-counted active launches ════════════════════════════════════════════════════════════

    [Test]
    public async Task Launch_launch_stop_keeps_the_id_admissible_when_the_first_launch_fails_unsequenced_first() =>
        await LaunchLaunchStopAsync(firstIsSequenced: false);

    [Test]
    public async Task Launch_launch_stop_keeps_the_id_admissible_when_the_first_launch_fails_sequenced_first() =>
        await LaunchLaunchStopAsync(firstIsSequenced: true);

    /// <summary>The instance-count pin, both mixed-format orders: launch(X) -> launch(X) -> stop(X) where the
    /// FIRST launch fails while the SECOND is still parked. Reference counting is what keeps X admissible —
    /// an id-set with a single flag would have dropped the stop the moment the first instance settled.
    ///
    /// <para>A gate item is parked FIRST so both launches queue behind it: without that, nothing stops the
    /// lane from dequeuing and finalizing the deliberately-failing first launch before the instance-count
    /// assertion below runs, making <c>== 2</c> a race rather than a pin.</para></summary>
    static async Task LaunchLaunchStopAsync(bool firstIsSequenced) {
        var h = new Harness();
        await using var p = h.P();
        using var gate = new Park();
        using var second = new Park();
        var firstFailed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        p.SubmitUnsequenced(h.Unseq(UnsequencedKind.Launch, "gate", "gate", "launch", gate.RunAsync));
        await WaitBounded(gate.Started.Task, "the gate launch never started executing");

        Func<Task> failing = () => {
            h.ExecOrder.Enqueue("launch-1");
            firstFailed.TrySetResult();
            throw new InvalidOperationException("first launch failed");
        };

        if (firstIsSequenced) {
            _ = p.SubmitAsync(Harness.SeqLaunch(1, "x"), async () => { await failing(); return default; });
            p.SubmitUnsequenced(h.Unseq(UnsequencedKind.Launch, "x", "launch-2", "launch", second.RunAsync));
        } else {
            p.SubmitUnsequenced(new UnsequencedItem(UnsequencedKind.Launch, "x", "launch", failing));
            _ = p.SubmitAsync(Harness.SeqLaunch(1, "x"), async () => {
                h.ExecOrder.Enqueue("launch-2");
                await second.RunAsync();
                return new CommandOutcome(CommandOutcomeKind.LaunchExecuted, "x");
            });
        }

        // Both launches are queued behind the still-parked gate — neither has started, so this observes
        // the submit-time instance count rather than racing the lane's drain.
        await Assert.That(p.ActiveLaunchInstancesForTest("x")).IsEqualTo(2);

        gate.Release();
        await WaitBounded(firstFailed.Task, "the first launch never ran");
        await WaitBounded(second.Started.Task, "the second launch never started executing");

        // One instance settled (terminally failed); the SECOND is still in flight, so x stays admissible.
        await SpinUntil(() => p.ActiveLaunchInstancesForTest("x") == 1, "the failed launch's instance was not retired");
        await Assert.That(p.SubmitUnsequenced(h.Unseq(UnsequencedKind.Stop, "x", "stop")))
            .IsEqualTo(SubmitOutcome.Committed);

        second.Release();
        await WaitBounded(p.WhenIdleForTest(), "the lane never drained");
        await Assert.That(h.ExecOrder.ToArray().Where(l => l == "stop").ToArray()).IsEquivalentTo(new[] { "stop" });
        await Assert.That(h.ExecOrder.ToArray()[^1]).IsEqualTo("stop"); // after BOTH launches settled
        await Assert.That(p.IsActiveLaunchTargetForTest("x")).IsFalse();
    }

    // ══ Accept-branch-only pin ══════════════════════════════════════════════════════════════════════

    [Test]
    public async Task A_duplicate_replay_and_every_rejected_sequenced_launch_mutate_no_tracking_state() {
        var h = new Harness();
        // bound: 1 — with one accepted-and-unacked identity in the cache, the NEXT next-seq submission is
        // rejected as Backpressure, which is the only way to reach that arm deterministically.
        await using var p = h.P("e1", bound: 1);
        using var park = new Park();
        h.Know("blocker", "keyed");

        // Park the lane, then establish the exact state the rejections must not touch: one queued stop for
        // "blocker", one accepted sequenced launch for "keyed" (holding an active instance), and one queued
        // stop for "keyed" created AFTER that launch (so it is the current segment's key).
        p.SubmitUnsequenced(h.Unseq(UnsequencedKind.Stop, "blocker", "blocker", "stop", park.RunAsync));
        await WaitBounded(park.Started.Task, "the blocker stop never started executing");

        var accepted = Harness.SeqLaunch(1, "keyed");
        _ = p.SubmitAsync(accepted, () => Task.FromResult(new CommandOutcome(CommandOutcomeKind.LaunchExecuted, "keyed")));
        p.SubmitUnsequenced(h.Unseq(UnsequencedKind.Stop, "keyed", "keyed-stop"));

        var depthBefore     = p.QueuedStopDepth;
        var keysBefore      = p.PendingStopKeyCountForTest;
        var activeIdsBefore = p.ActiveLaunchIdCountForTest;
        var activeForKeyed  = p.ActiveLaunchInstancesForTest("keyed");
        var watermarkBefore = p.HighestAcceptedSeq;
        await Assert.That(activeForKeyed).IsEqualTo(1);
        await Assert.That(depthBefore).IsEqualTo(1);

        // 1) duplicate replay — answered from the identity cache, never re-executed.
        _ = p.SubmitAsync(accepted, () => Task.FromResult(new CommandOutcome(CommandOutcomeKind.LaunchExecuted)));
        // 2) stale epoch — never touches THIS epoch's lane.
        _ = p.SubmitAsync(Harness.SeqLaunch(2, "keyed", epoch: "other"), () => Task.FromResult(default(CommandOutcome)));
        // 3) non-next (a gap).
        _ = p.SubmitAsync(Harness.SeqLaunch(9, "keyed"), () => Task.FromResult(default(CommandOutcome)));
        // 4) backpressure — the next seq, with the identity cache already at its bound.
        _ = p.SubmitAsync(Harness.SeqLaunch(2, "keyed"), () => Task.FromResult(default(CommandOutcome)));

        await Assert.That(h.Rejects.Select(r => r.Reason)).Contains(CommandRejectedReason.StaleEpoch);
        await Assert.That(h.Rejects.Select(r => r.Reason)).Contains(CommandRejectedReason.WrongNext);
        await Assert.That(h.Rejects.Select(r => r.Reason)).Contains(CommandRejectedReason.Backpressure);

        // The whole point: the sequenced mutations live in the ACCEPT branch only.
        await Assert.That(p.QueuedStopDepth).IsEqualTo(depthBefore);
        await Assert.That(p.PendingStopKeyCountForTest).IsEqualTo(keysBefore);
        await Assert.That(p.ActiveLaunchIdCountForTest).IsEqualTo(activeIdsBefore);
        await Assert.That(p.ActiveLaunchInstancesForTest("keyed")).IsEqualTo(activeForKeyed);
        await Assert.That(p.HighestAcceptedSeq).IsEqualTo(watermarkBefore);

        park.Release();
        await WaitBounded(p.WhenIdleForTest(), "the lane never drained");
    }

    // ══ Fault isolation ═════════════════════════════════════════════════════════════════════════════

    [Test]
    public async Task A_faulting_unsequenced_item_does_not_kill_the_lane_and_a_stop_behind_it_still_executes() {
        var h = new Harness();
        await using var p = h.P();
        h.Know("survivor");
        using var park = new Park();

        p.SubmitUnsequenced(h.Unseq(UnsequencedKind.Launch, "gate", "gate", "launch", park.RunAsync));
        await WaitBounded(park.Started.Task, "the gate launch never started executing");

        p.SubmitUnsequenced(new UnsequencedItem(UnsequencedKind.Launch, "boom", "launch",
            () => throw new InvalidOperationException("item blew up")));
        p.SubmitUnsequenced(h.Unseq(UnsequencedKind.Stop, "survivor", "after-fault"));

        park.Release();
        await WaitBounded(p.WhenIdleForTest(), "the lane died on the faulting item and never ran the queued stop");
        await Assert.That(h.ExecOrder.ToArray()).Contains("after-fault");
        await Assert.That(p.IsActiveLaunchTargetForTest("boom")).IsFalse(); // faulted launch still retired
    }

    // ══ Hysteresis alarm ════════════════════════════════════════════════════════════════════════════

    [Test]
    public async Task The_queued_stop_alarm_fires_once_stays_quiet_during_growth_and_rearms_only_below_the_watermark() {
        var h = new Harness();
        await using var p = h.P();
        using var park = new Park();
        h.Know("blocker");

        p.SubmitUnsequenced(h.Unseq(UnsequencedKind.Stop, "blocker", "blocker", "stop", park.RunAsync));
        await WaitBounded(park.Started.Task, "the blocker stop never started executing");

        // Grow to one below the threshold: silent.
        for (var i = 0; i < SequencedCommandProcessor.StopQueueAlarmThreshold - 1; i++) {
            h.Know("a" + i);
            p.SubmitUnsequenced(h.Unseq(UnsequencedKind.Stop, "a" + i, "a" + i));
        }
        await Assert.That(h.Logger.ErrorCount).IsEqualTo(0);

        // The crossing emits exactly one Error.
        h.Know("cross");
        p.SubmitUnsequenced(h.Unseq(UnsequencedKind.Stop, "cross", "cross"));
        await Assert.That(h.Logger.ErrorCount).IsEqualTo(1);

        // Further growth is quiet even after the minimum interval elapses — the alarm is edge-triggered,
        // and only a drain below the hysteresis watermark re-arms it.
        h.Time.Advance(SequencedCommandProcessor.StopQueueAlarmMinInterval * 3);
        for (var i = 0; i < 40; i++) {
            h.Know("b" + i);
            p.SubmitUnsequenced(h.Unseq(UnsequencedKind.Stop, "b" + i, "b" + i));
        }
        await Assert.That(h.Logger.ErrorCount).IsEqualTo(1);
        await Assert.That(p.QueuedStopHighWater).IsGreaterThanOrEqualTo(SequencedCommandProcessor.StopQueueAlarmThreshold);

        // Drain everything (well below the watermark), then cross again with the interval satisfied.
        park.Release();
        await WaitBounded(p.WhenIdleForTest(), "the lane never drained the saturated stop queue");
        await Assert.That(p.QueuedStopDepth).IsEqualTo(0);

        using var park2 = new Park();
        h.Know("blocker2");
        p.SubmitUnsequenced(h.Unseq(UnsequencedKind.Stop, "blocker2", "blocker2", "stop", park2.RunAsync));
        await WaitBounded(park2.Started.Task, "the second blocker stop never started executing");
        for (var i = 0; i < SequencedCommandProcessor.StopQueueAlarmThreshold; i++) {
            h.Know("c" + i);
            p.SubmitUnsequenced(h.Unseq(UnsequencedKind.Stop, "c" + i, "c" + i));
        }
        await Assert.That(h.Logger.ErrorCount).IsEqualTo(2); // re-armed by the drain, interval satisfied

        park2.Release();
        await WaitBounded(p.WhenIdleForTest(), "the lane never drained");
    }

    [Test]
    public async Task Boundary_oscillation_inside_the_minimum_interval_emits_no_further_errors() {
        var h = new Harness();
        await using var p = h.P();

        // Six crossings, each preceded by a full drain (which re-arms) but all inside one minimum interval:
        // exactly one Error total. Without the interval, this is where the alarm becomes its own incident.
        for (var round = 0; round < 6; round++) {
            using var park = new Park();
            h.Know("gate" + round);
            p.SubmitUnsequenced(h.Unseq(UnsequencedKind.Stop, "gate" + round, "gate" + round, "stop", park.RunAsync));
            await WaitBounded(park.Started.Task, "the round's gate stop never started executing");

            for (var i = 0; i < SequencedCommandProcessor.StopQueueAlarmThreshold; i++) {
                var id = $"r{round}-{i}";
                h.Know(id);
                p.SubmitUnsequenced(h.Unseq(UnsequencedKind.Stop, id, id));
            }

            h.Time.Advance(TimeSpan.FromSeconds(1)); // total advance stays under the 60s minimum
            park.Release();
            await WaitBounded(p.WhenIdleForTest(), "the lane never drained round " + round);
        }

        await Assert.That(h.Logger.ErrorCount).IsEqualTo(1);
    }

    // ══ Shutdown ═══════════════════════════════════════════════════════════════════════════════════

    [Test]
    public async Task Shutdown_synthesizes_terminal_answers_for_queued_sequenced_items_and_discards_unsequenced_ones() {
        var h = new Harness();
        var p = h.P();
        using var park = new Park();
        h.Know("victim");

        p.SubmitUnsequenced(h.Unseq(UnsequencedKind.Launch, "gate", "gate", "launch", park.RunAsync));
        await WaitBounded(park.Started.Task, "the gate launch must be dequeued and running before shutdown begins");

        // Queued behind the in-flight item: one un-seq'd stop (to be discarded) and one ACCEPTED sequenced
        // launch (to be synthesized). The sequenced launch also holds an active instance, so its
        // shutdown-synthesized settlement must retire that exact token.
        p.SubmitUnsequenced(h.Unseq(UnsequencedKind.Stop, "victim", "victim-stop"));
        var seqDone = p.SubmitAsync(Harness.SeqLaunch(1, "queued-launch"), () => {
            h.ExecOrder.Enqueue("queued-launch");
            return Task.FromResult(new CommandOutcome(CommandOutcomeKind.LaunchExecuted));
        });

        await Assert.That(p.QueuedStopDepth).IsEqualTo(1);
        await Assert.That(p.IsActiveLaunchTargetForTest("queued-launch")).IsTrue();
        await Assert.That(p.IsActiveLaunchTargetForTest("gate")).IsTrue();

        // Deterministic shutdown ordering: DisposeAsync closes the lane SYNCHRONOUSLY (before its first
        // await), so capturing its task first and only THEN releasing the in-flight item guarantees the
        // queued items are reached in draining mode rather than racing a normal execution.
        var disposing = p.DisposeAsync().AsTask();
        park.Release();
        await WaitBounded(disposing, "DisposeAsync hung instead of draining the lane");

        // The accepted sequenced item got its terminal answer, exactly once, and its done-task completed.
        await Assert.That(seqDone.IsCompleted).IsTrue();
        await Assert.That(seqDone.IsFaulted).IsFalse();
        await Assert.That(h.Rejects.Count(r => r.Seq == 1 && r.Reason == CommandRejectedReason.InternalError)).IsEqualTo(1);
        await Assert.That(p.LastProcessedSeq).IsEqualTo(1L);

        // The un-seq'd stop was DISCARDED, not executed, and the counter returned to zero.
        await Assert.That(h.ExecOrder.ToArray()).DoesNotContain("victim-stop");
        await Assert.That(h.ExecOrder.ToArray()).DoesNotContain("queued-launch");
        await Assert.That(p.QueuedStopDepth).IsEqualTo(0);

        // Every active-launch token — the in-flight one AND the synthesized one — was retired.
        await Assert.That(p.ActiveLaunchIdCountForTest).IsEqualTo(0);
        await Assert.That(p.IsActiveLaunchTargetForTest("queued-launch")).IsFalse();
        await Assert.That(p.IsActiveLaunchTargetForTest("gate")).IsFalse();

        // And a post-shutdown submission is refused rather than silently queued into a dead lane.
        await Assert.That(p.SubmitUnsequenced(h.Unseq(UnsequencedKind.Stop, "victim", "late")))
            .IsEqualTo(SubmitOutcome.Refused);
    }

    [Test]
    public async Task A_shutdown_synthesized_done_task_completes_exactly_once() {
        var h = new Harness();
        var p = h.P();
        using var park = new Park();

        p.SubmitUnsequenced(h.Unseq(UnsequencedKind.Launch, "gate", "gate", "launch", park.RunAsync));
        await WaitBounded(park.Started.Task, "the gate launch must be dequeued and running before shutdown begins");

        var seqDone = p.SubmitAsync(Harness.SeqStop(1, "queued"), () => Task.FromResult(new CommandOutcome(CommandOutcomeKind.StopExecuted)));

        // Close the lane BEFORE releasing the in-flight item — see the note in the test above.
        var disposing = p.DisposeAsync().AsTask();
        park.Release();
        await WaitBounded(disposing, "DisposeAsync hung");
        await WaitBounded(seqDone, "the shutdown-synthesized item's done-task never completed");

        // Exactly one terminal answer for that seq — a second completion would have thrown from SetResult,
        // and a second synthesis would have produced a second rejection.
        await Assert.That(h.Rejects.Count(r => r.Seq == 1)).IsEqualTo(1);
        await Assert.That(h.Acks.Count(a => a.Seq == 1)).IsEqualTo(0); // synthesis emits the rejection only
    }

    [Test]
    public async Task A_never_published_processor_still_disposes_instead_of_hanging_on_its_start_barrier() {
        var h = new Harness();
        var barrier = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var p = h.P(startBarrier: barrier.Task);
        h.Know("x");

        p.SubmitUnsequenced(h.Unseq(UnsequencedKind.Stop, "x", "never-runs"));
        await Assert.That(h.ExecOrder.ToArray()).IsEmpty(); // the barrier is holding the lane closed

        await WaitBounded(p.DisposeAsync().AsTask(), "DisposeAsync hung on an uncompleted start barrier");
        await Assert.That(h.ExecOrder.ToArray()).IsEmpty();
        await Assert.That(p.QueuedStopDepth).IsEqualTo(0);
    }

    [Test]
    public async Task The_lane_does_not_execute_its_first_item_until_the_start_barrier_completes() {
        var h = new Harness();
        var barrier = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var p = h.P(startBarrier: barrier.Task);
        h.Know("x");

        p.SubmitUnsequenced(h.Unseq(UnsequencedKind.Stop, "x", "first"));
        await Task.Delay(50);
        await Assert.That(h.ExecOrder.ToArray()).IsEmpty();

        barrier.SetResult();
        await WaitBounded(p.WhenIdleForTest(), "the lane never started after the barrier completed");
        await Assert.That(h.ExecOrder.ToArray()).IsEquivalentTo(new[] { "first" });
    }

    [Test]
    public async Task An_unsequenced_launch_refused_after_shutdown_never_reaches_the_lane() {
        var h = new Harness();
        var p = h.P();
        await p.DisposeAsync();

        await Assert.That(p.SubmitUnsequenced(h.Unseq(UnsequencedKind.Launch, "x", "late", "launch")))
            .IsEqualTo(SubmitOutcome.Refused);
        await Assert.That(h.ExecOrder.ToArray()).IsEmpty();
        await Assert.That(p.IsActiveLaunchTargetForTest("x")).IsFalse(); // a refusal mutates nothing
    }

    /// <summary>The stop-admission probe walks live orchestrator collections, so it can throw. Admitting is
    /// the safe direction: an un-sequenced stop for a target that turns out not to exist no-ops, whereas
    /// dropping one for a live agent would leave it running.</summary>
    [Test]
    public async Task A_throwing_stop_admission_probe_admits_rather_than_drops() {
        await using var p = new SequencedCommandProcessor(
            "e1", _ => AgentLiveness.Live, _ => Task.CompletedTask, _ => Task.CompletedTask,
            NullLogger.Instance, isKnownStopTarget: _ => throw new InvalidOperationException("registry read blew up"));

        var ran = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await Assert.That(p.SubmitUnsequenced(new UnsequencedItem(UnsequencedKind.Stop, "x", "stop",
            () => { ran.TrySetResult(); return Task.CompletedTask; }))).IsEqualTo(SubmitOutcome.Committed);
        await WaitBounded(ran.Task, "the admitted stop never executed");
    }
}
