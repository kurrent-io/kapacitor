using Avalonia;
using Avalonia.Controls;
using Capacitor.App.Services;
using Capacitor.App.ViewModels;
using Capacitor.App.Views;
using Capacitor.Cli.Core.LocalIpc;

namespace Capacitor.App.Tests.Unit;

/// Task 6: the thin native-menu/tray-icon adapter (spec §4 icon, §5 menu + rebuild cadence, §6
/// frozen-desired-value capture). CountBadge and TrayMenuSync are pure — no Avalonia types — so
/// they run without a headless session. Everything touching real Avalonia types (NativeMenu,
/// TrayIcon, Application resources) runs inside AvaloniaSession.DispatchAsync, wrapped in
/// WithImmediateRxScheduler wherever a TrayViewModel is constructed (its MenuModel OAPH rides
/// RxSchedulers.MainThreadScheduler) — the same pattern as TrayViewModelTests.
public class TrayAdapterTests {
    static AgentActionService NewActions(FakeDaemonClientService service) =>
        new(new ScriptedLocalControlOps(), new RecordingNotifier(), new RecordingOpener(), service.SnapshotsSubject, CancellationToken.None, NeverConfirm.Confirm);

    static TrayMenuModel Model(
            TrayState state = TrayState.Idle, int count = 0, string header = "hdr",
            IReadOnlyList<TrayAgentEntry>? agents = null, TrayPauseItem? pause = null, int pendingConsent = 0,
            bool shimInstallVisible = false) =>
        new(state, count, header, agents ?? [], pause ?? new TrayPauseItem(Enabled: true, Checked: false), pendingConsent,
            shimInstallVisible);

    // ---- CountBadge (pure) ----

    [Test]
    [Arguments(0, "0")]   // unreachable in practice (Running only when active_agents > 0) but defined
    [Arguments(1, "1")]
    [Arguments(9, "9")]
    [Arguments(10, "9+")]
    [Arguments(42, "9+")]
    public async Task CountBadge_maps_count_to_digit_or_plus(int count, string expected) {
        await Assert.That(TrayIconRenderer.CountBadge(count)).IsEqualTo(expected);
    }

    // ---- TrayMenuSync (pure) ----

    [Test]
    public async Task OnModelChanged_marks_dirty() {
        var sync = new TrayMenuSync();
        sync.OnModelChanged(Model());
        await Assert.That(sync.Dirty).IsTrue();
    }

    [Test]
    public async Task OnNeedsUpdate_rebuilds_once_from_latest_and_clears_dirty() {
        var sync = new TrayMenuSync();
        var seen = new List<TrayMenuModel>();
        sync.OnModelChanged(Model(header: "first"));

        sync.OnNeedsUpdate(seen.Add);

        await Assert.That(seen.Count).IsEqualTo(1);
        await Assert.That(seen[0].Header).IsEqualTo("first");
        await Assert.That(sync.Dirty).IsFalse();
    }

    [Test]
    public async Task OnNeedsUpdate_without_a_prior_change_does_not_rebuild() {
        var sync = new TrayMenuSync();
        var calls = 0;

        sync.OnNeedsUpdate(_ => calls++);

        await Assert.That(calls).IsEqualTo(0);
    }

    [Test]
    public async Task OnNeedsUpdate_called_twice_in_a_row_rebuilds_only_the_first_time() {
        var sync = new TrayMenuSync();
        var calls = 0;
        sync.OnModelChanged(Model());

        sync.OnNeedsUpdate(_ => calls++);
        sync.OnNeedsUpdate(_ => calls++);

        await Assert.That(calls).IsEqualTo(1);
    }

    [Test]
    public async Task Change_while_conceptually_open_is_visible_only_at_the_next_NeedsUpdate() {
        var sync = new TrayMenuSync();
        var seen = new List<TrayMenuModel>();
        sync.OnModelChanged(Model(header: "v1"));
        sync.OnNeedsUpdate(seen.Add); // consumes v1, clears dirty — simulates the menu now "open"

        sync.OnModelChanged(Model(header: "v2")); // arrives while conceptually open
        await Assert.That(seen.Count).IsEqualTo(1); // not applied yet

        sync.OnNeedsUpdate(seen.Add); // next open — now visible
        await Assert.That(seen.Count).IsEqualTo(2);
        await Assert.That(seen[1].Header).IsEqualTo("v2");
    }

    // ---- TrayIconRenderer.Get (needs a real Avalonia asset loader for Assets/kcap-icon.png) ----

    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task Get_returns_a_non_null_icon_for_every_state() {
        await AvaloniaSession.DispatchAsync(async () => {
            foreach (var state in Enum.GetValues<TrayState>())
                await Assert.That(TrayIconRenderer.Get(state, state == TrayState.Running ? 3 : 0)).IsNotNull();
            return true;
        });
    }

    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task Get_caches_the_same_reference_for_the_same_key() {
        var same = await AvaloniaSession.DispatchAsync(() =>
            ReferenceEquals(TrayIconRenderer.Get(TrayState.Idle, 0), TrayIconRenderer.Get(TrayState.Idle, 0)));

        await Assert.That(same).IsTrue();
    }

    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task Get_caps_running_counts_above_the_digit_cap_to_the_same_cached_icon() {
        var same = await AvaloniaSession.DispatchAsync(() =>
            ReferenceEquals(TrayIconRenderer.Get(TrayState.Running, 10), TrayIconRenderer.Get(TrayState.Running, 999)));

        await Assert.That(same).IsTrue();
    }

    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task Get_returns_different_references_for_different_states() {
        var different = await AvaloniaSession.DispatchAsync(() =>
            !ReferenceEquals(TrayIconRenderer.Get(TrayState.Stopped, 0), TrayIconRenderer.Get(TrayState.Attention, 0)));

        await Assert.That(different).IsTrue();
    }

    // ---- TrayMenuBuilder structure (spec §5, §6) ----

    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task Rebuild_sets_a_disabled_header_item_with_the_model_text() {
        await AvaloniaSession.WithImmediateRxScheduler(async () => {
            var (headerText, headerEnabled) = await AvaloniaSession.DispatchAsync(() => {
                var service = new FakeDaemonClientService();
                var pause = new FakePauseController();
                var consent = new FakeConsentService();
                using var vm = new TrayViewModel(service, pause, NewActions(service), consent);
                var builder = new TrayMenuBuilder(vm);
                var menu = new NativeMenu();

                builder.Rebuild(menu, Model(header: "daemon-a: connected — 2 agent(s) running"));

                var header = (NativeMenuItem)menu.Items[0];
                return (header.Header, header.IsEnabled);
            });

            await Assert.That(headerText).IsEqualTo("daemon-a: connected — 2 agent(s) running");
            await Assert.That(headerEnabled).IsFalse();
        });
    }

    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task Rebuild_omits_the_agent_section_when_there_are_no_agents() {
        await AvaloniaSession.WithImmediateRxScheduler(async () => {
            var (itemCount, separatorCount) = await AvaloniaSession.DispatchAsync(() => {
                var service = new FakeDaemonClientService();
                var pause = new FakePauseController();
                var consent = new FakeConsentService();
                using var vm = new TrayViewModel(service, pause, NewActions(service), consent);
                var builder = new TrayMenuBuilder(vm);
                var menu = new NativeMenu();

                builder.Rebuild(menu, Model(agents: []));

                return (menu.Items.Count, menu.Items.OfType<NativeMenuItemSeparator>().Count());
            });

            // header, sep, pause, open, sep, quit — no agent submenu items, exactly 2 separators.
            await Assert.That(itemCount).IsEqualTo(6);
            await Assert.That(separatorCount).IsEqualTo(2);
        });
    }

    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task Rebuild_builds_agent_submenu_items_with_correct_parameters_and_enablement() {
        await AvaloniaSession.WithImmediateRxScheduler(async () => {
            var result = await AvaloniaSession.DispatchAsync(() => {
                var service = new FakeDaemonClientService();
                var pause = new FakePauseController();
                var consent = new FakeConsentService();
                using var vm = new TrayViewModel(service, pause, NewActions(service), consent);
                var builder = new TrayMenuBuilder(vm);
                var menu = new NativeMenu();

                var agents = new List<TrayAgentEntry> {
                    new("a1", "agent · claude · repo-one", "agent", StopEnabled: true),
                    new("a2", "review-flow · codex · —", "review-flow", StopEnabled: false),
                };
                builder.Rebuild(menu, Model(agents: agents));

                var agentItem1 = (NativeMenuItem)menu.Items[2];
                var agentItem2 = (NativeMenuItem)menu.Items[3];
                var stop1 = (NativeMenuItem)agentItem1.Menu!.Items[0];
                var openWeb1 = (NativeMenuItem)agentItem1.Menu!.Items[1];
                var stop2 = (NativeMenuItem)agentItem2.Menu!.Items[0];

                return (
                    agentItem1.Header, agentItem2.Header,
                    stop1.Header, stop1.CommandParameter, stop1.IsEnabled, ReferenceEquals(stop1.Command, vm.StopAgentCommand),
                    openWeb1.Header, openWeb1.CommandParameter, ReferenceEquals(openWeb1.Command, vm.OpenInWebCommand),
                    stop2.IsEnabled,
                    // trailing separator after the agent section, then pause/open/sep/quit = 4 more items
                    menu.Items.Count);
            });

            await Assert.That(result.Item1).IsEqualTo("agent · claude · repo-one");
            await Assert.That(result.Item2).IsEqualTo("review-flow · codex · —");
            await Assert.That(result.Item3).IsEqualTo("Stop");
            await Assert.That(result.Item4).IsEqualTo("a1");
            await Assert.That(result.Item5).IsTrue();
            await Assert.That(result.Item6).IsTrue();
            await Assert.That(result.Item7).IsEqualTo("Open in web");
            await Assert.That(result.Item8).IsEqualTo("a1");
            await Assert.That(result.Item9).IsTrue();
            await Assert.That(result.Item10).IsFalse(); // stop2.IsEnabled — StopEnabled: false
            await Assert.That(result.Item11).IsEqualTo(9); // header, sep, 2 agents, sep, pause, open, sep, quit
        });
    }

    // ---- Review pending launches item (spec §8) ----

    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task Rebuild_omits_the_review_item_when_no_pending_consent() {
        await AvaloniaSession.WithImmediateRxScheduler(async () => {
            var hasReviewItem = await AvaloniaSession.DispatchAsync(() => {
                var service = new FakeDaemonClientService();
                var pause = new FakePauseController();
                var consent = new FakeConsentService();
                using var vm = new TrayViewModel(service, pause, NewActions(service), consent);
                var builder = new TrayMenuBuilder(vm);
                var menu = new NativeMenu();

                builder.Rebuild(menu, Model(agents: [], pendingConsent: 0));

                return menu.Items.OfType<NativeMenuItem>().Any(i => i.Header == "Review pending launches…");
            });

            await Assert.That(hasReviewItem).IsFalse();
        });
    }

    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task Rebuild_includes_review_item_between_agents_and_pause_when_pending_consent_positive() {
        await AvaloniaSession.WithImmediateRxScheduler(async () => {
            var (header, reviewIndex, pauseIndex, commandMatches) = await AvaloniaSession.DispatchAsync(() => {
                var service = new FakeDaemonClientService();
                var pause = new FakePauseController();
                var consent = new FakeConsentService();
                using var vm = new TrayViewModel(service, pause, NewActions(service), consent);
                var builder = new TrayMenuBuilder(vm);
                var menu = new NativeMenu();

                var agents = new List<TrayAgentEntry> {
                    new("a1", "agent · claude · repo-one", "agent", StopEnabled: true),
                };
                builder.Rebuild(menu, Model(agents: agents, pendingConsent: 3));

                var items = menu.Items.OfType<NativeMenuItem>().ToList();
                var review = items.First(i => i.Header == "Review pending launches…");
                var pauseItem = items.First(i => i.Header == "Pause new launches");

                return (review.Header, menu.Items.IndexOf(review), menu.Items.IndexOf(pauseItem),
                    ReferenceEquals(review.Command, vm.ReviewPendingCommand));
            });

            await Assert.That(header).IsEqualTo("Review pending launches…");
            await Assert.That(reviewIndex).IsEqualTo(pauseIndex - 1); // immediately before the pause toggle
            await Assert.That(commandMatches).IsTrue();
        });
    }

    // Regression coverage (review Critical 1): NativeMenuItem.OnPropertyChanged recomputes
    // IsEnabled from Command.CanExecute(CommandParameter) whenever Command is (re)assigned
    // (decompiler-verified) — so IsEnabled must be the LAST property set in BuildPauseItem's
    // initializer, or an explicit Enabled: false silently comes back as IsEnabled == true. Covers
    // Enabled: false for BOTH Checked states, not just the Enabled: true cases that let this slip
    // originally.
    [Test]
    [NotInParallel("AvaloniaSession")]
    [Arguments(false, true, true)]    // unchecked, enabled -> desired true
    [Arguments(true, false, true)]    // checked, enabled -> desired false
    [Arguments(false, true, false)]   // unchecked, DISABLED -> desired true, IsEnabled must stay false
    [Arguments(true, false, false)]   // checked, DISABLED -> desired false, IsEnabled must stay false
    public async Task Rebuild_pause_item_freezes_desired_value_and_preserves_enablement(
            bool checkedNow, bool expectedDesired, bool enabledNow) {
        await AvaloniaSession.WithImmediateRxScheduler(async () => {
            var (toggleType, isChecked, isEnabled, parameter, commandMatches) = await AvaloniaSession.DispatchAsync(() => {
                var service = new FakeDaemonClientService();
                var pause = new FakePauseController();
                var consent = new FakeConsentService();
                using var vm = new TrayViewModel(service, pause, NewActions(service), consent);
                var builder = new TrayMenuBuilder(vm);
                var menu = new NativeMenu();

                builder.Rebuild(menu, Model(pause: new TrayPauseItem(Enabled: enabledNow, Checked: checkedNow)));

                var pauseItem = menu.Items.OfType<NativeMenuItem>().First(i => i.Header == "Pause new launches");
                return (pauseItem.ToggleType, pauseItem.IsChecked, pauseItem.IsEnabled, (bool)pauseItem.CommandParameter!,
                    ReferenceEquals(pauseItem.Command, vm.TogglePauseCommand));
            });

            await Assert.That(toggleType).IsEqualTo(MenuItemToggleType.CheckBox);
            await Assert.That(isChecked).IsEqualTo(checkedNow);
            await Assert.That(isEnabled).IsEqualTo(enabledNow);
            await Assert.That(parameter).IsEqualTo(expectedDesired);
            await Assert.That(commandMatches).IsTrue();
        });
    }

    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task Rebuild_includes_open_and_quit_items_wired_to_vm_commands() {
        await AvaloniaSession.WithImmediateRxScheduler(async () => {
            var (openHeader, openMatches, quitHeader, quitMatches) = await AvaloniaSession.DispatchAsync(() => {
                var service = new FakeDaemonClientService();
                var pause = new FakePauseController();
                var consent = new FakeConsentService();
                using var vm = new TrayViewModel(service, pause, NewActions(service), consent);
                var builder = new TrayMenuBuilder(vm);
                var menu = new NativeMenu();

                builder.Rebuild(menu, Model(agents: []));

                var items = menu.Items.OfType<NativeMenuItem>().ToList();
                var open = items.First(i => i.Header == "Open Kurrent Capacitor");
                var quit = items.First(i => i.Header == "Quit");
                return (open.Header, ReferenceEquals(open.Command, vm.OpenMainWindowCommand),
                    quit.Header, ReferenceEquals(quit.Command, vm.QuitCommand));
            });

            await Assert.That(openHeader).IsEqualTo("Open Kurrent Capacitor");
            await Assert.That(openMatches).IsTrue();
            await Assert.That(quitHeader).IsEqualTo("Quit");
            await Assert.That(quitMatches).IsTrue();
        });
    }

    // ---- "Install command-line tool…" item (AI-1654 §5, Task 24) ----

    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task Rebuild_omits_the_shim_item_when_not_offerable() {
        await AvaloniaSession.WithImmediateRxScheduler(async () => {
            var hasShimItem = await AvaloniaSession.DispatchAsync(() => {
                var service = new FakeDaemonClientService();
                var pause = new FakePauseController();
                var consent = new FakeConsentService();
                using var vm = new TrayViewModel(service, pause, NewActions(service), consent);
                var builder = new TrayMenuBuilder(vm);
                var menu = new NativeMenu();

                builder.Rebuild(menu, Model(agents: [], shimInstallVisible: false));

                return menu.Items.OfType<NativeMenuItem>().Any(i => i.Header == "Install command-line tool…");
            });

            await Assert.That(hasShimItem).IsFalse();
        });
    }

    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task Rebuild_includes_the_shim_item_between_open_and_quit_when_offerable() {
        await AvaloniaSession.WithImmediateRxScheduler(async () => {
            var (header, shimIndex, openIndex, quitIndex, commandMatches) = await AvaloniaSession.DispatchAsync(() => {
                var service = new FakeDaemonClientService();
                var pause = new FakePauseController();
                var consent = new FakeConsentService();
                using var vm = new TrayViewModel(service, pause, NewActions(service), consent);
                var builder = new TrayMenuBuilder(vm);
                var menu = new NativeMenu();

                builder.Rebuild(menu, Model(agents: [], shimInstallVisible: true));

                var items = menu.Items.OfType<NativeMenuItem>().ToList();
                var shim = items.First(i => i.Header == "Install command-line tool…");
                var open = items.First(i => i.Header == "Open Kurrent Capacitor");
                var quit = items.First(i => i.Header == "Quit");

                return (shim.Header, menu.Items.IndexOf(shim), menu.Items.IndexOf(open), menu.Items.IndexOf(quit),
                    ReferenceEquals(shim.Command, vm.InstallShimCommand));
            });

            await Assert.That(header).IsEqualTo("Install command-line tool…");
            await Assert.That(shimIndex).IsEqualTo(openIndex + 1); // immediately after "Open Kurrent Capacitor"
            await Assert.That(shimIndex).IsLessThan(quitIndex); // still before the trailing separator + Quit
            await Assert.That(commandMatches).IsTrue();
        });
    }

    // ---- TrayIconManager wiring (spec §5) ----
    //
    // NativeMenu.NeedsUpdate can only be RAISED through INativeMenuExporterEventsImplBridge, and
    // decompiling Avalonia.Controls's REFERENCE assembly (the one the compiler binds against, not
    // the runtime one) shows its Raise* members are `internal` there — external code cannot
    // cast-and-call them, by design (only Avalonia's own native exporters may raise them). The
    // headless platform's CreateTrayIcon() also returns null (decompiler-verified,
    // AvaloniaHeadlessPlatform.HeadlessWindowingPlatform), so there is no exporter to drive it even
    // indirectly. The NeedsUpdate-only rebuild and its pause-state refresh kick (moved here from
    // NativeMenu.Opening, which macOS status-item menus never raise — found in manual acceptance)
    // are therefore proven at the TrayMenuSync/TrayMenuBuilder unit level above (pure, real
    // event-independent) plus manual macOS acceptance (spec §12); what's left testable here is
    // TrayIconManager's own construction/disposal behavior. The edge-triggered on-Connected kick
    // (spec §6) is covered directly on TrayViewModel in TrayViewModelTests, without needing a real
    // NativeMenu event at all.

    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task Construction_sets_the_icon_and_tooltip_but_leaves_the_menu_empty_until_NeedsUpdate() {
        await AvaloniaSession.WithImmediateRxScheduler(async () => {
            var (icon, tooltip, itemCountBeforeNeedsUpdate) = await AvaloniaSession.DispatchAsync(() => {
                var service = new FakeDaemonClientService();
                var pause = new FakePauseController();
                var consent = new FakeConsentService();
                using var vm = new TrayViewModel(service, pause, NewActions(service), consent);
                service.StatusSubject.OnNext(new AttachStatus(AttachState.Connected, null, []));
                service.SnapshotsSubject.OnNext(FakeDaemonClientService.Snap(connection: "connected", active: 2));

                var app = Application.Current!;
                using var manager = new TrayIconManager(app, vm);
                var trayIcon = TrayIcon.GetIcons(app)!.Single();

                return (trayIcon.Icon, trayIcon.ToolTipText, trayIcon.Menu!.Items.Count);
            });

            await Assert.That(icon).IsNotNull();
            await Assert.That(tooltip).IsEqualTo("Kurrent Capacitor");
            await Assert.That(itemCountBeforeNeedsUpdate).IsEqualTo(0);
        });
    }

    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task Icon_updates_immediately_on_model_change_without_NeedsUpdate() {
        await AvaloniaSession.WithImmediateRxScheduler(async () => {
            var iconChanged = await AvaloniaSession.DispatchAsync(() => {
                var service = new FakeDaemonClientService();
                var pause = new FakePauseController();
                var consent = new FakeConsentService();
                using var vm = new TrayViewModel(service, pause, NewActions(service), consent);

                var app = Application.Current!;
                using var manager = new TrayIconManager(app, vm);
                var trayIcon = TrayIcon.GetIcons(app)!.Single();
                var initialIcon = trayIcon.Icon;

                service.StatusSubject.OnNext(new AttachStatus(AttachState.Connected, null, []));
                service.SnapshotsSubject.OnNext(FakeDaemonClientService.Snap(connection: "connected", active: 3));

                // No NeedsUpdate raised — the icon must already reflect Running(3).
                return !ReferenceEquals(initialIcon, trayIcon.Icon)
                    && ReferenceEquals(trayIcon.Icon, TrayIconRenderer.Get(TrayState.Running, 3));
            });

            await Assert.That(iconChanged).IsTrue();
        });
    }

    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task Dispose_detaches_and_disposes_the_tray_icon() {
        await AvaloniaSession.WithImmediateRxScheduler(async () => {
            var iconsAfterDispose = await AvaloniaSession.DispatchAsync(() => {
                var service = new FakeDaemonClientService();
                var pause = new FakePauseController();
                var consent = new FakeConsentService();
                using var vm = new TrayViewModel(service, pause, NewActions(service), consent);

                var app = Application.Current!;
                var manager = new TrayIconManager(app, vm);
                var registered = TrayIcon.GetIcons(app)!.Count;

                manager.Dispose();

                var afterCount = TrayIcon.GetIcons(app)?.Count ?? 0;
                return (registered, afterCount);
            });

            await Assert.That(iconsAfterDispose.registered).IsEqualTo(1);
            await Assert.That(iconsAfterDispose.afterCount).IsEqualTo(0);
        });
    }
}
