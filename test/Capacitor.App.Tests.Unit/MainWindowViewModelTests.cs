using System.Reactive.Threading.Tasks;
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
    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task Projections_follow_the_snapshot() {
        await AvaloniaSession.WithImmediateRxScheduler(async () => {
            var service = new FakeDaemonClientService();
            var vm = new MainWindowViewModel(service, CancellationToken.None);
            using var activation = vm.Activator.Activate();

            service.SnapshotsSubject.OnNext(Snap(daemon: "daemon-a", version: "1.2.3", serverUrl: "http://localhost:9999", connection: "connected"));

            await Assert.That(vm.DaemonName).IsEqualTo("daemon-a");
            await Assert.That(vm.DaemonVersion).IsEqualTo("1.2.3");
            await Assert.That(vm.ServerUrl).IsEqualTo("http://localhost:9999");
            await Assert.That(vm.ConnectionText).IsEqualTo("connected");
        });
    }

    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task Agent_count_renders_only_while_connected() {
        await AvaloniaSession.WithImmediateRxScheduler(async () => {
            var service = new FakeDaemonClientService();
            var vm = new MainWindowViewModel(service, CancellationToken.None);
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
            var vm = new MainWindowViewModel(service, CancellationToken.None);

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
            var vm = new MainWindowViewModel(service, CancellationToken.None);
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
            var vm = new MainWindowViewModel(service, CancellationToken.None);
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

    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task Deactivation_disposes_subscriptions() {
        await AvaloniaSession.WithImmediateRxScheduler(async () => {
            var service = new FakeDaemonClientService();
            var vm = new MainWindowViewModel(service, CancellationToken.None);

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
