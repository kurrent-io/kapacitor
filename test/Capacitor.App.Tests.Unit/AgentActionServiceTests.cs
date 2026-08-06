using System.Reactive.Subjects;
using Capacitor.App.Services;
using Capacitor.Cli.Core.LocalIpc;
using TUnit.Assertions.Enums;

namespace Capacitor.App.Tests.Unit;

/// Plain TUnit tests — AgentActionService is scheduler-free (a plain BehaviorSubject, no Avalonia
/// globals), so no AvaloniaSession is needed. Every async settle is driven by
/// ScriptedLocalControlOps's per-call TaskCompletionSource gates and WaitUntilAsync polling
/// (PauseControllerTests idiom) — never Task.Delay-based ordering.
public class AgentActionServiceTests {
    static AgentActionService NewService(
            ScriptedLocalControlOps ops, RecordingNotifier notifier, RecordingOpener opener,
            IObservable<DaemonStatusDto>? snapshots = null, CancellationToken shutdownToken = default) =>
        new(ops, notifier, opener, snapshots ?? new ReplaySubject<DaemonStatusDto>(1), shutdownToken);

    static async Task WaitUntilAsync(Func<bool> condition, TimeSpan? timeout = null, string what = "condition") {
        var deadline = DateTime.UtcNow + (timeout ?? TimeSpan.FromSeconds(5));
        while (!condition()) {
            if (DateTime.UtcNow > deadline) throw new TimeoutException($"Timed out waiting for: {what}");
            await Task.Delay(10);
        }
    }

    // ---- stop result mapping ----

    [Test]
    public async Task Stop_stopped_no_banner() {
        var ops = new ScriptedLocalControlOps();
        var notifier = new RecordingNotifier();
        var service = NewService(ops, notifier, new RecordingOpener());
        var states = new List<IReadOnlySet<string>>();
        using var sub = service.StopsInFlight.Subscribe(states.Add);

        ops.QueueStop(new StopAgentResult(true, "stopped", null));
        service.RequestStop("a", "agent-a");

        await WaitUntilAsync(() => states[^1].Count == 0 && states.Count >= 3, what: "stop to settle");
        await Assert.That(notifier.Notified).IsEmpty();
    }

    [Test]
    public async Task Stop_failed_banners() {
        var ops = new ScriptedLocalControlOps();
        var notifier = new RecordingNotifier();
        var service = NewService(ops, notifier, new RecordingOpener());

        ops.QueueStop(new StopAgentResult(false, "failed", null));
        service.RequestStop("a", "agent-a");

        await WaitUntilAsync(() => notifier.Notified.Count >= 1, what: "failed banner");
        await Assert.That(notifier.Notified).IsEquivalentTo(["Couldn't stop agent-a"], CollectionOrdering.Matching);
    }

    [Test]
    public async Task Stop_skipped_banners() {
        var ops = new ScriptedLocalControlOps();
        var notifier = new RecordingNotifier();
        var service = NewService(ops, notifier, new RecordingOpener());

        ops.QueueStop(new StopAgentResult(false, "skipped", null));
        service.RequestStop("a", "agent-a");

        await WaitUntilAsync(() => notifier.Notified.Count >= 1, what: "skipped banner");
        await Assert.That(notifier.Notified).IsEquivalentTo(["The daemon declined to stop agent-a"], CollectionOrdering.Matching);
    }

    [Test]
    public async Task Stop_error_banners_daemon_text_verbatim() {
        var ops = new ScriptedLocalControlOps();
        var notifier = new RecordingNotifier();
        var service = NewService(ops, notifier, new RecordingOpener());

        ops.QueueStop(new StopAgentResult(false, "error", "cannot stop a protected review participant without --force"));
        service.RequestStop("a", "agent-a");

        await WaitUntilAsync(() => notifier.Notified.Count >= 1, what: "error banner");
        await Assert.That(notifier.Notified).IsEquivalentTo(
            ["cannot stop a protected review participant without --force"], CollectionOrdering.Matching);
    }

    [Test]
    public async Task Stop_daemon_unreachable_maps_to_neutral_copy() {
        var ops = new ScriptedLocalControlOps();
        var notifier = new RecordingNotifier();
        var service = NewService(ops, notifier, new RecordingOpener());

        ops.QueueStopFailure("daemon_unreachable");
        service.RequestStop("a", "agent-a");

        await WaitUntilAsync(() => notifier.Notified.Count >= 1, what: "unreachable banner");
        await Assert.That(notifier.Notified).IsEquivalentTo(["The daemon is not reachable"], CollectionOrdering.Matching);
    }

    [Test]
    public async Task Stop_other_reason_banners_with_message() {
        var ops = new ScriptedLocalControlOps();
        var notifier = new RecordingNotifier();
        var service = NewService(ops, notifier, new RecordingOpener());

        ops.QueueStopFailure("unexpected_reply");
        service.RequestStop("a", "agent-a");

        await WaitUntilAsync(() => notifier.Notified.Count >= 1, what: "unmapped-reason banner");
        await Assert.That(notifier.Notified).IsEquivalentTo(["Couldn't stop agent-a: unexpected_reply"], CollectionOrdering.Matching);
    }

    [Test]
    public async Task Stop_operation_canceled_is_quiet() {
        var ops = new ScriptedLocalControlOps();
        var notifier = new RecordingNotifier();
        var cts = new CancellationTokenSource();
        var service = NewService(ops, notifier, new RecordingOpener(), shutdownToken: cts.Token);
        var states = new List<IReadOnlySet<string>>();
        using var sub = service.StopsInFlight.Subscribe(states.Add);

        var gate = ops.ArmStop();
        service.RequestStop("a", "agent-a");
        await WaitUntilAsync(() => ops.StopCalls >= 1, what: "stop call issued");

        cts.Cancel(); // fires the registered callback synchronously, cancelling the held call

        await WaitUntilAsync(() => states[^1].Count == 0, what: "in-flight cleared after cancellation");
        await Assert.That(notifier.Notified).IsEmpty();
    }

    // An unmapped exception must banner (neutral copy) AND still clean up the in-flight entry,
    // never wedging a later request for the same id.
    [Test]
    public async Task Stop_unmapped_exception_banners_and_clears_inflight() {
        var ops = new ScriptedLocalControlOps();
        var notifier = new RecordingNotifier();
        var service = NewService(ops, notifier, new RecordingOpener());
        var states = new List<IReadOnlySet<string>>();
        using var sub = service.StopsInFlight.Subscribe(states.Add);

        ops.QueueStopUnmappedFailure(new InvalidOperationException("boom"));
        service.RequestStop("a", "agent-a");

        await WaitUntilAsync(() => states[^1].Count == 0 && states.Count >= 3, what: "stop to settle after unmapped exception");
        await Assert.That(notifier.Notified).IsEquivalentTo(["Couldn't stop agent-a: boom"], CollectionOrdering.Matching);

        // Lane-freed proof: a subsequent stop for the SAME id is accepted, not dropped forever.
        ops.QueueStop(new StopAgentResult(true, "stopped", null));
        service.RequestStop("a", "agent-a");
        await WaitUntilAsync(() => ops.StopCalls >= 2, what: "second stop accepted");
    }

    // ---- in-flight gating ----

    [Test]
    public async Task Second_request_same_id_while_inflight_is_noop() {
        var ops = new ScriptedLocalControlOps();
        var notifier = new RecordingNotifier();
        var service = NewService(ops, notifier, new RecordingOpener());
        var states = new List<IReadOnlySet<string>>();
        using var sub = service.StopsInFlight.Subscribe(states.Add);

        var gate = ops.ArmStop();
        service.RequestStop("a", "agent-a");
        await WaitUntilAsync(() => ops.StopCalls >= 1, what: "first stop issued");

        service.RequestStop("a", "agent-a"); // already in flight: no-op — no second call, no second push
        await Assert.That(ops.StopCalls).IsEqualTo(1);
        await Assert.That(states.Count).IsEqualTo(2); // seed(empty) + add("a") only

        gate.SetResult(new StopAgentResult(true, "stopped", null));
        await WaitUntilAsync(() => states[^1].Count == 0, what: "in-flight cleared");
        await Assert.That(ops.StopCalls).IsEqualTo(1); // exactly one call ever issued
    }

    [Test]
    public async Task Different_ids_run_concurrently() {
        var ops = new ScriptedLocalControlOps();
        var notifier = new RecordingNotifier();
        var service = NewService(ops, notifier, new RecordingOpener());
        var states = new List<IReadOnlySet<string>>();
        using var sub = service.StopsInFlight.Subscribe(states.Add);

        var gateA = ops.ArmStop();
        var gateB = ops.ArmStop();
        service.RequestStop("a", "agent-a");
        service.RequestStop("b", "agent-b");

        await WaitUntilAsync(() => ops.StopCalls >= 2, what: "both stops issued");
        await WaitUntilAsync(() => states[^1].Contains("a") && states[^1].Contains("b"), what: "both in flight");

        gateA.SetResult(new StopAgentResult(true, "stopped", null));
        gateB.SetResult(new StopAgentResult(true, "stopped", null));

        await WaitUntilAsync(() => states[^1].Count == 0, what: "both cleared");
        await Assert.That(notifier.Notified).IsEmpty();
    }

    [Test]
    public async Task StopsInFlight_observable_emits_add_then_remove() {
        var ops = new ScriptedLocalControlOps();
        var notifier = new RecordingNotifier();
        var service = NewService(ops, notifier, new RecordingOpener());
        var states = new List<IReadOnlySet<string>>();
        using var sub = service.StopsInFlight.Subscribe(states.Add);

        await Assert.That(states.Count).IsEqualTo(1);
        await Assert.That(states[0]).IsEmpty();

        var gate = ops.ArmStop();
        service.RequestStop("a", "agent-a");
        await WaitUntilAsync(() => states.Count >= 2, what: "add pushed");
        await Assert.That(states[1]).IsEquivalentTo(["a"], CollectionOrdering.Matching);

        gate.SetResult(new StopAgentResult(true, "stopped", null));
        await WaitUntilAsync(() => states.Count >= 3, what: "remove pushed");
        await Assert.That(states[2]).IsEmpty();
    }

    // ---- open in web ----

    [Test]
    public async Task OpenInWeb_builds_exact_url_and_escapes_id() {
        var ops = new ScriptedLocalControlOps();
        var notifier = new RecordingNotifier();
        var opener = new RecordingOpener();
        var snapshots = new ReplaySubject<DaemonStatusDto>(1);
        var service = NewService(ops, notifier, opener, snapshots);

        snapshots.OnNext(FakeDaemonClientService.Snap(serverUrl: "https://x.kcap.ai/"));

        service.OpenInWeb("a/b");

        await Assert.That(opener.Opened).IsEquivalentTo(["https://x.kcap.ai/agents/a%2Fb"], CollectionOrdering.Matching);
        await Assert.That(notifier.Notified).IsEmpty();
    }

    [Test]
    public async Task OpenInWeb_without_snapshot_notifies_and_returns() {
        var ops = new ScriptedLocalControlOps();
        var notifier = new RecordingNotifier();
        var opener = new RecordingOpener();
        var service = NewService(ops, notifier, opener);

        service.OpenInWeb("a");

        await Assert.That(opener.Opened).IsEmpty();
        await Assert.That(notifier.Notified).IsEquivalentTo(["Not connected to a daemon yet"], CollectionOrdering.Matching);
    }

    [Test]
    public async Task OpenInWeb_opener_throw_banners() {
        var ops = new ScriptedLocalControlOps();
        var notifier = new RecordingNotifier();
        var opener = new RecordingOpener { ThrowOnOpen = new InvalidOperationException("no handler registered") };
        var snapshots = new ReplaySubject<DaemonStatusDto>(1);
        var service = NewService(ops, notifier, opener, snapshots);
        snapshots.OnNext(FakeDaemonClientService.Snap(serverUrl: "https://x.kcap.ai"));

        service.OpenInWeb("a");

        await Assert.That(notifier.Notified).IsEquivalentTo(
            ["Couldn't open the browser: no handler registered"], CollectionOrdering.Matching);
    }
}
