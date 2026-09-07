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
            IObservable<DaemonStatusDto>? snapshots = null, CancellationToken shutdownToken = default,
            Func<string, Task<bool>>? confirmForceStop = null, string? fallbackServerUrl = null) =>
        new(ops, notifier, opener, snapshots ?? new ReplaySubject<DaemonStatusDto>(1), shutdownToken,
            confirmForceStop ?? NeverConfirm.Confirm, fallbackServerUrl);

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
        var states = new StopStateRecorder();
        using var sub = service.StopsInFlight.Subscribe(states.Add);

        ops.QueueStop(new StopAgentResult(true, "stopped", null));
        service.RequestStop("a", "agent-a", "agent");

        await WaitUntilAsync(() => states[^1].Count == 0 && states.Count >= 3, what: "stop to settle");
        await Assert.That(notifier.Notified).IsEmpty();
    }

    [Test]
    public async Task Stop_failed_banners() {
        var ops = new ScriptedLocalControlOps();
        var notifier = new RecordingNotifier();
        var service = NewService(ops, notifier, new RecordingOpener());

        ops.QueueStop(new StopAgentResult(false, "failed", null));
        service.RequestStop("a", "agent-a", "agent");

        await WaitUntilAsync(() => notifier.Notified.Count >= 1, what: "failed banner");
        await Assert.That(notifier.Notified).IsEquivalentTo(["Couldn't stop agent-a"], CollectionOrdering.Matching);
    }

    [Test]
    public async Task Stop_skipped_banners() {
        var ops = new ScriptedLocalControlOps();
        var notifier = new RecordingNotifier();
        var service = NewService(ops, notifier, new RecordingOpener());

        ops.QueueStop(new StopAgentResult(false, "skipped", null));
        service.RequestStop("a", "agent-a", "agent");

        await WaitUntilAsync(() => notifier.Notified.Count >= 1, what: "skipped banner");
        await Assert.That(notifier.Notified).IsEquivalentTo(["The daemon declined to stop agent-a"], CollectionOrdering.Matching);
    }

    // §7 change: the app never surfaces the daemon's Error text verbatim anymore — it becomes a
    // generic toast, and the full daemon text (which may name an id or "Pass --force…" CLI-speak
    // the app can't act on) goes to stderr only.
    [Test]
    public async Task Stop_error_banners_generic_copy_not_daemon_text() {
        var ops = new ScriptedLocalControlOps();
        var notifier = new RecordingNotifier();
        var service = NewService(ops, notifier, new RecordingOpener());

        ops.QueueStop(new StopAgentResult(false, "error", "agent-a is a review agent. Pass --force to stop it anyway."));
        service.RequestStop("a", "agent-a", "agent");

        await WaitUntilAsync(() => notifier.Notified.Count >= 1, what: "error banner");
        await Assert.That(notifier.Notified).IsEquivalentTo(["Couldn't stop agent-a"], CollectionOrdering.Matching);
        await Assert.That(notifier.Notified[0].Contains("--force")).IsFalse();
    }

    [Test]
    public async Task Stop_daemon_unreachable_maps_to_neutral_copy() {
        var ops = new ScriptedLocalControlOps();
        var notifier = new RecordingNotifier();
        var service = NewService(ops, notifier, new RecordingOpener());

        ops.QueueStopFailure("daemon_unreachable");
        service.RequestStop("a", "agent-a", "agent");

        await WaitUntilAsync(() => notifier.Notified.Count >= 1, what: "unreachable banner");
        await Assert.That(notifier.Notified).IsEquivalentTo(["The daemon is not reachable"], CollectionOrdering.Matching);
    }

    [Test]
    public async Task Stop_other_reason_banners_with_message() {
        var ops = new ScriptedLocalControlOps();
        var notifier = new RecordingNotifier();
        var service = NewService(ops, notifier, new RecordingOpener());

        ops.QueueStopFailure("unexpected_reply");
        service.RequestStop("a", "agent-a", "agent");

        await WaitUntilAsync(() => notifier.Notified.Count >= 1, what: "unmapped-reason banner");
        await Assert.That(notifier.Notified).IsEquivalentTo(["Couldn't stop agent-a: unexpected_reply"], CollectionOrdering.Matching);
    }

    [Test]
    public async Task Stop_operation_canceled_is_quiet() {
        var ops = new ScriptedLocalControlOps();
        var notifier = new RecordingNotifier();
        var cts = new CancellationTokenSource();
        var service = NewService(ops, notifier, new RecordingOpener(), shutdownToken: cts.Token);
        var states = new StopStateRecorder();
        using var sub = service.StopsInFlight.Subscribe(states.Add);

        var gate = ops.ArmStop();
        service.RequestStop("a", "agent-a", "agent");
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
        var states = new StopStateRecorder();
        using var sub = service.StopsInFlight.Subscribe(states.Add);

        ops.QueueStopUnmappedFailure(new InvalidOperationException("boom"));
        service.RequestStop("a", "agent-a", "agent");

        await WaitUntilAsync(() => states[^1].Count == 0 && states.Count >= 3, what: "stop to settle after unmapped exception");
        await Assert.That(notifier.Notified).IsEquivalentTo(["Couldn't stop agent-a: boom"], CollectionOrdering.Matching);

        // Lane-freed proof: a subsequent stop for the SAME id is accepted, not dropped forever.
        ops.QueueStop(new StopAgentResult(true, "stopped", null));
        service.RequestStop("a", "agent-a", "agent");
        await WaitUntilAsync(() => ops.StopCalls >= 2, what: "second stop accepted");
    }

    // ---- in-flight gating ----

    [Test]
    public async Task Second_request_same_id_while_inflight_is_noop() {
        var ops = new ScriptedLocalControlOps();
        var notifier = new RecordingNotifier();
        var service = NewService(ops, notifier, new RecordingOpener());
        var states = new StopStateRecorder();
        using var sub = service.StopsInFlight.Subscribe(states.Add);

        var gate = ops.ArmStop();
        service.RequestStop("a", "agent-a", "agent");
        await WaitUntilAsync(() => ops.StopCalls >= 1, what: "first stop issued");

        service.RequestStop("a", "agent-a", "agent"); // already in flight: no-op — no second call, no second push
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
        var states = new StopStateRecorder();
        using var sub = service.StopsInFlight.Subscribe(states.Add);

        var gateA = ops.ArmStop();
        var gateB = ops.ArmStop();
        service.RequestStop("a", "agent-a", "agent");
        service.RequestStop("b", "agent-b", "agent");

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
        var states = new StopStateRecorder();
        using var sub = service.StopsInFlight.Subscribe(states.Add);

        await Assert.That(states.Count).IsEqualTo(1);
        await Assert.That(states[0]).IsEmpty();

        var gate = ops.ArmStop();
        service.RequestStop("a", "agent-a", "agent");
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

    // ---- open in web: local vs remote origin (finding 5) ----

    [Test]
    public async Task OpenInWeb_and_OpenInWebRemote_use_different_servers() {
        var ops = new ScriptedLocalControlOps();
        var notifier = new RecordingNotifier();
        var opener = new RecordingOpener();
        var snapshots = new ReplaySubject<DaemonStatusDto>(1);
        var service = NewService(ops, notifier, opener, snapshots, fallbackServerUrl: "https://a.kcap.ai");

        snapshots.OnNext(FakeDaemonClientService.Snap(serverUrl: "https://b.kcap.ai"));

        service.OpenInWeb("a1");
        service.OpenInWebRemote("a1");

        await Assert.That(opener.Opened).IsEquivalentTo(
            ["https://b.kcap.ai/agents/a1", "https://a.kcap.ai/agents/a1"], CollectionOrdering.Matching);
        await Assert.That(notifier.Notified).IsEmpty();
    }

    [Test]
    public async Task OpenInWebRemote_without_a_profile_notifies_and_returns() {
        var ops = new ScriptedLocalControlOps();
        var notifier = new RecordingNotifier();
        var opener = new RecordingOpener();
        var service = NewService(ops, notifier, opener);

        service.OpenInWebRemote("a1");

        await Assert.That(opener.Opened).IsEmpty();
        await Assert.That(notifier.Notified).IsEquivalentTo(["Not signed in to a server"], CollectionOrdering.Matching);
    }

    // ---- confirm-then-force for protected kinds (decision 5) ----

    [Test]
    public async Task Protected_kind_confirm_true_stops_with_force() {
        var ops = new ScriptedLocalControlOps();
        var notifier = new RecordingNotifier();
        var confirmer = new RecordingConfirmer();
        var service = NewService(ops, notifier, new RecordingOpener(), confirmForceStop: confirmer.Confirm);

        confirmer.Queue(true);
        ops.QueueStop(new StopAgentResult(true, "stopped", null));
        service.RequestStop("a", "agent-a", "review");

        await WaitUntilAsync(() => ops.StopCalls >= 1, what: "stop issued after confirm");
        await Assert.That(confirmer.Prompted).IsEquivalentTo(["agent-a"], CollectionOrdering.Matching);
        await Assert.That(ops.StopPayloads).IsEquivalentTo([("a", true)], CollectionOrdering.Matching);
        await Assert.That(notifier.Notified).IsEmpty();
    }

    [Test]
    public async Task Protected_kind_confirm_false_no_ops_call_clears_inflight_no_toast() {
        var ops = new ScriptedLocalControlOps();
        var notifier = new RecordingNotifier();
        var confirmer = new RecordingConfirmer();
        var service = NewService(ops, notifier, new RecordingOpener(), confirmForceStop: confirmer.Confirm);
        var states = new StopStateRecorder();
        using var sub = service.StopsInFlight.Subscribe(states.Add);

        confirmer.Queue(false);
        service.RequestStop("a", "agent-a", "review");

        await WaitUntilAsync(() => states[^1].Count == 0, what: "in-flight cleared after cancelled confirm");
        await Assert.That(confirmer.Prompted).IsEquivalentTo(["agent-a"], CollectionOrdering.Matching);
        await Assert.That(ops.StopCalls).IsEqualTo(0);
        await Assert.That(notifier.Notified).IsEmpty();
    }

    [Test]
    public async Task Non_protected_kind_stops_with_force_false_and_never_invokes_confirm_seam() {
        var ops = new ScriptedLocalControlOps();
        var notifier = new RecordingNotifier();
        // NeverConfirm (NewService's default): throws if invoked — a passing test IS the proof
        // the confirm seam was never reached for kind "agent".
        var service = NewService(ops, notifier, new RecordingOpener());

        ops.QueueStop(new StopAgentResult(true, "stopped", null));
        service.RequestStop("a", "agent-a", "agent");

        await WaitUntilAsync(() => ops.StopCalls >= 1, what: "stop issued");
        await Assert.That(ops.StopPayloads).IsEquivalentTo([("a", false)], CollectionOrdering.Matching);
        await Assert.That(notifier.Notified).IsEmpty();
    }

    // Fail-safe (spec: "unknown kind token → treated protected"), mirroring the daemon's own
    // Kind != LaunchKind.Default check and the CLI's IsProtectedKind — an unrecognised KindText
    // must never read as stoppable without confirmation.
    [Test]
    public async Task Unknown_kind_token_is_treated_as_protected() {
        var ops = new ScriptedLocalControlOps();
        var notifier = new RecordingNotifier();
        var confirmer = new RecordingConfirmer();
        var service = NewService(ops, notifier, new RecordingOpener(), confirmForceStop: confirmer.Confirm);

        confirmer.Queue(true);
        ops.QueueStop(new StopAgentResult(true, "stopped", null));
        service.RequestStop("a", "agent-a", "SomeFutureKind");

        await WaitUntilAsync(() => ops.StopCalls >= 1, what: "stop issued after confirm");
        await Assert.That(confirmer.Prompted).IsEquivalentTo(["agent-a"], CollectionOrdering.Matching);
        await Assert.That(ops.StopPayloads).IsEquivalentTo([("a", true)], CollectionOrdering.Matching);
    }

    // A second RequestStop for the same id while the confirmation dialog is still open (the
    // confirm Task not yet resolved) must no-op — the id has been in-flight since the FIRST
    // RequestStop's synchronous lock, before the dialog was ever awaited.
    [Test]
    public async Task Second_request_while_confirm_dialog_open_is_noop() {
        var ops = new ScriptedLocalControlOps();
        var notifier = new RecordingNotifier();
        var confirmer = new RecordingConfirmer();
        var service = NewService(ops, notifier, new RecordingOpener(), confirmForceStop: confirmer.Confirm);
        var states = new StopStateRecorder();
        using var sub = service.StopsInFlight.Subscribe(states.Add);

        var gate = confirmer.Arm();
        service.RequestStop("a", "agent-a", "review");
        await WaitUntilAsync(() => confirmer.Prompted.Count >= 1, what: "dialog opened");

        service.RequestStop("a", "agent-a", "review"); // dialog still open: no-op
        await Assert.That(confirmer.Prompted.Count).IsEqualTo(1); // never prompted a second time
        await Assert.That(states.Count).IsEqualTo(2); // seed(empty) + add("a") only

        ops.QueueStop(new StopAgentResult(true, "stopped", null));
        gate.SetResult(true);
        await WaitUntilAsync(() => states[^1].Count == 0, what: "in-flight cleared");
        await Assert.That(ops.StopCalls).IsEqualTo(1); // exactly one call ever issued
    }
}

/// Thread-safe collector for StopsInFlight pushes. AgentActionService.RequestStop pushes
/// synchronously on the test thread, while RunStopAsync's completion pushes on a threadpool
/// thread — BehaviorSubject invokes subscribers on whichever thread calls OnNext. A plain
/// List&lt;T&gt; written from both threads while the test thread polls Count/[^1] tears under a
/// concurrent Add during an internal array resize and NREs on read. Every access takes one lock.
sealed class StopStateRecorder {
    readonly Lock _gate = new();
    readonly List<IReadOnlySet<string>> _states = [];

    public void Add(IReadOnlySet<string> state) { lock (_gate) _states.Add(state); }
    public int Count { get { lock (_gate) return _states.Count; } }
    public IReadOnlySet<string> this[Index index] { get { lock (_gate) return _states[index]; } }
}
