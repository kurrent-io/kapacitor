using System.Reactive.Subjects;
using System.Reactive.Threading.Tasks;
using Capacitor.App.Services;
using Capacitor.App.ViewModels;
using Capacitor.Cli.Core.LocalIpc;
using TUnit.Assertions.Enums;

namespace Capacitor.App.Tests.Unit;

/// Scripted IPauseController — subject-backed State + recorded calls — mirrors
/// FakeDaemonClientService's shape so TrayViewModelTests can drive exact sequences.
sealed class FakePauseController : IPauseController {
    public readonly BehaviorSubject<PauseState> StateSubject = new(new PauseState(false, false, false));
    public IObservable<PauseState> State => StateSubject;

    public int RefreshCount;
    public void RequestRefresh() => RefreshCount++;

    public readonly List<bool> ToggleRequests = [];
    public void RequestToggle(bool desired) => ToggleRequests.Add(desired);
}

/// Covers the §4 ten-row state matrix, §5 header copy, agent-entry projection, and pause-item
/// enablement. All tests touch RxSchedulers (TrayViewModel's OAPH uses
/// RxSchedulers.MainThreadScheduler), so every test runs inside
/// AvaloniaSession.WithImmediateRxScheduler and carries [NotInParallel("AvaloniaSession")].
public class TrayViewModelTests {
    static DaemonStatusDto Snap(string connection = "connected", int active = 0, IReadOnlyList<AgentStatusDto>? agents = null) =>
        new(new DaemonInfoDto("daemon-a", "1.2.3", "http://localhost:9999", connection, 5, active), (agents ?? []).ToList());

    // Real AgentActionService wired to the SAME service.SnapshotsSubject as the FakeDaemonClientService
    // (production shares one snapshots stream between TrayViewModel and AgentActionService) —
    // AgentActionService has no interface seam (spec-pinned concrete sealed class), so tests
    // construct it for real against a scripted ILocalControlOps.
    static AgentActionService NewActions(FakeDaemonClientService service, ScriptedLocalControlOps? ops = null) =>
        new(ops ?? new ScriptedLocalControlOps(), new RecordingNotifier(), new RecordingOpener(), service.SnapshotsSubject, CancellationToken.None);

    // ---- §4 state matrix ----

    [Test]
    [NotInParallel("AvaloniaSession")]
    [Arguments("daemon_unreachable", TrayState.Stopped)]   // row 1
    [Arguments("daemon_incompatible", TrayState.Attention)] // row 2
    [Arguments("some_future_reason", TrayState.Attention)]  // row 10
    public async Task Unreachable_reason_maps_to_state(string reason, TrayState expected) {
        await AvaloniaSession.WithImmediateRxScheduler(async () => {
            var service = new FakeDaemonClientService();
            var pause = new FakePauseController();
            var actions = NewActions(service);
            using var vm = new TrayViewModel(service, pause, actions);

            service.StatusSubject.OnNext(new AttachStatus(AttachState.Unreachable, reason, null));

            await Assert.That(vm.MenuModel.State).IsEqualTo(expected);
            await Assert.That(vm.MenuModel.RunningCount).IsEqualTo(0);
        });
    }

    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task Connecting_state_maps_to_connecting() { // row 3
        await AvaloniaSession.WithImmediateRxScheduler(async () => {
            var service = new FakeDaemonClientService();
            var pause = new FakePauseController();
            var actions = NewActions(service);
            using var vm = new TrayViewModel(service, pause, actions);

            await Assert.That(vm.MenuModel.State).IsEqualTo(TrayState.Connecting);
            await Assert.That(vm.MenuModel.RunningCount).IsEqualTo(0);
        });
    }

    [Test]
    [NotInParallel("AvaloniaSession")]
    [Arguments("connecting", 0, TrayState.Connecting, 0)]    // row 4
    [Arguments("reconnecting", 0, TrayState.Attention, 0)]   // row 5
    [Arguments("disconnected", 0, TrayState.Attention, 0)]   // row 5
    [Arguments("connected", -1, TrayState.Attention, 0)]     // row 6
    [Arguments("connected", 0, TrayState.Idle, 0)]           // row 7
    [Arguments("connected", 4, TrayState.Running, 4)]        // row 8
    [Arguments("weird", 0, TrayState.Attention, 0)]          // row 9
    public async Task Connected_connection_value_maps_to_state(
            string connection, int active, TrayState expectedState, int expectedCount) {
        await AvaloniaSession.WithImmediateRxScheduler(async () => {
            var service = new FakeDaemonClientService();
            var pause = new FakePauseController();
            var actions = NewActions(service);
            using var vm = new TrayViewModel(service, pause, actions);

            service.StatusSubject.OnNext(new AttachStatus(AttachState.Connected, null, []));
            service.SnapshotsSubject.OnNext(Snap(connection, active));

            await Assert.That(vm.MenuModel.State).IsEqualTo(expectedState);
            await Assert.That(vm.MenuModel.RunningCount).IsEqualTo(expectedCount);
        });
    }

    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task Connected_before_first_snapshot_is_defensively_connecting() {
        await AvaloniaSession.WithImmediateRxScheduler(async () => {
            var service = new FakeDaemonClientService();
            var pause = new FakePauseController();
            var actions = NewActions(service);
            using var vm = new TrayViewModel(service, pause, actions);

            service.StatusSubject.OnNext(new AttachStatus(AttachState.Connected, null, []));
            // No snapshot pushed — cannot happen per the client pin, but Project must stay total.

            await Assert.That(vm.MenuModel.State).IsEqualTo(TrayState.Connecting);
        });
    }

    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task Stale_snapshot_does_not_override_unreachable_precedence() {
        await AvaloniaSession.WithImmediateRxScheduler(async () => {
            var service = new FakeDaemonClientService();
            var pause = new FakePauseController();
            var actions = NewActions(service);
            using var vm = new TrayViewModel(service, pause, actions);

            service.StatusSubject.OnNext(new AttachStatus(AttachState.Connected, null, []));
            service.SnapshotsSubject.OnNext(Snap("connected", 3));
            await Assert.That(vm.MenuModel.State).IsEqualTo(TrayState.Running);

            service.StatusSubject.OnNext(new AttachStatus(AttachState.Unreachable, "daemon_unreachable", null));
            await Assert.That(vm.MenuModel.State).IsEqualTo(TrayState.Stopped);
            await Assert.That(vm.MenuModel.RunningCount).IsEqualTo(0);

            service.StatusSubject.OnNext(new AttachStatus(AttachState.Unreachable, "daemon_incompatible", null));
            await Assert.That(vm.MenuModel.State).IsEqualTo(TrayState.Attention);
        });
    }

    // ---- §5 header copy ----

    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task Header_stopped() {
        await AvaloniaSession.WithImmediateRxScheduler(async () => {
            var service = new FakeDaemonClientService();
            var pause = new FakePauseController();
            var actions = NewActions(service);
            using var vm = new TrayViewModel(service, pause, actions);

            service.StatusSubject.OnNext(new AttachStatus(AttachState.Unreachable, "daemon_unreachable", null));

            await Assert.That(vm.MenuModel.Header).IsEqualTo("daemon-a: not running");
        });
    }

    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task Header_connecting() {
        await AvaloniaSession.WithImmediateRxScheduler(async () => {
            var service = new FakeDaemonClientService();
            var pause = new FakePauseController();
            var actions = NewActions(service);
            using var vm = new TrayViewModel(service, pause, actions);

            await Assert.That(vm.MenuModel.Header).IsEqualTo("daemon-a: connecting…");
        });
    }

    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task Header_incompatible_has_no_daemon_name_prefix() {
        await AvaloniaSession.WithImmediateRxScheduler(async () => {
            var service = new FakeDaemonClientService();
            var pause = new FakePauseController();
            var actions = NewActions(service);
            using var vm = new TrayViewModel(service, pause, actions);

            service.StatusSubject.OnNext(new AttachStatus(AttachState.Unreachable, "daemon_incompatible", null));

            await Assert.That(vm.MenuModel.Header)
                .IsEqualTo("app and daemon are incompatible — make sure both are up to date");
        });
    }

    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task Header_reconnecting_to_server() {
        await AvaloniaSession.WithImmediateRxScheduler(async () => {
            var service = new FakeDaemonClientService();
            var pause = new FakePauseController();
            var actions = NewActions(service);
            using var vm = new TrayViewModel(service, pause, actions);

            service.StatusSubject.OnNext(new AttachStatus(AttachState.Connected, null, []));
            service.SnapshotsSubject.OnNext(Snap("reconnecting"));

            await Assert.That(vm.MenuModel.Header).IsEqualTo("daemon-a: reconnecting to server");
        });
    }

    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task Header_disconnected_from_server() {
        await AvaloniaSession.WithImmediateRxScheduler(async () => {
            var service = new FakeDaemonClientService();
            var pause = new FakePauseController();
            var actions = NewActions(service);
            using var vm = new TrayViewModel(service, pause, actions);

            service.StatusSubject.OnNext(new AttachStatus(AttachState.Connected, null, []));
            service.SnapshotsSubject.OnNext(Snap("disconnected"));

            await Assert.That(vm.MenuModel.Header).IsEqualTo("daemon-a: disconnected from server");
        });
    }

    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task Header_idle() {
        await AvaloniaSession.WithImmediateRxScheduler(async () => {
            var service = new FakeDaemonClientService();
            var pause = new FakePauseController();
            var actions = NewActions(service);
            using var vm = new TrayViewModel(service, pause, actions);

            service.StatusSubject.OnNext(new AttachStatus(AttachState.Connected, null, []));
            service.SnapshotsSubject.OnNext(Snap("connected", 0));

            await Assert.That(vm.MenuModel.Header).IsEqualTo("daemon-a: connected — no agents");
        });
    }

    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task Header_running() {
        await AvaloniaSession.WithImmediateRxScheduler(async () => {
            var service = new FakeDaemonClientService();
            var pause = new FakePauseController();
            var actions = NewActions(service);
            using var vm = new TrayViewModel(service, pause, actions);

            service.StatusSubject.OnNext(new AttachStatus(AttachState.Connected, null, []));
            service.SnapshotsSubject.OnNext(Snap("connected", 4));

            await Assert.That(vm.MenuModel.Header).IsEqualTo("daemon-a: connected — 4 agent(s) running");
        });
    }

    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task Header_needs_attention_on_unrecognized_connection() { // row 9
        await AvaloniaSession.WithImmediateRxScheduler(async () => {
            var service = new FakeDaemonClientService();
            var pause = new FakePauseController();
            var actions = NewActions(service);
            using var vm = new TrayViewModel(service, pause, actions);

            service.StatusSubject.OnNext(new AttachStatus(AttachState.Connected, null, []));
            service.SnapshotsSubject.OnNext(Snap("weird"));

            await Assert.That(vm.MenuModel.Header).IsEqualTo("daemon-a: needs attention");
        });
    }

    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task Header_needs_attention_on_negative_active_agents() { // row 6
        await AvaloniaSession.WithImmediateRxScheduler(async () => {
            var service = new FakeDaemonClientService();
            var pause = new FakePauseController();
            var actions = NewActions(service);
            using var vm = new TrayViewModel(service, pause, actions);

            service.StatusSubject.OnNext(new AttachStatus(AttachState.Connected, null, []));
            service.SnapshotsSubject.OnNext(Snap("connected", -1));

            await Assert.That(vm.MenuModel.Header).IsEqualTo("daemon-a: needs attention");
        });
    }

    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task Header_needs_attention_on_unrecognized_unreachable_reason() { // row 10
        await AvaloniaSession.WithImmediateRxScheduler(async () => {
            var service = new FakeDaemonClientService();
            var pause = new FakePauseController();
            var actions = NewActions(service);
            using var vm = new TrayViewModel(service, pause, actions);

            service.StatusSubject.OnNext(new AttachStatus(AttachState.Unreachable, "future_reason", null));

            await Assert.That(vm.MenuModel.Header).IsEqualTo("daemon-a: needs attention");
        });
    }

    // ---- agent entries ----

    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task Entries_filter_to_starting_and_running_ordered_by_creation() {
        await AvaloniaSession.WithImmediateRxScheduler(async () => {
            var service = new FakeDaemonClientService();
            var pause = new FakePauseController();
            var actions = NewActions(service);
            using var vm = new TrayViewModel(service, pause, actions);

            var t0 = new DateTime(2026, 8, 6, 10, 0, 0, DateTimeKind.Utc);
            var agents = new List<AgentStatusDto> {
                new("b", "agent", "claude", "/repos/kcap-cli", "Running", null, null, null, t0.AddMinutes(2), null),
                new("a", "agent", "claude", "/repos/kcap-cli", "Starting", null, null, null, t0, null),
                new("c", "review", "codex", null, "Completed", null, null, null, t0.AddMinutes(1), null),
            };

            service.StatusSubject.OnNext(new AttachStatus(AttachState.Connected, null, []));
            service.SnapshotsSubject.OnNext(Snap("connected", 2, agents));

            var entries = vm.MenuModel.Agents;
            await Assert.That(entries.Count).IsEqualTo(2);
            await Assert.That(entries[0].Id).IsEqualTo("a");
            await Assert.That(entries[1].Id).IsEqualTo("b");
            await Assert.That(entries[0].Label).IsEqualTo("agent · claude · kcap-cli");
        });
    }

    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task Entries_tiebreak_by_id_ordinal_when_created_at_equal() {
        await AvaloniaSession.WithImmediateRxScheduler(async () => {
            var service = new FakeDaemonClientService();
            var pause = new FakePauseController();
            var actions = NewActions(service);
            using var vm = new TrayViewModel(service, pause, actions);

            var t0 = new DateTime(2026, 8, 6, 10, 0, 0, DateTimeKind.Utc);
            var agents = new List<AgentStatusDto> {
                new("z", "agent", "claude", null, "Running", null, null, null, t0, null),
                new("a", "agent", "claude", null, "Running", null, null, null, t0, null),
            };

            service.StatusSubject.OnNext(new AttachStatus(AttachState.Connected, null, []));
            service.SnapshotsSubject.OnNext(Snap("connected", 2, agents));

            var entries = vm.MenuModel.Agents;
            await Assert.That(entries[0].Id).IsEqualTo("a");
            await Assert.That(entries[1].Id).IsEqualTo("z");
        });
    }

    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task Entries_label_uses_em_dash_for_null_repo_path() {
        await AvaloniaSession.WithImmediateRxScheduler(async () => {
            var service = new FakeDaemonClientService();
            var pause = new FakePauseController();
            var actions = NewActions(service);
            using var vm = new TrayViewModel(service, pause, actions);

            var agents = new List<AgentStatusDto> {
                new("r", "review-flow", "codex", null, "Running", null, null, null, DateTime.UtcNow, null),
            };

            service.StatusSubject.OnNext(new AttachStatus(AttachState.Connected, null, []));
            service.SnapshotsSubject.OnNext(Snap("connected", 1, agents));

            await Assert.That(vm.MenuModel.Agents[0].Label).IsEqualTo("review-flow · codex · —");
        });
    }

    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task Entries_empty_when_not_connected_despite_retained_snapshot() {
        await AvaloniaSession.WithImmediateRxScheduler(async () => {
            var service = new FakeDaemonClientService();
            var pause = new FakePauseController();
            var actions = NewActions(service);
            using var vm = new TrayViewModel(service, pause, actions);

            var agents = new List<AgentStatusDto> {
                new("a", "agent", "claude", null, "Running", null, null, null, DateTime.UtcNow, null),
            };

            service.StatusSubject.OnNext(new AttachStatus(AttachState.Connected, null, []));
            service.SnapshotsSubject.OnNext(Snap("connected", 1, agents));
            await Assert.That(vm.MenuModel.Agents.Count).IsEqualTo(1);

            service.StatusSubject.OnNext(new AttachStatus(AttachState.Unreachable, "daemon_unreachable", null));
            await Assert.That(vm.MenuModel.Agents.Count).IsEqualTo(0);
        });
    }

    // ---- pause item enablement ----

    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task Pause_disabled_without_consent_capability() {
        await AvaloniaSession.WithImmediateRxScheduler(async () => {
            var service = new FakeDaemonClientService();
            var pause = new FakePauseController();
            var actions = NewActions(service);
            using var vm = new TrayViewModel(service, pause, actions);

            pause.StateSubject.OnNext(new PauseState(Checked: false, Verified: true, Busy: false));
            service.StatusSubject.OnNext(new AttachStatus(AttachState.Connected, null, [])); // no consent/1
            service.SnapshotsSubject.OnNext(Snap());

            await Assert.That(vm.MenuModel.Pause.Enabled).IsFalse();
        });
    }

    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task Pause_disabled_when_not_connected() {
        await AvaloniaSession.WithImmediateRxScheduler(async () => {
            var service = new FakeDaemonClientService();
            var pause = new FakePauseController();
            var actions = NewActions(service);
            using var vm = new TrayViewModel(service, pause, actions);

            pause.StateSubject.OnNext(new PauseState(Checked: false, Verified: true, Busy: false));
            service.StatusSubject.OnNext(new AttachStatus(AttachState.Unreachable, "daemon_unreachable", null));

            await Assert.That(vm.MenuModel.Pause.Enabled).IsFalse();
        });
    }

    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task Pause_disabled_when_busy() {
        await AvaloniaSession.WithImmediateRxScheduler(async () => {
            var service = new FakeDaemonClientService();
            var pause = new FakePauseController();
            var actions = NewActions(service);
            using var vm = new TrayViewModel(service, pause, actions);

            service.StatusSubject.OnNext(new AttachStatus(AttachState.Connected, null, ["consent/1"]));
            service.SnapshotsSubject.OnNext(Snap());
            pause.StateSubject.OnNext(new PauseState(Checked: false, Verified: true, Busy: true));

            await Assert.That(vm.MenuModel.Pause.Enabled).IsFalse();
        });
    }

    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task Pause_disabled_when_unverified() {
        await AvaloniaSession.WithImmediateRxScheduler(async () => {
            var service = new FakeDaemonClientService();
            var pause = new FakePauseController();
            var actions = NewActions(service);
            using var vm = new TrayViewModel(service, pause, actions);

            service.StatusSubject.OnNext(new AttachStatus(AttachState.Connected, null, ["consent/1"]));
            service.SnapshotsSubject.OnNext(Snap());
            pause.StateSubject.OnNext(new PauseState(Checked: false, Verified: false, Busy: false));

            await Assert.That(vm.MenuModel.Pause.Enabled).IsFalse();
        });
    }

    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task Pause_enabled_with_checked_mirroring_state() {
        await AvaloniaSession.WithImmediateRxScheduler(async () => {
            var service = new FakeDaemonClientService();
            var pause = new FakePauseController();
            var actions = NewActions(service);
            using var vm = new TrayViewModel(service, pause, actions);

            service.StatusSubject.OnNext(new AttachStatus(AttachState.Connected, null, ["consent/1"]));
            service.SnapshotsSubject.OnNext(Snap());
            pause.StateSubject.OnNext(new PauseState(Checked: true, Verified: true, Busy: false));

            await Assert.That(vm.MenuModel.Pause.Enabled).IsTrue();
            await Assert.That(vm.MenuModel.Pause.Checked).IsTrue();
        });
    }

    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task Pause_checked_reflects_last_known_value_even_when_disabled() {
        await AvaloniaSession.WithImmediateRxScheduler(async () => {
            var service = new FakeDaemonClientService();
            var pause = new FakePauseController();
            var actions = NewActions(service);
            using var vm = new TrayViewModel(service, pause, actions);

            service.StatusSubject.OnNext(new AttachStatus(AttachState.Connected, null, ["consent/1"]));
            service.SnapshotsSubject.OnNext(Snap());
            pause.StateSubject.OnNext(new PauseState(Checked: true, Verified: false, Busy: false));

            await Assert.That(vm.MenuModel.Pause.Enabled).IsFalse();
            await Assert.That(vm.MenuModel.Pause.Checked).IsTrue();
        });
    }

    // ---- adapter delegation ----

    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task RequestPauseRefresh_delegates_to_controller() {
        await AvaloniaSession.WithImmediateRxScheduler(async () => {
            var service = new FakeDaemonClientService();
            var pause = new FakePauseController();
            var actions = NewActions(service);
            using var vm = new TrayViewModel(service, pause, actions);

            vm.RequestPauseRefresh();

            await Assert.That(pause.RefreshCount).IsEqualTo(1);
        });
    }

    [Test]
    [NotInParallel("AvaloniaSession")]
    [Arguments(true)]
    [Arguments(false)]
    public async Task TogglePauseCommand_reaches_controller_with_parameter_value(bool desired) {
        await AvaloniaSession.WithImmediateRxScheduler(async () => {
            var service = new FakeDaemonClientService();
            var pause = new FakePauseController();
            var actions = NewActions(service);
            using var vm = new TrayViewModel(service, pause, actions);

            await vm.TogglePauseCommand.Execute(desired).ToTask();

            await Assert.That(pause.ToggleRequests).IsEquivalentTo([desired], CollectionOrdering.Matching);
        });
    }

    // ---- stop gating + open-in-web (spec §7) ----

    static async Task WaitUntilAsync(Func<bool> condition, TimeSpan? timeout = null, string what = "condition") {
        var deadline = DateTime.UtcNow + (timeout ?? TimeSpan.FromSeconds(5));
        while (!condition()) {
            if (DateTime.UtcNow > deadline) throw new TimeoutException($"Timed out waiting for: {what}");
            await Task.Delay(10);
        }
    }

    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task Entry_StopEnabled_flips_while_stop_in_flight() {
        await AvaloniaSession.WithImmediateRxScheduler(async () => {
            var service = new FakeDaemonClientService();
            var pause = new FakePauseController();
            var ops = new ScriptedLocalControlOps();
            var actions = new AgentActionService(ops, new RecordingNotifier(), new RecordingOpener(), service.SnapshotsSubject, CancellationToken.None);
            using var vm = new TrayViewModel(service, pause, actions);

            var agents = new List<AgentStatusDto> {
                new("a", "agent", "claude", null, "Running", null, null, null, DateTime.UtcNow, null),
            };
            service.StatusSubject.OnNext(new AttachStatus(AttachState.Connected, null, []));
            service.SnapshotsSubject.OnNext(Snap("connected", 1, agents));

            await Assert.That(vm.MenuModel.Agents[0].StopEnabled).IsTrue();

            var gate = ops.ArmStop();
            actions.RequestStop("a", "agent · claude · —");

            // Pushed synchronously by RequestStop before it returns (spec §7 in-flight gating).
            await Assert.That(vm.MenuModel.Agents[0].StopEnabled).IsFalse();

            gate.SetResult(new StopAgentResult(true, "stopped", null));
            await WaitUntilAsync(() => vm.MenuModel.Agents[0].StopEnabled, what: "entry to re-enable after stop completes");
        });
    }

    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task StopAgentCommand_reaches_service_with_entry_label() {
        await AvaloniaSession.WithImmediateRxScheduler(async () => {
            var service = new FakeDaemonClientService();
            var pause = new FakePauseController();
            var ops = new ScriptedLocalControlOps();
            var notifier = new RecordingNotifier();
            var actions = new AgentActionService(ops, notifier, new RecordingOpener(), service.SnapshotsSubject, CancellationToken.None);
            using var vm = new TrayViewModel(service, pause, actions);

            var agents = new List<AgentStatusDto> {
                new("a", "agent", "claude", "/repos/kcap-cli", "Running", null, null, null, DateTime.UtcNow, null),
            };
            service.StatusSubject.OnNext(new AttachStatus(AttachState.Connected, null, []));
            service.SnapshotsSubject.OnNext(Snap("connected", 1, agents));

            ops.QueueStop(new StopAgentResult(false, "failed", null));
            await vm.StopAgentCommand.Execute("a").ToTask();

            await WaitUntilAsync(() => notifier.Notified.Count >= 1, what: "stop banner");
            await Assert.That(notifier.Notified).IsEquivalentTo(["Couldn't stop agent · claude · kcap-cli"], CollectionOrdering.Matching);
            await Assert.That(ops.StopPayloads).IsEquivalentTo([("a", false)], CollectionOrdering.Matching);
        });
    }

    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task OpenInWebCommand_reaches_service() {
        await AvaloniaSession.WithImmediateRxScheduler(async () => {
            var service = new FakeDaemonClientService();
            var pause = new FakePauseController();
            var ops = new ScriptedLocalControlOps();
            var opener = new RecordingOpener();
            var actions = new AgentActionService(ops, new RecordingNotifier(), opener, service.SnapshotsSubject, CancellationToken.None);
            using var vm = new TrayViewModel(service, pause, actions);

            service.SnapshotsSubject.OnNext(FakeDaemonClientService.Snap(serverUrl: "https://x.kcap.ai"));

            await vm.OpenInWebCommand.Execute("agent-1").ToTask();

            await Assert.That(opener.Opened).IsEquivalentTo(["https://x.kcap.ai/agents/agent-1"], CollectionOrdering.Matching);
        });
    }

    // ---- OpenMainWindowCommand / QuitCommand delegation (Task 6 adds the injected delegates; Task 7 supplies the real callbacks) ----

    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task OpenMainWindowCommand_invokes_the_injected_delegate() {
        await AvaloniaSession.WithImmediateRxScheduler(async () => {
            var service = new FakeDaemonClientService();
            var pause = new FakePauseController();
            var actions = NewActions(service);
            var calls = 0;
            using var vm = new TrayViewModel(service, pause, actions, openMainWindow: () => calls++);

            await vm.OpenMainWindowCommand.Execute().ToTask();

            await Assert.That(calls).IsEqualTo(1);
        });
    }

    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task QuitCommand_invokes_the_injected_delegate() {
        await AvaloniaSession.WithImmediateRxScheduler(async () => {
            var service = new FakeDaemonClientService();
            var pause = new FakePauseController();
            var actions = NewActions(service);
            var calls = 0;
            using var vm = new TrayViewModel(service, pause, actions, quit: () => calls++);

            await vm.QuitCommand.Execute().ToTask();

            await Assert.That(calls).IsEqualTo(1);
        });
    }

    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task OpenMainWindowCommand_and_QuitCommand_default_to_a_no_op_without_throwing() {
        await AvaloniaSession.WithImmediateRxScheduler(async () => {
            var service = new FakeDaemonClientService();
            var pause = new FakePauseController();
            var actions = NewActions(service);
            using var vm = new TrayViewModel(service, pause, actions); // no delegates injected

            await vm.OpenMainWindowCommand.Execute().ToTask();
            await vm.QuitCommand.Execute().ToTask();
        });
    }
}
