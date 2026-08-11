using System.Reactive.Subjects;
using System.Reactive.Threading.Tasks;
using Capacitor.App.Services;
using Capacitor.App.ViewModels;
using Capacitor.Cli.Core.LocalIpc;
using TUnit.Assertions.Enums;
using static Capacitor.App.Tests.Unit.ConsentEntries;

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
        new(ops ?? new ScriptedLocalControlOps(), new RecordingNotifier(), new RecordingOpener(), service.SnapshotsSubject, CancellationToken.None, NeverConfirm.Confirm);

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
            var consent = new FakeConsentService();
            using var vm = new TrayViewModel(service, pause, actions, consent);

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
            var consent = new FakeConsentService();
            using var vm = new TrayViewModel(service, pause, actions, consent);

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
            var consent = new FakeConsentService();
            using var vm = new TrayViewModel(service, pause, actions, consent);

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
            var consent = new FakeConsentService();
            using var vm = new TrayViewModel(service, pause, actions, consent);

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
            var consent = new FakeConsentService();
            using var vm = new TrayViewModel(service, pause, actions, consent);

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
            var consent = new FakeConsentService();
            using var vm = new TrayViewModel(service, pause, actions, consent);

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
            var consent = new FakeConsentService();
            using var vm = new TrayViewModel(service, pause, actions, consent);

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
            var consent = new FakeConsentService();
            using var vm = new TrayViewModel(service, pause, actions, consent);

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
            var consent = new FakeConsentService();
            using var vm = new TrayViewModel(service, pause, actions, consent);

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
            var consent = new FakeConsentService();
            using var vm = new TrayViewModel(service, pause, actions, consent);

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
            var consent = new FakeConsentService();
            using var vm = new TrayViewModel(service, pause, actions, consent);

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
            var consent = new FakeConsentService();
            using var vm = new TrayViewModel(service, pause, actions, consent);

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
            var consent = new FakeConsentService();
            using var vm = new TrayViewModel(service, pause, actions, consent);

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
            var consent = new FakeConsentService();
            using var vm = new TrayViewModel(service, pause, actions, consent);

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
            var consent = new FakeConsentService();
            using var vm = new TrayViewModel(service, pause, actions, consent);

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
            var consent = new FakeConsentService();
            using var vm = new TrayViewModel(service, pause, actions, consent);

            var t0 = new DateTime(2026, 8, 6, 10, 0, 0, DateTimeKind.Utc);
            var agents = new List<AgentStatusDto> {
                new("b", "agent", "claude", "/repos/kcap-cli", "Running", null, null, null, t0.AddMinutes(2), null, null),
                new("a", "agent", "claude", "/repos/kcap-cli", "Starting", null, null, null, t0, null, null),
                new("c", "review", "codex", null, "Completed", null, null, null, t0.AddMinutes(1), null, null),
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
            var consent = new FakeConsentService();
            using var vm = new TrayViewModel(service, pause, actions, consent);

            var t0 = new DateTime(2026, 8, 6, 10, 0, 0, DateTimeKind.Utc);
            var agents = new List<AgentStatusDto> {
                new("z", "agent", "claude", null, "Running", null, null, null, t0, null, null),
                new("a", "agent", "claude", null, "Running", null, null, null, t0, null, null),
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
            var consent = new FakeConsentService();
            using var vm = new TrayViewModel(service, pause, actions, consent);

            var agents = new List<AgentStatusDto> {
                new("r", "review-flow", "codex", null, "Running", null, null, null, DateTime.UtcNow, null, null),
            };

            service.StatusSubject.OnNext(new AttachStatus(AttachState.Connected, null, []));
            service.SnapshotsSubject.OnNext(Snap("connected", 1, agents));

            await Assert.That(vm.MenuModel.Agents[0].Label).IsEqualTo("review-flow · codex · —");
        });
    }

    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task Entries_label_is_worktree_aware_for_claude_worktree_repo_path() {
        await AvaloniaSession.WithImmediateRxScheduler(async () => {
            var service = new FakeDaemonClientService();
            var pause = new FakePauseController();
            var actions = NewActions(service);
            var consent = new FakeConsentService();
            using var vm = new TrayViewModel(service, pause, actions, consent);

            var agents = new List<AgentStatusDto> {
                new("a", "agent", "claude", "/Users/alexey/dev/kcap-server/.claude/worktrees/hazy-sleeping-plum", "Running", null, null, null, DateTime.UtcNow, null, null),
            };

            service.StatusSubject.OnNext(new AttachStatus(AttachState.Connected, null, []));
            service.SnapshotsSubject.OnNext(Snap("connected", 1, agents));

            await Assert.That(vm.MenuModel.Agents[0].Label).IsEqualTo("agent · claude · kcap-server");
        });
    }

    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task Entries_empty_when_not_connected_despite_retained_snapshot() {
        await AvaloniaSession.WithImmediateRxScheduler(async () => {
            var service = new FakeDaemonClientService();
            var pause = new FakePauseController();
            var actions = NewActions(service);
            var consent = new FakeConsentService();
            using var vm = new TrayViewModel(service, pause, actions, consent);

            var agents = new List<AgentStatusDto> {
                new("a", "agent", "claude", null, "Running", null, null, null, DateTime.UtcNow, null, null),
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
            var consent = new FakeConsentService();
            using var vm = new TrayViewModel(service, pause, actions, consent);

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
            var consent = new FakeConsentService();
            using var vm = new TrayViewModel(service, pause, actions, consent);

            pause.StateSubject.OnNext(new PauseState(Checked: false, Verified: true, Busy: false));
            service.StatusSubject.OnNext(new AttachStatus(AttachState.Unreachable, "daemon_unreachable", null));

            await Assert.That(vm.MenuModel.Pause.Enabled).IsFalse();
        });
    }

    // Isolates the `connected` term from `hasCapability`: BuildPause derives both solely from the
    // same AttachStatus, and Pause_disabled_when_not_connected's not-Connected status carries null
    // Capabilities (the real, contract-abiding shape — AttachStatus.cs pins capabilities null on
    // every non-connected state), so hasCapability is false there for the same reason connected is
    // — a mutant that drops the `connected &&` term from the enabled formula still passes. To kill
    // that mutant, capability must stay true while connected is false, which is unreachable through
    // the service contract, hence the deliberate contract violation below.
    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task Pause_disabled_when_not_connected_despite_retained_capability() {
        await AvaloniaSession.WithImmediateRxScheduler(async () => {
            var service = new FakeDaemonClientService();
            var pause = new FakePauseController();
            var actions = NewActions(service);
            var consent = new FakeConsentService();
            using var vm = new TrayViewModel(service, pause, actions, consent);

            service.StatusSubject.OnNext(new AttachStatus(AttachState.Connected, null, ["consent/1"]));
            service.SnapshotsSubject.OnNext(Snap());
            pause.StateSubject.OnNext(new PauseState(Checked: true, Verified: true, Busy: false));
            await Assert.That(vm.MenuModel.Pause.Enabled).IsTrue();

            // Deliberately violates the real AttachStatus contract (capabilities are never non-null
            // when not Connected) so hasCapability alone cannot explain disablement — this isolates
            // and kills a mutant that drops the `connected &&` term in BuildPause.
            service.StatusSubject.OnNext(new AttachStatus(AttachState.Unreachable, "daemon_unreachable", ["consent/1"]));

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
            var consent = new FakeConsentService();
            using var vm = new TrayViewModel(service, pause, actions, consent);

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
            var consent = new FakeConsentService();
            using var vm = new TrayViewModel(service, pause, actions, consent);

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
            var consent = new FakeConsentService();
            using var vm = new TrayViewModel(service, pause, actions, consent);

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
            var consent = new FakeConsentService();
            using var vm = new TrayViewModel(service, pause, actions, consent);

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
            var consent = new FakeConsentService();
            using var vm = new TrayViewModel(service, pause, actions, consent);

            vm.RequestPauseRefresh();

            await Assert.That(pause.RefreshCount).IsEqualTo(1);
        });
    }

    // Fix-round 2: macOS status-item menus never raise NativeMenu.Opening (manual acceptance), so
    // the adapter's refresh kick moved to NeedsUpdate — but that alone means the toggle is only
    // ever verified starting at the SECOND menu open. This edge-triggered kick on the
    // Connecting -> Connected transition covers the gap: verified before the FIRST open too.
    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task RequestPauseRefresh_kicks_once_on_the_edge_into_Connected() {
        await AvaloniaSession.WithImmediateRxScheduler(async () => {
            var service = new FakeDaemonClientService();
            var pause = new FakePauseController();
            var actions = NewActions(service);
            var consent = new FakeConsentService();
            using var vm = new TrayViewModel(service, pause, actions, consent);

            // Connecting -> Connected(with capability): exactly one refresh from the edge.
            service.StatusSubject.OnNext(new AttachStatus(AttachState.Connected, null, ["consent/1"]));
            await Assert.That(pause.RefreshCount).IsEqualTo(1);

            // A snapshot update while still Connected is not a state transition — no extra refresh.
            service.SnapshotsSubject.OnNext(Snap("connected", 1));
            await Assert.That(pause.RefreshCount).IsEqualTo(1);

            // Another Connected push with no actual state change (DistinctUntilChanged) — none.
            service.StatusSubject.OnNext(new AttachStatus(AttachState.Connected, null, ["consent/1"]));
            await Assert.That(pause.RefreshCount).IsEqualTo(1);

            // Disconnect, then reconnect: one more refresh from the new edge.
            service.StatusSubject.OnNext(new AttachStatus(AttachState.Unreachable, "daemon_unreachable", null));
            await Assert.That(pause.RefreshCount).IsEqualTo(1);

            service.StatusSubject.OnNext(new AttachStatus(AttachState.Connected, null, ["consent/1"]));
            await Assert.That(pause.RefreshCount).IsEqualTo(2);
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
            var consent = new FakeConsentService();
            using var vm = new TrayViewModel(service, pause, actions, consent);

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
            var actions = new AgentActionService(ops, new RecordingNotifier(), new RecordingOpener(), service.SnapshotsSubject, CancellationToken.None, NeverConfirm.Confirm);
            var consent = new FakeConsentService();
            using var vm = new TrayViewModel(service, pause, actions, consent);

            var agents = new List<AgentStatusDto> {
                new("a", "agent", "claude", null, "Running", null, null, null, DateTime.UtcNow, null, null),
            };
            service.StatusSubject.OnNext(new AttachStatus(AttachState.Connected, null, []));
            service.SnapshotsSubject.OnNext(Snap("connected", 1, agents));

            await Assert.That(vm.MenuModel.Agents[0].StopEnabled).IsTrue();

            var gate = ops.ArmStop();
            actions.RequestStop("a", "agent · claude · —", "agent");

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
            var actions = new AgentActionService(ops, notifier, new RecordingOpener(), service.SnapshotsSubject, CancellationToken.None, NeverConfirm.Confirm);
            var consent = new FakeConsentService();
            using var vm = new TrayViewModel(service, pause, actions, consent);

            var agents = new List<AgentStatusDto> {
                new("a", "agent", "claude", "/repos/kcap-cli", "Running", null, null, null, DateTime.UtcNow, null, null),
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

    // Proves TrayAgentEntry.Kind is actually threaded from the snapshot through to
    // AgentActionService.RequestStop (decision 5) — a protected kind clicked from the tray goes
    // through the confirm seam and, once confirmed, stops with force:true.
    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task StopAgentCommand_for_a_protected_kind_entry_goes_through_confirm_with_force() {
        await AvaloniaSession.WithImmediateRxScheduler(async () => {
            var service = new FakeDaemonClientService();
            var pause = new FakePauseController();
            var ops = new ScriptedLocalControlOps();
            var confirmer = new RecordingConfirmer();
            var actions = new AgentActionService(ops, new RecordingNotifier(), new RecordingOpener(), service.SnapshotsSubject, CancellationToken.None, confirmer.Confirm);
            var consent = new FakeConsentService();
            using var vm = new TrayViewModel(service, pause, actions, consent);

            var agents = new List<AgentStatusDto> {
                new("a", "review-flow", "codex", "/repos/kcap-cli", "Running", null, null, null, DateTime.UtcNow, null, null),
            };
            service.StatusSubject.OnNext(new AttachStatus(AttachState.Connected, null, []));
            service.SnapshotsSubject.OnNext(Snap("connected", 1, agents));

            confirmer.Queue(true);
            ops.QueueStop(new StopAgentResult(true, "stopped", null));
            await vm.StopAgentCommand.Execute("a").ToTask();

            await WaitUntilAsync(() => ops.StopCalls >= 1, what: "stop issued after confirm");
            await Assert.That(confirmer.Prompted).IsEquivalentTo(["review-flow · codex · kcap-cli"], CollectionOrdering.Matching);
            await Assert.That(ops.StopPayloads).IsEquivalentTo([("a", true)], CollectionOrdering.Matching);
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
            var actions = new AgentActionService(ops, new RecordingNotifier(), opener, service.SnapshotsSubject, CancellationToken.None, NeverConfirm.Confirm);
            var consent = new FakeConsentService();
            using var vm = new TrayViewModel(service, pause, actions, consent);

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
            var consent = new FakeConsentService();
            using var vm = new TrayViewModel(service, pause, actions, consent, openMainWindow: () => calls++);

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
            var consent = new FakeConsentService();
            using var vm = new TrayViewModel(service, pause, actions, consent, quit: () => calls++);

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
            var consent = new FakeConsentService();
            using var vm = new TrayViewModel(service, pause, actions, consent); // no delegates injected

            await vm.OpenMainWindowCommand.Execute().ToTask();
            await vm.QuitCommand.Execute().ToTask();
            await vm.ReviewPendingCommand.Execute().ToTask();
        });
    }

    // ---- §8 pending-consent Attention row + Review menu item ----

    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task Pending_consent_asserts_attention_over_idle_and_running() {
        await AvaloniaSession.WithImmediateRxScheduler(async () => {
            var service = new FakeDaemonClientService();
            var pause = new FakePauseController();
            var actions = NewActions(service);
            var consent = new FakeConsentService();
            using var vm = new TrayViewModel(service, pause, actions, consent);

            service.StatusSubject.OnNext(new AttachStatus(AttachState.Connected, null, []));
            service.SnapshotsSubject.OnNext(Snap("connected", 0));
            consent.Add(Entry("a1", "p1"));

            await Assert.That(vm.MenuModel.State).IsEqualTo(TrayState.Attention);
            await Assert.That(vm.MenuModel.Header).IsEqualTo("daemon-a: 1 launch awaiting approval");
            await Assert.That(vm.MenuModel.RunningCount).IsEqualTo(0);
            await Assert.That(vm.MenuModel.PendingConsent).IsEqualTo(1);

            // 2 agents running + pendingCount 3 -> still Attention, plural copy, badge keeps 2.
            service.SnapshotsSubject.OnNext(Snap("connected", 2));
            consent.Add(Entry("a2", "p2", requestedAt: T0.AddSeconds(1)));
            consent.Add(Entry("a3", "p3", requestedAt: T0.AddSeconds(2)));

            await Assert.That(vm.MenuModel.State).IsEqualTo(TrayState.Attention);
            await Assert.That(vm.MenuModel.Header).IsEqualTo("daemon-a: 3 launches awaiting approval");
            await Assert.That(vm.MenuModel.RunningCount).IsEqualTo(2);
            await Assert.That(vm.MenuModel.PendingConsent).IsEqualTo(3);
        });
    }

    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task Connection_trouble_rows_keep_precedence() {
        await AvaloniaSession.WithImmediateRxScheduler(async () => {
            var service = new FakeDaemonClientService();
            var pause = new FakePauseController();
            var actions = NewActions(service);
            var consent = new FakeConsentService();
            using var vm = new TrayViewModel(service, pause, actions, consent);

            for (var i = 0; i < 5; i++) consent.Add(Entry($"a{i}", $"p{i}", requestedAt: T0.AddSeconds(i)));

            service.StatusSubject.OnNext(new AttachStatus(AttachState.Unreachable, "daemon_unreachable", null));
            await Assert.That(vm.MenuModel.State).IsEqualTo(TrayState.Stopped); // row 1 still wins
            await Assert.That(vm.MenuModel.PendingConsent).IsEqualTo(5);

            service.StatusSubject.OnNext(new AttachStatus(AttachState.Connected, null, []));
            service.SnapshotsSubject.OnNext(Snap("reconnecting"));

            await Assert.That(vm.MenuModel.State).IsEqualTo(TrayState.Attention);
            await Assert.That(vm.MenuModel.Header).IsEqualTo("daemon-a: reconnecting to server"); // row 5's copy wins
        });
    }

    // ---- AI-1654 Task 22: ILifecycleSurface.Attention ----

    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task Lifecycle_attention_upgrades_idle_and_supplies_its_own_header_text() {
        await AvaloniaSession.WithImmediateRxScheduler(async () => {
            var service = new FakeDaemonClientService();
            var pause = new FakePauseController();
            var actions = NewActions(service);
            var consent = new FakeConsentService();
            var lifecycleAttention = new BehaviorSubject<string?>(null);
            using var vm = new TrayViewModel(service, pause, actions, consent, lifecycleAttention: lifecycleAttention);

            service.StatusSubject.OnNext(new AttachStatus(AttachState.Connected, null, []));
            service.SnapshotsSubject.OnNext(Snap("connected", 0));
            await Assert.That(vm.MenuModel.State).IsEqualTo(TrayState.Idle);

            lifecycleAttention.OnNext("restore-verification failed — see terminal for repair steps");

            await Assert.That(vm.MenuModel.State).IsEqualTo(TrayState.Attention);
            await Assert.That(vm.MenuModel.Header).IsEqualTo("daemon-a: restore-verification failed — see terminal for repair steps");
        });
    }

    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task Lifecycle_attention_never_downgrades_a_connection_trouble_row() {
        await AvaloniaSession.WithImmediateRxScheduler(async () => {
            var service = new FakeDaemonClientService();
            var pause = new FakePauseController();
            var actions = NewActions(service);
            var consent = new FakeConsentService();
            var lifecycleAttention = new BehaviorSubject<string?>("orphan label needs repair");
            using var vm = new TrayViewModel(service, pause, actions, consent, lifecycleAttention: lifecycleAttention);

            service.StatusSubject.OnNext(new AttachStatus(AttachState.Unreachable, "daemon_unreachable", null));

            await Assert.That(vm.MenuModel.State).IsEqualTo(TrayState.Stopped); // row 1 still wins
            await Assert.That(vm.MenuModel.Header).IsEqualTo("daemon-a: not running"); // untouched by the lifecycle text
        });
    }

    /// Fix round 1: rows that resolve to Attention ON THEIR OWN (2, 5, 6, 9, 10) — as opposed to
    /// row 1 (Stopped), the only row the test above exercises — are the ones a co-occurring
    /// lifecycle Attention could actually collide with, since both land on TrayState.Attention.
    /// Each row's own text must still win; only a genuinely fine baseState (Idle/Running) may ever
    /// yield its header to the lifecycle message.
    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task Lifecycle_attention_never_masks_a_connection_trouble_rows_own_header_text() {
        await AvaloniaSession.WithImmediateRxScheduler(async () => {
            var service = new FakeDaemonClientService();
            var pause = new FakePauseController();
            var actions = NewActions(service);
            var consent = new FakeConsentService();
            var lifecycleAttention = new BehaviorSubject<string?>("orphan label needs repair");
            using var vm = new TrayViewModel(service, pause, actions, consent, lifecycleAttention: lifecycleAttention);

            // Row 2: daemon_incompatible — the skew special-case (no daemon-name prefix) wins even
            // over the connection-trouble exemption below.
            service.StatusSubject.OnNext(new AttachStatus(AttachState.Unreachable, "daemon_incompatible", null));
            await Assert.That(vm.MenuModel.State).IsEqualTo(TrayState.Attention);
            await Assert.That(vm.MenuModel.Header).IsEqualTo("app and daemon are incompatible — make sure both are up to date");

            // Row 10: an unrecognized Unreachable reason.
            service.StatusSubject.OnNext(new AttachStatus(AttachState.Unreachable, "some_future_reason", null));
            await Assert.That(vm.MenuModel.State).IsEqualTo(TrayState.Attention);
            await Assert.That(vm.MenuModel.Header).IsEqualTo("daemon-a: needs attention");

            service.StatusSubject.OnNext(new AttachStatus(AttachState.Connected, null, []));

            // Row 5: reconnecting.
            service.SnapshotsSubject.OnNext(Snap("reconnecting"));
            await Assert.That(vm.MenuModel.State).IsEqualTo(TrayState.Attention);
            await Assert.That(vm.MenuModel.Header).IsEqualTo("daemon-a: reconnecting to server");

            // Row 5: disconnected.
            service.SnapshotsSubject.OnNext(Snap("disconnected"));
            await Assert.That(vm.MenuModel.State).IsEqualTo(TrayState.Attention);
            await Assert.That(vm.MenuModel.Header).IsEqualTo("daemon-a: disconnected from server");

            // Row 6: connected, malformed (negative) active-agent count.
            service.SnapshotsSubject.OnNext(Snap("connected", -1));
            await Assert.That(vm.MenuModel.State).IsEqualTo(TrayState.Attention);
            await Assert.That(vm.MenuModel.Header).IsEqualTo("daemon-a: needs attention");

            // Row 9: unrecognized connection value.
            service.SnapshotsSubject.OnNext(Snap("weird"));
            await Assert.That(vm.MenuModel.State).IsEqualTo(TrayState.Attention);
            await Assert.That(vm.MenuModel.Header).IsEqualTo("daemon-a: needs attention");
        });
    }

    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task Lifecycle_attention_wins_header_text_over_pending_consent() {
        await AvaloniaSession.WithImmediateRxScheduler(async () => {
            var service = new FakeDaemonClientService();
            var pause = new FakePauseController();
            var actions = NewActions(service);
            var consent = new FakeConsentService();
            var lifecycleAttention = new BehaviorSubject<string?>("orphan label needs repair");
            using var vm = new TrayViewModel(service, pause, actions, consent, lifecycleAttention: lifecycleAttention);

            service.StatusSubject.OnNext(new AttachStatus(AttachState.Connected, null, []));
            service.SnapshotsSubject.OnNext(Snap("connected", 0));
            consent.Add(Entry("a1", "p1"));

            await Assert.That(vm.MenuModel.State).IsEqualTo(TrayState.Attention);
            await Assert.That(vm.MenuModel.Header).IsEqualTo("daemon-a: orphan label needs repair");
            await Assert.That(vm.MenuModel.PendingConsent).IsEqualTo(1); // the badge count is unaffected
        });
    }

    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task ReviewPendingCommand_invokes_the_injected_delegate() {
        await AvaloniaSession.WithImmediateRxScheduler(async () => {
            var service = new FakeDaemonClientService();
            var pause = new FakePauseController();
            var actions = NewActions(service);
            var consent = new FakeConsentService();
            var calls = 0;
            using var vm = new TrayViewModel(service, pause, actions, consent, openReviewPrompts: () => calls++);

            await vm.ReviewPendingCommand.Execute().ToTask();

            await Assert.That(calls).IsEqualTo(1);
        });
    }
}
