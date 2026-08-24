using System.Reactive.Subjects;
using System.Reactive.Threading.Tasks;
using Avalonia.Media;
using Capacitor.App.Services;
using Capacitor.App.ViewModels;
using static Capacitor.App.Tests.Unit.FakeDaemonClientService;

namespace Capacitor.App.Tests.Unit;

/// Scripted IDaemonClientService fake (FakeDaemonClientService) — subject-backed
/// Status/Snapshots/Agents — so VM tests drive exact event sequences without a real daemon.
/// All tests here touch RxSchedulers (the VM's WhenActivated projections use
/// RxSchedulers.MainThreadScheduler), so every test runs inside
/// AvaloniaSession.WithImmediateRxScheduler and carries [NotInParallel("AvaloniaSession")].
public class MainWindowViewModelTests {
    // Real AppNotifier (not RecordingNotifier) — none of these tests exercise notifications, and
    // the toast overlay is a View concern MainWindowViewModel no longer touches (spec §11); the
    // production notifier is fine here, kept only because AgentActionService requires one.
    static (AgentActionService Actions, IAppNotifier Notifier) NewActions(FakeDaemonClientService service) {
        var notifier = new AppNotifier();
        var actions = new AgentActionService(new ScriptedLocalControlOps(), notifier, new RecordingOpener(), service.SnapshotsSubject, CancellationToken.None, NeverConfirm.Confirm);
        return (actions, notifier);
    }

    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task Projections_follow_the_snapshot() {
        await AvaloniaSession.WithImmediateRxScheduler(async () => {
            var service = new FakeDaemonClientService();
            var (actions, _) = NewActions(service);
            var vm = new MainWindowViewModel(service, actions, new FakeTicker(), CancellationToken.None, TestActivity.New());
            using var activation = vm.Activator.Activate();

            service.SnapshotsSubject.OnNext(Snap(daemon: "daemon-a", version: "1.2.3", serverUrl: "http://localhost:9999", connection: "connected"));
            service.StatusSubject.OnNext(new AttachStatus(AttachState.Connected, null, null));

            await Assert.That(vm.DaemonName).IsEqualTo("daemon-a");
            await Assert.That(vm.DaemonVersion).IsEqualTo("1.2.3");
            await Assert.That(vm.ServerUrl).IsEqualTo("http://localhost:9999");
            await Assert.That(vm.ConnectionText).IsEqualTo("connected"); // raw wire value, unchanged
            await Assert.That(vm.ConnectionDisplay).IsEqualTo("Connected"); // new presentation projection
        });
    }

    // ---- VersionDisplay (spec: SEMVER only, everything from the first '+' is build metadata) ----

    [Test]
    [Arguments("1.2.3+abc", "1.2.3")]
    [Arguments("1.2.3", "1.2.3")]
    [Arguments("1.2.3+a.b+c", "1.2.3")] // only the FIRST '+' matters
    [Arguments("", "")]
    [Arguments(null, "")]
    public async Task VersionDisplay_strips_build_metadata(string? raw, string expected) {
        await Assert.That(MainWindowViewModel.StripBuildMetadata(raw)).IsEqualTo(expected);
    }

    // ---- ConnectionDisplay / StatusDotBrush (local attach State first, daemon Connection only
    // once Connected — see MainWindowViewModel.ConnectionDisplayFor's doc comment) ----

    [Test]
    [Arguments(AttachState.Connecting, null, "connected", "Connecting…")]
    [Arguments(AttachState.Unreachable, "daemon_unreachable", "connected", "Unreachable")]
    [Arguments(AttachState.Unreachable, "daemon_incompatible", "connected", "Incompatible")]
    [Arguments(AttachState.Connected, null, "connected", "Connected")]
    [Arguments(AttachState.Connected, null, "connecting", "Connecting…")]
    [Arguments(AttachState.Connected, null, "reconnecting", "Reconnecting…")]
    [Arguments(AttachState.Connected, null, "disconnected", "Disconnected")]
    public async Task ConnectionDisplayFor_maps_state_and_daemon_connection_to_one_capitalized_word(
            AttachState state, string? reason, string daemonConnection, string expected) {
        var status = new AttachStatus(state, reason, null);
        await Assert.That(MainWindowViewModel.ConnectionDisplayFor(status, daemonConnection)).IsEqualTo(expected);
    }

    [Test]
    [Arguments(AttachState.Connecting, null, "connected", "#FFB300")]
    [Arguments(AttachState.Unreachable, "daemon_unreachable", "connected", "#9E9E9E")]
    [Arguments(AttachState.Unreachable, "daemon_incompatible", "connected", "#E53935")]
    [Arguments(AttachState.Connected, null, "connected", "#4CAF50")]
    [Arguments(AttachState.Connected, null, "connecting", "#FFB300")]
    [Arguments(AttachState.Connected, null, "reconnecting", "#FFB300")]
    [Arguments(AttachState.Connected, null, "disconnected", "#E53935")]
    public async Task StatusDotFor_maps_to_the_matching_bucket_color(
            AttachState state, string? reason, string daemonConnection, string expectedHex) {
        var status = new AttachStatus(state, reason, null);
        var brush = (SolidColorBrush)MainWindowViewModel.StatusDotFor(status, daemonConnection);
        await Assert.That(brush.Color).IsEqualTo(Color.Parse(expectedHex));
    }

    // ---- Start/Retry visibility (spec: shows ONLY when the action is meaningful, tracking the
    // SAME state predicate as canStart/canRetry — but, unlike CanExecute, never hidden just
    // because a start is in flight) ----

    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task Start_and_retry_visibility_track_the_command_state_matrix() {
        await AvaloniaSession.WithImmediateRxScheduler(async () => {
            var service = new FakeDaemonClientService();
            var (actions, _) = NewActions(service);
            var vm = new MainWindowViewModel(service, actions, new FakeTicker(), CancellationToken.None, TestActivity.New());

            foreach (var reason in new[] { "daemon_unreachable", "daemon_incompatible" }) {
                service.StatusSubject.OnNext(new AttachStatus(AttachState.Connecting, null, null));
                await Assert.That(vm.StartVisible).IsFalse();
                await Assert.That(vm.RetryVisible).IsTrue();

                service.StatusSubject.OnNext(new AttachStatus(AttachState.Connected, null, null));
                await Assert.That(vm.StartVisible).IsFalse();
                await Assert.That(vm.RetryVisible).IsFalse();

                service.StatusSubject.OnNext(new AttachStatus(AttachState.Unreachable, reason, null));
                await Assert.That(vm.StartVisible).IsEqualTo(reason == "daemon_unreachable");
                await Assert.That(vm.RetryVisible).IsTrue();
            }
        });
    }

    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task StartVisible_stays_true_while_a_start_is_in_flight_unlike_CanExecute() {
        await AvaloniaSession.WithImmediateRxScheduler(async () => {
            var service = new FakeDaemonClientService();
            var (actions, _) = NewActions(service);
            var vm = new MainWindowViewModel(service, actions, new FakeTicker(), CancellationToken.None, TestActivity.New());

            service.StatusSubject.OnNext(new AttachStatus(AttachState.Unreachable, "daemon_unreachable", null));

            var startCanExecute = false;
            using var subStart = vm.StartDaemonCommand.CanExecute.Subscribe(v => startCanExecute = v);

            var gate = new TaskCompletionSource();
            service.StartBehavior = async _ => {
                await gate.Task;
                return new StartDaemonResult(true, null);
            };

            var execute = vm.StartDaemonCommand.Execute().ToTask();
            await Assert.That(startCanExecute).IsFalse(); // command disabled while in flight...
            await Assert.That(vm.StartVisible).IsTrue();  // ...but the button itself stays visible

            gate.SetResult();
            await execute;
        });
    }

    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task Agent_count_renders_only_while_connected() {
        await AvaloniaSession.WithImmediateRxScheduler(async () => {
            var service = new FakeDaemonClientService();
            var (actions, _) = NewActions(service);
            var vm = new MainWindowViewModel(service, actions, new FakeTicker(), CancellationToken.None, TestActivity.New());
            using var activation = vm.Activator.Activate();

            service.SnapshotsSubject.OnNext(Snap(active: 2, max: 5));
            service.StatusSubject.OnNext(new AttachStatus(AttachState.Connected, null, null));
            await Assert.That(vm.AgentCountText).IsEqualTo("2 of 5 agents");

            // Retention is the SERVICE's concern (spec §5) — the fake never clears its snapshot
            // on disconnect either; the VM merely stops RENDERING the count.
            service.StatusSubject.OnNext(new AttachStatus(AttachState.Unreachable, "daemon_unreachable", null));
            await Assert.That(vm.AgentCountText).IsEqualTo("—");
        });
    }

    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task Command_enablement_matrix() {
        await AvaloniaSession.WithImmediateRxScheduler(async () => {
            var service = new FakeDaemonClientService();
            var (actions, _) = NewActions(service);
            var vm = new MainWindowViewModel(service, actions, new FakeTicker(), CancellationToken.None, TestActivity.New());

            var startCanExecute = false;
            var retryCanExecute = false;
            using var subStart = vm.StartDaemonCommand.CanExecute.Subscribe(v => startCanExecute = v);
            using var subRetry = vm.RetryCommand.CanExecute.Subscribe(v => retryCanExecute = v);

            foreach (var reason in new[] { "daemon_unreachable", "daemon_incompatible" }) {
                service.StatusSubject.OnNext(new AttachStatus(AttachState.Connecting, null, null));
                await Assert.That(startCanExecute).IsFalse();
                await Assert.That(retryCanExecute).IsTrue();

                service.StatusSubject.OnNext(new AttachStatus(AttachState.Connected, null, null));
                await Assert.That(startCanExecute).IsFalse();
                await Assert.That(retryCanExecute).IsFalse();

                service.StatusSubject.OnNext(new AttachStatus(AttachState.Unreachable, reason, null));
                await Assert.That(startCanExecute).IsEqualTo(reason == "daemon_unreachable");
                await Assert.That(retryCanExecute).IsTrue();
            }

            // In-flight: an outstanding start disables StartDaemonCommand even though the
            // status itself keeps satisfying (Unreachable, daemon_unreachable) throughout —
            // ReactiveCommand's own CanExecute ANDs in "not currently executing".
            service.StatusSubject.OnNext(new AttachStatus(AttachState.Unreachable, "daemon_unreachable", null));
            await Assert.That(startCanExecute).IsTrue();

            var gate = new TaskCompletionSource();
            service.StartBehavior = async _ => {
                await gate.Task;
                return new StartDaemonResult(true, null);
            };

            var execute = vm.StartDaemonCommand.Execute().ToTask();
            await Assert.That(startCanExecute).IsFalse(); // in flight

            gate.SetResult();
            await execute;
            await Assert.That(startCanExecute).IsTrue(); // status unchanged, attempt finished
        });
    }

    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task Incompatible_renders_neutral_skew_message() {
        await AvaloniaSession.WithImmediateRxScheduler(async () => {
            var service = new FakeDaemonClientService();
            var (actions, _) = NewActions(service);
            var vm = new MainWindowViewModel(service, actions, new FakeTicker(), CancellationToken.None, TestActivity.New());
            using var activation = vm.Activator.Activate();

            var startCanExecute = false;
            var retryCanExecute = false;
            using var subStart = vm.StartDaemonCommand.CanExecute.Subscribe(v => startCanExecute = v);
            using var subRetry = vm.RetryCommand.CanExecute.Subscribe(v => retryCanExecute = v);

            service.StatusSubject.OnNext(new AttachStatus(AttachState.Unreachable, "daemon_incompatible", null));

            await Assert.That(vm.Reason).IsNotNull();
            await Assert.That(vm.Reason!).Contains("app and daemon are incompatible — make sure both are up to date");
            await Assert.That(startCanExecute).IsFalse();
            await Assert.That(retryCanExecute).IsTrue();
        });
    }

    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task Start_message_lifecycle() {
        await AvaloniaSession.WithImmediateRxScheduler(async () => {
            var service = new FakeDaemonClientService();
            var (actions, _) = NewActions(service);
            var vm = new MainWindowViewModel(service, actions, new FakeTicker(), CancellationToken.None, TestActivity.New());
            using var activation = vm.Activator.Activate();

            service.StatusSubject.OnNext(new AttachStatus(AttachState.Unreachable, "daemon_unreachable", null));

            service.StartBehavior = _ => Task.FromResult(new StartDaemonResult(false, "boom: could not bind socket"));
            await vm.StartDaemonCommand.Execute().ToTask();
            await Assert.That(vm.StartMessage).IsEqualTo("boom: could not bind socket");

            // A new attempt clears the previous failure message SYNCHRONOUSLY, before the
            // attempt's own async work resolves — proven here by asserting it's already null
            // while the gated fake is still pending.
            var gate = new TaskCompletionSource();
            service.StartBehavior = async _ => {
                await gate.Task;
                return new StartDaemonResult(true, null);
            };
            var execute = vm.StartDaemonCommand.Execute().ToTask();
            await Assert.That(vm.StartMessage).IsNull();
            gate.SetResult();
            await execute;

            // A transition to Connected clears it too.
            service.StartBehavior = _ => Task.FromResult(new StartDaemonResult(false, "second failure"));
            service.StatusSubject.OnNext(new AttachStatus(AttachState.Unreachable, "daemon_unreachable", null));
            await vm.StartDaemonCommand.Execute().ToTask();
            await Assert.That(vm.StartMessage).IsEqualTo("second failure");

            service.StatusSubject.OnNext(new AttachStatus(AttachState.Connected, null, null));
            await Assert.That(vm.StartMessage).IsNull();
        });
    }

    // spec §6: ILifecycleSurface.Status one-liners ride the same StartMessage lane.
    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task Lifecycle_status_sets_and_is_cleared_like_a_start_failure() {
        await AvaloniaSession.WithImmediateRxScheduler(async () => {
            var service = new FakeDaemonClientService();
            var (actions, _) = NewActions(service);
            var lifecycleStatus = new Subject<string?>();
            var vm = new MainWindowViewModel(
                service, actions, new FakeTicker(), CancellationToken.None, TestActivity.New(),
                lifecycleStatus: lifecycleStatus);
            using var activation = vm.Activator.Activate();

            lifecycleStatus.OnNext("daemon started, app not yet attached — retrying");
            await Assert.That(vm.StartMessage).IsEqualTo("daemon started, app not yet attached — retrying");

            service.StatusSubject.OnNext(new AttachStatus(AttachState.Connected, null, null));
            await Assert.That(vm.StartMessage).IsNull(); // same Connected-transition clear RunStartAsync's own message gets
        });
    }

    // spec §4.4: StartDaemonCommand is repointed to the service-aware
    // DaemonLifecycleController.StartActionAsync when the composition root supplies one — the
    // plain detached StartDaemonAsync is a fallback for callers with no live controller, not the
    // production path.
    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task StartDaemonCommand_invokes_the_supplied_startAction_instead_of_StartDaemonAsync() {
        await AvaloniaSession.WithImmediateRxScheduler(async () => {
            var service = new FakeDaemonClientService();
            var (actions, _) = NewActions(service);
            var calls = 0;
            CancellationToken? seen = null;
            Task StartAction(CancellationToken ct) {
                calls++;
                seen = ct;
                return Task.CompletedTask;
            }
            using var cts = new CancellationTokenSource();
            var vm = new MainWindowViewModel(service, actions, new FakeTicker(), cts.Token, TestActivity.New(), StartAction);

            service.StatusSubject.OnNext(new AttachStatus(AttachState.Unreachable, "daemon_unreachable", null));
            await vm.StartDaemonCommand.Execute().ToTask();

            await Assert.That(calls).IsEqualTo(1);
            await Assert.That(seen).IsEqualTo(cts.Token);
            await Assert.That(service.StartDaemonCallCount).IsEqualTo(0);
        });
    }

    // ---- Navigation (spec §3). The full surface-swap/teardown matrix lives in
    // WorkspaceNavigationTests; these two pin what the VM's own nullable-default seams promise. ----

    /// Every caller that predates workspaces passes no factory — and must keep landing on the
    /// tabbed shell rather than a half-built workspace or a throw.
    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task Without_a_workspace_factory_the_window_stays_on_the_tabbed_shell() {
        await AvaloniaSession.WithImmediateRxScheduler(async () => {
            var service = new FakeDaemonClientService();
            var (actions, _) = NewActions(service);
            var vm = new MainWindowViewModel(service, actions, new FakeTicker(), CancellationToken.None, TestActivity.New());

            vm.OpenSession("0123456789abcdef0123456789abcdef");
            vm.OpenSessionIfCurrent("0123456789abcdef0123456789abcdef", vm.NavigationGeneration);

            await Assert.That(vm.CurrentWorkspace).IsNull();
        });
    }

    /// The gate is app-lifetime, not per-window: MainWindowCoordinator can build a second window
    /// over the same composition, and a launch captured in the first must read as stale in the
    /// second — which only holds while both read ONE generation.
    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task The_navigation_gate_is_shared_across_the_windows_built_over_it() {
        await AvaloniaSession.WithImmediateRxScheduler(async () => {
            var service = new FakeDaemonClientService();
            var (actions, _) = NewActions(service);
            var gate = new NavigationGate();
            MainWindowViewModel Build() => new(
                service, actions, new FakeTicker(), CancellationToken.None, TestActivity.New(), navigation: gate);

            var first = Build();
            var second = Build();
            var captured = first.NavigationGeneration;

            first.CloseWorkspace(); // close-to-hide in the first window

            await Assert.That(second.NavigationGeneration).IsEqualTo(first.NavigationGeneration);
            await Assert.That(second.NavigationGeneration).IsNotEqualTo(captured);

            first.LatchShutdown();
            await Assert.That(gate.ShutdownLatched).IsTrue();
        });
    }

    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task Deactivation_disposes_subscriptions() {
        await AvaloniaSession.WithImmediateRxScheduler(async () => {
            var service = new FakeDaemonClientService();
            var (actions, _) = NewActions(service);
            var vm = new MainWindowViewModel(service, actions, new FakeTicker(), CancellationToken.None, TestActivity.New());

            var activation = vm.Activator.Activate();
            service.SnapshotsSubject.OnNext(Snap(daemon: "daemon-a", active: 1, max: 5));
            service.StatusSubject.OnNext(new AttachStatus(AttachState.Connected, null, null));

            await Assert.That(vm.DaemonName).IsEqualTo("daemon-a");
            await Assert.That(vm.AgentCountText).IsEqualTo("1 of 5 agents");

            activation.Dispose(); // window close

            var daemonNameAtDeactivation = vm.DaemonName;
            var agentCountAtDeactivation = vm.AgentCountText;
            var stateAtDeactivation      = vm.State;

            service.SnapshotsSubject.OnNext(Snap(daemon: "daemon-b", active: 3, max: 5));
            service.StatusSubject.OnNext(new AttachStatus(AttachState.Unreachable, "daemon_unreachable", null));

            await Assert.That(vm.DaemonName).IsEqualTo(daemonNameAtDeactivation);
            await Assert.That(vm.AgentCountText).IsEqualTo(agentCountAtDeactivation);
            await Assert.That(vm.State).IsEqualTo(stateAtDeactivation);
        });
    }
}
