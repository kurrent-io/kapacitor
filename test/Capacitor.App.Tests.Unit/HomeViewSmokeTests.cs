using System.Collections;
using System.Collections.Specialized;
using System.Reactive.Linq;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Capacitor.App.Services;
using Capacitor.App.ViewModels;
using Capacitor.App.Views;
using Capacitor.Cli.Core.LocalIpc;
using DynamicData;

namespace Capacitor.App.Tests.Unit;

/// Headless rendering acceptance for the Home tab. HomeView is a UserControl,
/// not a Window (unlike MainWindow) — each test hosts it inside a plain Window purely to give
/// headless something to Show(); session setup and control lookup otherwise copy
/// MainWindowSmokeTests exactly (see that file's own header comment).
public class HomeViewSmokeTests {
    /// Real-shaped agent ids (Guid("N"), 32 hex digits): a Started outcome carrying anything else
    /// is HomeViewModel's "launched but unopenable" error, which would keep StartErrorText visible.
    const string LaunchedId = "0123456789abcdef0123456789abcdef";
    const string SecondLaunchedId = "fedcba9876543210fedcba9876543210";

    sealed class RecordingLaunchClient : ILaunchClient {
        public LaunchOutcome Next = new(true, LaunchedId, null);
        public int StartCount;

        public Task<LaunchOutcome> StartAsync(LaunchRequest request, CancellationToken ct) {
            StartCount++;
            return Task.FromResult(Next);
        }
    }

    static (HomeView View, HomeViewModel Vm, FakeDaemonClientService Service, RecordingLaunchClient Launch, TempDir Tmp) Build() {
        var tmp = TempDir.WithPathTo("app-state.json", out var path);
        var service = new FakeDaemonClientService();
        // Connected steady state: StartCommand's canExecute gates on daemon + server both up.
        service.SnapshotsSubject.OnNext(FakeDaemonClientService.Snap());
        service.StatusSubject.OnNext(new AttachStatus(AttachState.Connected, null, null));
        var launch = new RecordingLaunchClient();
        var vm = new HomeViewModel(service, new AppStateStore(path), launch, () => Task.FromResult(Array.Empty<string>()));
        return (new HomeView { DataContext = vm }, vm, service, launch, tmp);
    }

    static T? Find<T>(Window window, string name) where T : Control =>
        window.GetVisualDescendants().OfType<T>().FirstOrDefault(c => c.Name == name);

    // ItemsControl.ItemsSource is an IEnumerable?; Sessions (ReadOnlyObservableCollection<T>) is
    // also an ICollection, so this reads the bound source's count directly — no dependency on a
    // realized visual tree / layout pass.
    static int ItemCount(ItemsControl items) => items.ItemsSource is ICollection c ? c.Count : -1;

    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task LauncherPane_resolves_its_named_controls() {
        var found = await AvaloniaSession.DispatchAsync(() => {
            var (_, vm, _, _, tmp) = Build();
            using var _tmp = tmp;
            var window = new Window { Content = new LauncherPaneView { DataContext = vm } };
            window.Show();
            Dispatcher.UIThread.RunJobs();

            var names = new[] {
                "GoalInput", "RepositoryChip", "AgentChip", "EffortChip", "PermissionChip", "StartButton",
                "StartErrorText", "ConnectionNoticeText", "DaemonStartMessageText",
                "StartDaemonButton", "RetryDaemonButton", "HomeSignInButton",
            };
            var resolved = names.ToDictionary(name => name, name => Find<Control>(window, name) is not null);

            window.Close();
            Dispatcher.UIThread.RunJobs();
            vm.Dispose();
            return resolved;
        });

        await Assert.That(found["GoalInput"]).IsTrue();
        await Assert.That(found["RepositoryChip"]).IsTrue();
        await Assert.That(found["AgentChip"]).IsTrue();
        await Assert.That(found["EffortChip"]).IsTrue();
        await Assert.That(found["PermissionChip"]).IsTrue();
        await Assert.That(found["StartButton"]).IsTrue();
        await Assert.That(found["StartErrorText"]).IsTrue();
        await Assert.That(found["ConnectionNoticeText"]).IsTrue();
        await Assert.That(found["DaemonStartMessageText"]).IsTrue();
        await Assert.That(found["StartDaemonButton"]).IsTrue();
        await Assert.That(found["RetryDaemonButton"]).IsTrue();
        await Assert.That(found["HomeSignInButton"]).IsTrue();
    }

    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task The_notice_and_sign_in_button_follow_the_server_connection() {
        var (noticeBefore, signInBefore, noticeAfter, noticeText, signInAfter) = await AvaloniaSession.DispatchAsync(() => {
            var (_, vm, service, _, tmp) = Build();
            using var _tmp = tmp;
            var window = new Window { Content = new LauncherPaneView { DataContext = vm } };
            window.Show();
            Dispatcher.UIThread.RunJobs();

            var notice = Find<TextBlock>(window, "ConnectionNoticeText")!;
            var signIn = Find<Button>(window, "HomeSignInButton")!;
            // The banner Border owns visibility; the text/button stay in the tree.
            var banner = notice.FindAncestorOfType<Border>()!;
            var before = (banner.IsVisible, signIn.IsVisible);

            service.SnapshotsSubject.OnNext(FakeDaemonClientService.Snap(connection: "disconnected"));
            Dispatcher.UIThread.RunJobs();
            var after = (banner.IsVisible, notice.Text, signIn.IsVisible);

            window.Close();
            Dispatcher.UIThread.RunJobs();
            vm.Dispose();
            return (before.Item1, before.Item2, after.Item1, after.Item2, after.Item3);
        });

        await Assert.That(noticeBefore).IsFalse();
        await Assert.That(signInBefore).IsFalse();
        await Assert.That(noticeAfter).IsTrue();
        await Assert.That(noticeText).IsEqualTo(HomeViewModel.ServerLostNotice);
        await Assert.That(signInAfter).IsTrue();
    }

    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task PermissionChip_shows_for_claude_and_hides_for_other_vendors() {
        var (forClaude, forCodex) = await AvaloniaSession.DispatchAsync(async () => {
            var (_, vm, _, _, tmp) = Build();
            using var _tmp = tmp;
            var window = new Window { Content = new LauncherPaneView { DataContext = vm } };
            window.Show();
            Dispatcher.UIThread.RunJobs();

            var chip = Find<Button>(window, "PermissionChip");
            var claude = chip?.IsVisible ?? false;

            await vm.ChooseHarnessAsync("codex");
            Dispatcher.UIThread.RunJobs();
            var codex = chip?.IsVisible ?? true;

            window.Close();
            Dispatcher.UIThread.RunJobs();
            vm.Dispose();
            return (claude, codex);
        });

        await Assert.That(forClaude).IsTrue();
        await Assert.That(forCodex).IsFalse();
    }

    /// The button carries a glyph, not text, so assistive technology reads the automation name.
    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task StartButton_carries_an_accessible_name() {
        var name = await AvaloniaSession.DispatchAsync(() => {
            var (_, vm, _, _, tmp) = Build();
            using var _tmp = tmp;
            var window = new Window { Content = new LauncherPaneView { DataContext = vm } };
            window.Show();
            Dispatcher.UIThread.RunJobs();

            var startButton = Find<Button>(window, "StartButton")!;
            var automationName = AutomationProperties.GetName(startButton);

            window.Close();
            Dispatcher.UIThread.RunJobs();
            vm.Dispose();
            return automationName;
        });

        await Assert.That(name).IsEqualTo("Start");
    }

    /// Disabled Start still exposes why — hover tips are suppressed on disabled controls unless
    /// ShowOnDisabled is set, and the tip text must name the missing repository gate.
    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task StartButton_tooltip_explains_disabled_without_repository() {
        var (tipBefore, tipAfter, showOnDisabled) = await AvaloniaSession.DispatchAsync(async () => {
            var (_, vm, _, _, tmp) = Build();
            using var _tmp = tmp;
            var window = new Window { Content = new LauncherPaneView { DataContext = vm } };
            window.Show();
            Dispatcher.UIThread.RunJobs();

            var startButton = Find<Button>(window, "StartButton")!;
            var before = ToolTip.GetTip(startButton) as string;
            var show = ToolTip.GetShowOnDisabled(startButton);

            await vm.SelectRepositoryAsync("/repos/kcap-cli");
            Dispatcher.UIThread.RunJobs();
            var after = ToolTip.GetTip(startButton) as string;

            window.Close();
            Dispatcher.UIThread.RunJobs();
            vm.Dispose();
            return (before, after, show);
        });

        await Assert.That(showOnDisabled).IsTrue();
        await Assert.That(tipBefore).IsEqualTo("Select a repository to start");
        await Assert.That(tipAfter).IsEqualTo("Start");
    }

    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task StartButton_is_enabled_only_once_a_repository_is_selected() {
        var (enabledBefore, enabledAfter) = await AvaloniaSession.DispatchAsync(async () => {
            var (_, vm, _, _, tmp) = Build();
            using var _tmp = tmp;
            var window = new Window { Content = new LauncherPaneView { DataContext = vm } };
            window.Show();
            Dispatcher.UIThread.RunJobs();

            var startButton = Find<Button>(window, "StartButton")!;
            var before = startButton.IsEnabled;

            await vm.SelectRepositoryAsync("/repos/kcap-cli");
            Dispatcher.UIThread.RunJobs();
            var after = startButton.IsEnabled;

            window.Close();
            Dispatcher.UIThread.RunJobs();
            vm.Dispose();
            return (before, after);
        });

        await Assert.That(enabledBefore).IsFalse();
        await Assert.That(enabledAfter).IsTrue();
    }

    /// Enter in the goal box starts the same way as the Start button — tunnel KeyDown, so the
    /// TextBox cannot swallow the key before Start sees it.
    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task Enter_in_the_goal_box_starts_when_Start_can_run() {
        var (goalAfter, startCount) = await AvaloniaSession.DispatchAsync(async () => {
            var (_, vm, _, launch, tmp) = Build();
            using var _tmp = tmp;
            var window = new Window { Content = new LauncherPaneView { DataContext = vm }, Width = 900, Height = 600 };
            window.Show();
            Dispatcher.UIThread.RunJobs();

            await vm.SelectRepositoryAsync("/repos/kcap-cli");
            Dispatcher.UIThread.RunJobs();

            var goal = Find<TextBox>(window, "GoalInput")!;
            goal.Focus();
            Dispatcher.UIThread.RunJobs();
            window.KeyTextInput("ship it");
            Dispatcher.UIThread.RunJobs();
            await Assert.That(goal.Text).IsEqualTo("ship it");

            window.KeyPressQwerty(PhysicalKey.Enter, RawInputModifiers.None);
            Dispatcher.UIThread.RunJobs();
            for (var i = 0; i < 50 && launch.StartCount == 0; i++) {
                await Task.Delay(10);
                Dispatcher.UIThread.RunJobs();
            }

            var after = vm.Goal;
            var count = launch.StartCount;

            window.Close();
            Dispatcher.UIThread.RunJobs();
            vm.Dispose();
            return (after, count);
        });

        await Assert.That(startCount).IsEqualTo(1);
        await Assert.That(goalAfter).IsEqualTo("");
    }

    /// Without a repository, Enter is consumed and starts nothing — same gate as the Start button.
    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task Enter_without_a_repository_does_not_start() {
        var (goalAfter, startCount) = await AvaloniaSession.DispatchAsync(() => {
            var (_, vm, _, launch, tmp) = Build();
            using var _tmp = tmp;
            var window = new Window { Content = new LauncherPaneView { DataContext = vm }, Width = 900, Height = 600 };
            window.Show();
            Dispatcher.UIThread.RunJobs();

            var goal = Find<TextBox>(window, "GoalInput")!;
            goal.Focus();
            Dispatcher.UIThread.RunJobs();
            window.KeyTextInput("ship it");
            Dispatcher.UIThread.RunJobs();

            window.KeyPressQwerty(PhysicalKey.Enter, RawInputModifiers.None);
            Dispatcher.UIThread.RunJobs();

            var after = goal.Text;
            var count = launch.StartCount;

            window.Close();
            Dispatcher.UIThread.RunJobs();
            vm.Dispose();
            return (after, count);
        });

        await Assert.That(startCount).IsEqualTo(0);
        await Assert.That(goalAfter).IsEqualTo("ship it");
    }

    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task StartErrorText_visibility_follows_StartError() {
        var (visibleBefore, visibleAfterFailure, errorMessage, visibleAfterSuccess) = await AvaloniaSession.DispatchAsync(async () => {
            var (_, vm, _, launch, tmp) = Build();
            using var _tmp = tmp;
            var window = new Window { Content = new LauncherPaneView { DataContext = vm } };
            window.Show();
            Dispatcher.UIThread.RunJobs();

            var errorText = Find<TextBlock>(window, "StartErrorText")!;
            var before = errorText.IsVisible;

            await vm.SelectRepositoryAsync("/repos/kcap-cli");
            launch.Next = new LaunchOutcome(false, null, "Daemon 'kcap-dev' is at capacity.");
            await vm.StartCommand.Execute();
            Dispatcher.UIThread.RunJobs();
            var afterFailure = errorText.IsVisible;
            var message = errorText.Text;

            launch.Next = new LaunchOutcome(true, SecondLaunchedId, null);
            await vm.StartCommand.Execute();
            Dispatcher.UIThread.RunJobs();
            var afterSuccess = errorText.IsVisible;

            window.Close();
            Dispatcher.UIThread.RunJobs();
            vm.Dispose();
            return (before, afterFailure, message, afterSuccess);
        });

        await Assert.That(visibleBefore).IsFalse();
        await Assert.That(visibleAfterFailure).IsTrue();
        await Assert.That(errorMessage).IsEqualTo("Daemon 'kcap-dev' is at capacity.");
        await Assert.That(visibleAfterSuccess).IsFalse();
    }

    /// Regression guard for HomeViewModel's ObserveOn before SortAndBind. In production the
    /// Agents cache is mutated on the daemon client's own pump thread and SortAndBind writes
    /// straight into the collection SessionCards is bound to, so the assertion that matters is
    /// WHICH THREAD the bound collection is mutated on. "Does not throw" does not work here:
    /// measured with the ObserveOn deleted, the off-thread push raises nothing and the container
    /// still realizes — a bare Dispatcher.VerifyAccess and a control property set from the same
    /// background thread DO throw, so the harness enforces affinity; this path simply defers its
    /// UI work. Thread identity is what distinguishes marshalled from unmarshalled. Deliberately
    /// NOT wrapped in WithImmediateRxScheduler — that pins the scheduler to Immediate, which turns
    /// the ObserveOn under test into a no-op.
    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task An_agent_arriving_off_the_UI_thread_reaches_the_grid_on_the_UI_thread() {
        var (mutatedOnUiThread, realizedCount, failure) = await AvaloniaSession.DispatchAsync(async () => {
            var (view, vm, service, _, tmp) = Build();
            using var _tmp = tmp;
            var window = new Window { Content = view };
            window.Show();
            Dispatcher.UIThread.RunJobs();

            var sessionCards = Find<ItemsControl>(window, "SessionCards")!;
            // ReadOnlyObservableCollection exposes CollectionChanged only through the interface.
            bool? onUiThread = null;
            ((INotifyCollectionChanged)vm.Sessions).CollectionChanged += (_, _) => onUiThread ??= Dispatcher.UIThread.CheckAccess();

            // Captured rather than propagated, so a thread-affinity throw arrives as a named
            // assertion failure instead of a bare rethrow out of the dispatch.
            Exception? thrown = null;
            try {
                await Task.Run(() => service.Agents.AddOrUpdate(new AgentStatusDto(
                    "a", "agent", "claude", "/repos/kcap-cli", "Running", null, null, null, DateTime.UtcNow, null, null)));
            } catch (Exception ex) {
                thrown = ex;
            }

            Dispatcher.UIThread.RunJobs();
            var count = ItemCount(sessionCards);

            window.Close();
            Dispatcher.UIThread.RunJobs();
            vm.Dispose();
            return (onUiThread, count, thrown?.ToString());
        });

        await Assert.That(failure).IsNull();
        await Assert.That(mutatedOnUiThread).IsTrue();
        await Assert.That(realizedCount).IsEqualTo(1);
    }

    /// The card's click plumbing (spec §3, entry points): the whole card is a Button whose Click
    /// carries the card's OWN id to the window. Realized-visual-dependent by necessity — the
    /// handler lives in the item template, so only a rendered card can raise it.
    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task Clicking_a_session_card_asks_to_open_that_session() {
        var requested = await AvaloniaSession.DispatchAsync(() => {
            using var tmp = TempDir.WithPathTo("app-state.json", out var path);
            var service = new FakeDaemonClientService();
            var opened = new List<string>();
            var vm = new HomeViewModel(
                service, new AppStateStore(path), new RecordingLaunchClient(),
                () => Task.FromResult(Array.Empty<string>()), openSession: opened.Add);
            var window = new Window { Content = new HomeView { DataContext = vm } };
            window.Show();
            Dispatcher.UIThread.RunJobs();

            service.Agents.AddOrUpdate(new AgentStatusDto(
                SecondLaunchedId, "agent", "claude", "/repos/kcap-cli", "Running", null, null, null, DateTime.UtcNow, null, null));
            Dispatcher.UIThread.RunJobs();

            var card = window.GetVisualDescendants().OfType<Button>().First(b => b.Classes.Contains("sessionCard"));
            card.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));

            window.Close();
            Dispatcher.UIThread.RunJobs();
            vm.Dispose();
            return opened.ToList();
        });

        await Assert.That(requested).IsEquivalentTo([SecondLaunchedId]);
    }

    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task SessionCards_item_count_tracks_the_agent_cache() {
        var (countEmpty, countAfterOne, countAfterTwo) = await AvaloniaSession.DispatchAsync(() => {
            var (view, vm, service, _, tmp) = Build();
            using var _tmp = tmp;
            var window = new Window { Content = view };
            window.Show();
            Dispatcher.UIThread.RunJobs();

            var sessionCards = Find<ItemsControl>(window, "SessionCards")!;
            var empty = ItemCount(sessionCards);

            service.Agents.AddOrUpdate(new AgentStatusDto(
                "a", "agent", "claude", "/repos/kcap-cli", "Running", null, null, null, DateTime.UtcNow, null, null));
            Dispatcher.UIThread.RunJobs();
            var afterOne = ItemCount(sessionCards);

            service.Agents.AddOrUpdate(new AgentStatusDto(
                "b", "agent", "codex", "/repos/other", "Running", null, null, null, DateTime.UtcNow, null, null));
            Dispatcher.UIThread.RunJobs();
            var afterTwo = ItemCount(sessionCards);

            window.Close();
            Dispatcher.UIThread.RunJobs();
            vm.Dispose();
            return (empty, afterOne, afterTwo);
        });

        await Assert.That(countEmpty).IsEqualTo(0);
        await Assert.That(countAfterOne).IsEqualTo(1);
        await Assert.That(countAfterTwo).IsEqualTo(2);
    }

    /// Pins that the goal box draws no ring of its own on focus: the card is its boundary, so
    /// the theme's focused border and fill stay off.
    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task A_focused_goal_box_draws_no_ring_inside_its_card() {
        var (thickness, transparent) = await AvaloniaSession.DispatchAsync(() => {
            var (_, vm, _, _, tmp) = Build();
            using var _tmp = tmp;
            var window = new Window { Content = new LauncherPaneView { DataContext = vm }, Width = 800, Height = 600 };
            window.Show();
            Dispatcher.UIThread.RunJobs();
            var goal = Find<TextBox>(window, "GoalInput")!;
            goal.Focus();
            Dispatcher.UIThread.RunJobs();
            window.UpdateLayout();
            var ring = goal.GetVisualDescendants().OfType<Border>().Single(b => b.Name == "PART_BorderElement");
            var result = (ring.BorderThickness, ring.Background is null || ring.Background is Avalonia.Media.ISolidColorBrush { Color.A: 0 });
            window.Close();
            Dispatcher.UIThread.RunJobs();
            vm.Dispose();
            return result;
        });

        await Assert.That(thickness).IsEqualTo(new Avalonia.Thickness(0));
        await Assert.That(transparent).IsTrue();
    }
}
