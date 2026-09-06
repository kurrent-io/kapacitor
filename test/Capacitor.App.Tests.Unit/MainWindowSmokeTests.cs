using System.Reactive.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Capacitor.App.Services;
using Capacitor.App.ViewModels;
using Capacitor.App.Views;
using Capacitor.Cli.Core.LocalIpc;
using DynamicData;
using Microsoft.Extensions.Time.Testing;
using static Capacitor.App.Tests.Unit.FakeDaemonClientService;

namespace Capacitor.App.Tests.Unit;

/// Headless rendering acceptance for the deliverable's identity block (spec §8): boot a real
/// MainWindow against a fake service pre-fed (Connected, snapshot), Show() it, and assert the
/// rendered text actually contains the daemon name/version/server URL/agent count — not just
/// that the VM's properties hold the right values (MainWindowViewModelTests already covers
/// that in isolation).
public class MainWindowSmokeTests {
    sealed class NeverLaunchClient : ILaunchClient {
        public Task<LaunchOutcome> StartAsync(LaunchRequest request, CancellationToken ct) =>
            Task.FromResult(new LaunchOutcome(false, null, "unexpected launch"));
    }

    // Real AppNotifier (not RecordingNotifier) — the production notifier is fine here; most of
    // these tests don't exercise the toast overlay at all (window.Notifier is left unset), and
    // the one that does (below) needs a real IObservable<string> to subscribe through.
    static (AgentActionService Actions, IAppNotifier Notifier) NewActions(FakeDaemonClientService service) {
        var notifier = new AppNotifier();
        var actions = new AgentActionService(new ScriptedLocalControlOps(), notifier, new RecordingOpener(), service.SnapshotsSubject, CancellationToken.None, NeverConfirm.Confirm);
        return (actions, notifier);
    }

    /// A real WorkspaceViewModel over the fake service and scripted attach/surface fakes — same
    /// pieces MainWindowViewModelTests wires, over the actions this file's NewActions built.
    static WorkspaceViewModel NewWorkspace(FakeDaemonClientService service, AgentActionService actions, string agentId) =>
        new(agentId, service, actions, new FakeTerminalAttachClientFactory().Factory,
            () => new FakeTerminalSurface(), new FakeTimeProvider(), new RecordingOpener(), new FakePermissionService(), new FakeWorkContextSource());

    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task MainWindow_renders_the_connection_word_and_tenant_not_the_identity_block() {
        await AvaloniaSession.WithImmediateRxScheduler(async () => {
            var rendered = await AvaloniaSession.DispatchAsync(() => {
                var service = new FakeDaemonClientService();
                service.SnapshotsSubject.OnNext(Snap(
                    daemon: "daemon-a", version: "1.2.3", serverUrl: "http://localhost:9999",
                    connection: "connected", active: 1, max: 5));
                service.StatusSubject.OnNext(new AttachStatus(AttachState.Connected, null, null));

                var (actions, _) = NewActions(service);
                var vm = new MainWindowViewModel(service, CancellationToken.None, TestActivity.New(),
                    tenantName: "kurrent");
                var window = new MainWindow { DataContext = vm };
                window.Show();
                // Control.Loaded is POSTED at DispatcherPriority.Loaded (Avalonia defers it, it
                // never fires synchronously from Show()) — pump the dispatcher so it actually
                // runs before reading bound text. This is what drives ReactiveWindow<T>'s
                // built-in Loaded->ViewModel.Activator.Activate() wiring.
                Dispatcher.UIThread.RunJobs();

                var texts = string.Join('\n', window.GetVisualDescendants()
                    .OfType<TextBlock>()
                    .Select(t => t.Text ?? ""));

                window.Close();
                Dispatcher.UIThread.RunJobs(); // flush the deferred Unloaded post so the VM's WhenActivated-scoped subscriptions actually get disposed before the next test runs

                return texts;
            });

            // The rail footer is the one daemon indicator: word + tenant on screen, the identity
            // block (name/version/URL) demoted to its hover tooltip — rendered text must NOT
            // carry it.
            await Assert.That(rendered).Contains("Connected");
            await Assert.That(rendered).Contains("kurrent");
            await Assert.That(rendered).DoesNotContain("daemon-a");
            await Assert.That(rendered).DoesNotContain("http://localhost:9999");
        });
    }

    /// Regression coverage for a Critical bug found in review: canStart/canRetry were built
    /// straight off service.Status with no ObserveOn, and ReactiveCommand does NOT reschedule a
    /// SUPPLIED canExecute onto its outputScheduler (only IsExecuting/ThrownExceptions ride it) —
    /// so a Status event arriving on a background thread carried CanExecuteChanged, and therefore
    /// a bound Button's IsEnabled write, onto that same background thread, tripping Avalonia's
    /// dispatcher thread-affinity check.
    ///
    /// Deliberately NOT wrapped in AvaloniaSession.WithImmediateRxScheduler: that swaps
    /// RxSchedulers.MainThreadScheduler for ImmediateScheduler.Instance, which would deliver the
    /// background-thread OnNext synchronously on the CALLING (background) thread regardless of
    /// whether an ObserveOn is present — it could never catch this bug either way. This test
    /// needs the REAL Avalonia-dispatcher scheduler that UseReactiveUI() installs for the whole
    /// headless session, so a background-thread publish actually has to cross a real dispatcher
    /// boundary to reach the Button.
    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task Status_transition_from_a_background_thread_does_not_throw_and_converges() {
        var (thrown, startEnabledAfter) = await AvaloniaSession.DispatchAsync(() => {
            var service = new FakeDaemonClientService();
            var (actions, _) = NewActions(service);
            var vm = new MainWindowViewModel(service, CancellationToken.None, TestActivity.New());
            var window = new MainWindow { DataContext = vm };
            window.Show();
            Dispatcher.UIThread.RunJobs();

            Exception? caught = null;
            var backgroundPublish = Task.Run(() => {
                try {
                    service.StatusSubject.OnNext(new AttachStatus(AttachState.Unreachable, "daemon_unreachable", null));
                } catch (Exception ex) {
                    caught = ex;
                }
            });
            backgroundPublish.Wait(TimeSpan.FromSeconds(5));

            // Give a correctly-marshaled dispatcher post a chance to actually run and converge.
            Dispatcher.UIThread.RunJobs();

            var startButton = window.GetVisualDescendants().OfType<Button>()
                .First(b => Equals(b.Content, "Start daemon"));
            var enabled = startButton.IsEnabled;

            window.Close();
            return (caught, enabled);
        });

        await Assert.That(thrown).IsNull();
        await Assert.That(startEnabledAfter).IsTrue();
    }

    /// Regression coverage for a Critical bug found in review: RunStartAsync did not catch
    /// OperationCanceledException, but DaemonClientService.StartDaemonAsync deliberately
    /// rethrows it when the caller-supplied ct fires mid-wait (App's `_shutdown` token — spec
    /// §5, "ct abandons the WAIT, not the started daemon"). App.OnShutdownRequested cancels
    /// that very token on Cmd+Q while a start may still be in flight. Nothing subscribes to
    /// StartDaemonCommand.ThrownExceptions, so ReactiveCommand's own default handler
    /// (decompile-verified: ReactiveUI.RxState.DefaultExceptionHandler) reschedules an
    /// UnhandledErrorException onto RxSchedulers.MainThreadScheduler — the still-alive
    /// dispatcher — crashing the app.
    ///
    /// Deliberately NOT wrapped in WithImmediateRxScheduler, for the same reason as the sibling
    /// test above: only a REAL dispatcher round-trip (via Dispatcher.UIThread.RunJobs(), which
    /// decompile-verified drains Avalonia's dispatcher queue including jobs enqueued mid-drain,
    /// and re-throws an unhandled job exception out of the call since nothing subscribes to
    /// Dispatcher.UIThread.UnhandledException) actually reproduces — and proves the fix for — a
    /// scheduler-rescheduled exception.
    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task Quit_during_start_does_not_crash() {
        var (thrown, completed) = await AvaloniaSession.DispatchAsync(() => {
            var service = new FakeDaemonClientService();
            service.StatusSubject.OnNext(new AttachStatus(AttachState.Unreachable, "daemon_unreachable", null));

            var shutdown = new CancellationTokenSource();
            var (actions, _) = NewActions(service);
            var vm = new MainWindowViewModel(service, shutdown.Token, TestActivity.New());
            var window = new MainWindow { DataContext = vm };
            window.Show();
            Dispatcher.UIThread.RunJobs();

            service.StartBehavior = async ct => {
                // Blocks until ct fires, then throws OCE — mirrors StartDaemonAsync's real
                // ct-abandons-the-wait contract (e.g. its own process.WaitForExitAsync(ct)).
                await Task.Delay(Timeout.InfiniteTimeSpan, ct);
                return new StartDaemonResult(true, null); // unreachable
            };

            var executeTask = vm.StartDaemonCommand.Execute().ToTask();

            Exception? caught = null;
            try {
                shutdown.Cancel(); // simulates Cmd+Q mid-start: OnShutdownRequested cancels this same token

                // The ct-cancellation continuation (and any exception ReactiveCommand reschedules
                // as a result) may hop through a thread-pool continuation before landing back on
                // the dispatcher queue, so poll rather than assume one RunJobs() drains it all.
                var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(2);
                while (!executeTask.IsCompleted && DateTime.UtcNow < deadline) {
                    Dispatcher.UIThread.RunJobs();
                    Thread.Sleep(5);
                }
            } catch (Exception ex) {
                caught = ex;
            }

            var isCompleted = executeTask.IsCompleted;

            window.Close();
            Dispatcher.UIThread.RunJobs();

            return (caught, isCompleted);
        });

        await Assert.That(thrown).IsNull();
        await Assert.That(completed).IsTrue();
    }

    /// BannerMessage must not reserve dead space when empty: Connecting… shows a notice, then a
    /// failed start replaces that body with the start message (one line, never stacked).
    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task StartMessage_and_reason_text_collapse_when_empty_and_appear_once_set() {
        var (bannerInitially, bannerTextUnreachable, bannerTextAfterFailure) =
            await AvaloniaSession.DispatchAsync(async () => {
                using var tmp = TempDir.WithPathTo("app-state.json", out var path);
                var service = new FakeDaemonClientService();
                var home = new HomeViewModel(
                    service, new AppStateStore(path), new NeverLaunchClient(),
                    () => Task.FromResult(Array.Empty<string>()));
                var vm = new MainWindowViewModel(service, CancellationToken.None, TestActivity.New(), home: home);
                var window = new MainWindow { DataContext = vm };
                window.Show();
                Dispatcher.UIThread.RunJobs();

                TextBlock Banner() =>
                    window.GetVisualDescendants().OfType<TextBlock>().First(t => t.Name == "BannerMessageText");
                Border NoticeBanner() => Banner().FindAncestorOfType<Border>()!;

                var bannerInit = NoticeBanner().IsVisible;

                service.StatusSubject.OnNext(new AttachStatus(AttachState.Unreachable, "daemon_unreachable", null));
                Dispatcher.UIThread.RunJobs();
                var textUnreachable = Banner().Text;

                service.StartBehavior = _ => Task.FromResult(new StartDaemonResult(false, "boom: could not bind socket"));
                await vm.StartDaemonCommand.Execute().ToTask();
                Dispatcher.UIThread.RunJobs();
                var textAfter = Banner().Text;

                window.Close();
                Dispatcher.UIThread.RunJobs();
                home.Dispose();

                return (bannerInit, textUnreachable, textAfter);
            });

        await Assert.That(bannerInitially).IsTrue(); // Connecting… still has a notice
        await Assert.That(bannerTextUnreachable).IsEqualTo(HomeViewModel.DaemonDownNotice);
        await Assert.That(bannerTextAfterFailure).IsEqualTo("boom: could not bind socket");
    }

    // ---- Toast overlay (spec §11: WindowNotificationManager replaces the inline banner) ----
    //
    // Proves the real production wiring end to end: MainWindow.Notifier assigned exactly as
    // App.BuildAndShowMainWindow does, a WindowNotificationManager actually constructible and
    // installable under the headless session, and the fired message rendered as visible text.
    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task Toast_renders_the_notifier_message() {
        var rendered = await AvaloniaSession.DispatchAsync(() => {
            var service = new FakeDaemonClientService();
            var (actions, notifier) = NewActions(service);
            var vm = new MainWindowViewModel(service, CancellationToken.None, TestActivity.New());
            var window = new MainWindow { DataContext = vm, Notifier = notifier };
            window.Show();
            Dispatcher.UIThread.RunJobs();

            notifier.Notify("Couldn't stop agent-a");
            Dispatcher.UIThread.RunJobs();

            var texts = string.Join('\n', window.GetVisualDescendants().OfType<TextBlock>().Select(t => t.Text ?? ""));

            window.Close();
            Dispatcher.UIThread.RunJobs();

            return texts;
        });

        await Assert.That(rendered).Contains("Kurrent Capacitor");
        await Assert.That(rendered).Contains("Couldn't stop agent-a");
    }

    // ---- Activity gate (spec §4) ----
    //
    // Proves the real production wiring end to end — the Activity flyout's open state, the
    // launcher pane being on screen (Sessions surface with NO workspace open), and the window's
    // own IsVisible (Show()/Hide()) all drive ActivityViewModel.OnTabVisibleChanged through the
    // code-behind, not just that the ViewModel reacts correctly in isolation
    // (ActivityViewModelTests already covers that). Each gate flips the polling off on its own;
    // each TRUE transition issues exactly one more immediate read, awaited via
    // PendingRefreshForTesting — the VM's stat+read hops off the UI thread, so RunJobs() alone no
    // longer guarantees the read has landed. Swapping the pane under the popup (leaving Sessions,
    // or opening a workspace) CLOSES the flyout, so coming back does not auto-resume — the feed
    // reopens by click.
    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task Activity_polls_only_while_open_on_the_launcher_pane_in_a_visible_window() {
        var reads = await AvaloniaSession.DispatchAsync(async () => {
            var service = new FakeDaemonClientService();
            var (actions, _) = NewActions(service);
            var reader = new ScriptedReader();
            reader.Set(new ConsentLogReadResult([], true));
            var activity = new ActivityViewModel(reader.Read, () => "k", new FakeTicker());
            var attach = new FakeTerminalAttachClientFactory();
            var vm = new MainWindowViewModel(
                service, CancellationToken.None, activity,
                workspaceFactory: agentId => new WorkspaceViewModel(
                    agentId, service, actions, attach.Factory, () => new FakeTerminalSurface(), new FakeTimeProvider(), new RecordingOpener(), new FakePermissionService(), new FakeWorkContextSource()));
            var window = new MainWindow { DataContext = vm };
            window.Show();
            Dispatcher.UIThread.RunJobs();

            var button = window.GetVisualDescendants().OfType<Button>().First(b => b.Name == "ActivityButton");
            var flyout = button.Flyout!;
            var closed = reader.ReadCalls; // starts closed — no read

            flyout.ShowAt(button);
            Dispatcher.UIThread.RunJobs();
            await activity.PendingRefreshForTesting!;
            var opened = reader.ReadCalls;

            vm.ShowHomeCommand.Execute().Subscribe(); // off the Sessions surface (hidden Home)
            Dispatcher.UIThread.RunJobs();
            var onHome = reader.ReadCalls;

            vm.ShowSessionsCommand.Execute().Subscribe();
            Dispatcher.UIThread.RunJobs();
            var backClosed = reader.ReadCalls; // the swap closed the feed — no auto-resume

            flyout.ShowAt(button);
            Dispatcher.UIThread.RunJobs();
            await activity.PendingRefreshForTesting!;
            var reopened = reader.ReadCalls;

            vm.OpenSession("0123456789abcdef0123456789abcdef"); // workspace replaces the launcher
            Dispatcher.UIThread.RunJobs();
            var workspaceOpen = reader.ReadCalls;

            vm.CloseWorkspace();
            Dispatcher.UIThread.RunJobs();
            var launcherBack = reader.ReadCalls; // closed by the swap — still off

            flyout.ShowAt(button);
            Dispatcher.UIThread.RunJobs();
            await activity.PendingRefreshForTesting!;
            var afterReopen = reader.ReadCalls;

            window.Hide();
            Dispatcher.UIThread.RunJobs();
            var afterHide = reader.ReadCalls;

            // Whether the popup survived the window hide is platform detail: Show() alone resumes
            // a surviving flyout, ShowAt() reopens a closed one, and a repeated true is not a
            // transition — either way exactly one more read lands.
            window.Show();
            Dispatcher.UIThread.RunJobs();
            flyout.ShowAt(button);
            Dispatcher.UIThread.RunJobs();
            await activity.PendingRefreshForTesting!;
            var afterReshow = reader.ReadCalls;

            window.Close();
            Dispatcher.UIThread.RunJobs();

            return (closed, opened, onHome, backClosed, reopened, workspaceOpen, launcherBack, afterReopen, afterHide, afterReshow);
        });

        await Assert.That(reads.closed).IsEqualTo(0);
        await Assert.That(reads.opened).IsEqualTo(1); // opening on the launcher: one immediate read
        await Assert.That(reads.onHome).IsEqualTo(1); // leaving Sessions is a FALSE transition
        await Assert.That(reads.backClosed).IsEqualTo(1);
        await Assert.That(reads.reopened).IsEqualTo(2);
        await Assert.That(reads.workspaceOpen).IsEqualTo(2); // a workspace opening is a FALSE transition
        await Assert.That(reads.launcherBack).IsEqualTo(2);
        await Assert.That(reads.afterReopen).IsEqualTo(3);
        await Assert.That(reads.afterHide).IsEqualTo(3); // hiding is a FALSE transition
        await Assert.That(reads.afterReshow).IsEqualTo(4);
    }

    /// The surface swap itself (spec §3) — the XAML side of what WorkspaceNavigationTests pins on
    /// the ViewModel. WorkspaceView is materialized from a template rather than always present, so
    /// this also proves the terminal control is CONSTRUCTED only once a workspace exists; closing
    /// it lands on the Sessions surface's placeholder, never back on Home.
    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task Opening_a_session_swaps_the_launcher_pane_for_the_workspace() {
        await AvaloniaSession.WithImmediateRxScheduler(async () => {
            var swap = await AvaloniaSession.DispatchAsync(() => {
                var service = new FakeDaemonClientService();
                var (actions, _) = NewActions(service);
                var attach = new FakeTerminalAttachClientFactory();
                var vm = new MainWindowViewModel(
                    service, CancellationToken.None, TestActivity.New(),
                    workspaceFactory: agentId => new WorkspaceViewModel(
                        agentId, service, actions, attach.Factory, () => new FakeTerminalSurface(), new FakeTimeProvider(), new RecordingOpener(), new FakePermissionService(), new FakeWorkContextSource()));
                var window = new MainWindow { DataContext = vm };
                window.Show();
                Dispatcher.UIThread.RunJobs();

                Control Surface(string name) =>
                    window.GetVisualDescendants().OfType<Control>().First(c => c.Name == name);

                var bootsOnSessions = Surface("SessionsSurface").IsVisible;
                var homeHiddenAtBoot = Surface("HomeSurface").IsVisible;
                var launcherAtBoot = Surface("LauncherPane").IsVisible;
                var workspacesBefore = window.GetVisualDescendants().OfType<WorkspaceView>().Count();

                vm.OpenSession("0123456789abcdef0123456789abcdef");
                Dispatcher.UIThread.RunJobs();
                var launcherGone = Surface("LauncherPane").IsVisible;
                var opened = window.GetVisualDescendants().OfType<WorkspaceView>().ToList();
                // Read NOW, not in the return tuple: closing below detaches the view and clears the
                // very DataContext this is asserting on.
                var boundToWorkspace = opened.FirstOrDefault()?.DataContext is WorkspaceViewModel;

                vm.CloseWorkspace();
                Dispatcher.UIThread.RunJobs();
                var stillSessions = Surface("SessionsSurface").IsVisible;
                var launcherBack = Surface("LauncherPane").IsVisible;
                var workspacesAfter = window.GetVisualDescendants().OfType<WorkspaceView>().Count();

                window.Close();
                Dispatcher.UIThread.RunJobs();

                return (bootsOnSessions, homeHiddenAtBoot, launcherAtBoot, workspacesBefore, launcherGone,
                    OpenedCount: opened.Count, boundToWorkspace, stillSessions, launcherBack, workspacesAfter);
            });

            await Assert.That(swap.bootsOnSessions).IsTrue();
            await Assert.That(swap.homeHiddenAtBoot).IsFalse(); // Home stays in the tree, hidden
            await Assert.That(swap.launcherAtBoot).IsTrue(); // the empty state IS the launcher
            await Assert.That(swap.workspacesBefore).IsEqualTo(0); // nothing terminal-shaped until a session is opened
            await Assert.That(swap.launcherGone).IsFalse();
            await Assert.That(swap.OpenedCount).IsEqualTo(1);
            await Assert.That(swap.boundToWorkspace).IsTrue();
            await Assert.That(swap.stillSessions).IsTrue();
            await Assert.That(swap.launcherBack).IsTrue();
            await Assert.That(swap.workspacesAfter).IsEqualTo(0);
        });
    }

    /// The rail's own click path (spec §3): a session row rendered by SessionRailView carries the
    /// VM's OpenCommand, and executing it opens that agent's workspace on the Sessions surface.
    ///
    /// Also pins the selection highlight as RENDERED state, not just as a bound class. A row's
    /// resting Background must come from the `railRow` class style: a local `Background` attribute
    /// on the Button would be a LocalValue, outrank the `.selected`/`.holdsSelected` style
    /// triggers, and leave the highlight permanently invisible while still passing any
    /// class-membership assertion. Comparing the opened row's alpha against its sibling's is what
    /// fails if a future local value defeats the style again.
    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task Rail_click_opens_the_workspace_and_highlights_the_open_row() {
        await AvaloniaSession.WithImmediateRxScheduler(async () => {
            var opened = await AvaloniaSession.DispatchAsync(() => {
                var service = new FakeDaemonClientService();
                service.SnapshotsSubject.OnNext(Snap());
                service.StatusSubject.OnNext(new AttachStatus(AttachState.Connected, null, null));
                service.Agents.AddOrUpdate(new AgentStatusDto(
                    "a1", "agent", "claude", "/dev/alpha/wt/feature-x", "Running",
                    null, null, null, DateTime.UtcNow, null, null, Title: "Fix the flaky test"));
                service.Agents.AddOrUpdate(new AgentStatusDto(
                    "a2", "agent", "claude", "/dev/alpha/wt/feature-x", "Running",
                    null, null, null, DateTime.UtcNow, null, null, Title: "Leave this one alone"));

                var (actions, _) = NewActions(service);
                MainWindowViewModel? vm = null;
                Func<string, string> resolveRepoRoot = p => p.Contains("/wt/", StringComparison.Ordinal)
                    ? p[..p.IndexOf("/wt/", StringComparison.Ordinal)]
                    : p;
                var directory = new AgentDirectory(
                    service, new FakeRemoteAgents(), new FakeServerLane(), new RepoIdentityResolver(_ => null),
                    resolveRepoRoot, null, null);
                var rail = new SessionRailViewModel(
                    directory, service, id => vm!.OpenSession(id), _ => { }, resolveRepoRoot);
                vm = new MainWindowViewModel(service, CancellationToken.None, TestActivity.New(),
                    workspaceFactory: id => NewWorkspace(service, actions, id), rail: rail);
                var window = new MainWindow { DataContext = vm };
                window.Show();
                Dispatcher.UIThread.RunJobs();

                vm.ShowSessionsCommand.Execute().Subscribe();
                Dispatcher.UIThread.RunJobs();

                Button Row(string text) => window.GetVisualDescendants().OfType<Button>()
                    .First(b => b.GetVisualDescendants().OfType<TextBlock>().Any(t => t.Text == text));
                byte Alpha(Button b) => (b.Background as ISolidColorBrush)?.Color.A ?? 0;

                Row("Fix the flaky test").Command!.Execute(null);
                Dispatcher.UIThread.RunJobs();

                var result = (vm.IsSessionsView, vm.CurrentWorkspace?.AgentId,
                    SelectedClass: Row("Fix the flaky test").Classes.Contains("selected"),
                    SelectedAlpha: Alpha(Row("Fix the flaky test")),
                    SiblingAlpha: Alpha(Row("Leave this one alone")),
                    // The worktree header row carries the same hazard through holdsSelected.
                    WorktreeAlpha: Alpha(Row("feature-x")));
                window.Close();
                Dispatcher.UIThread.RunJobs();
                return result;
            });
            await Assert.That(opened.Item1).IsTrue();
            await Assert.That(opened.Item2).IsEqualTo("a1");
            await Assert.That(opened.SelectedClass).IsTrue();
            await Assert.That(opened.SelectedAlpha).IsGreaterThan((byte)0); // the highlight actually paints
            await Assert.That(opened.SiblingAlpha).IsEqualTo((byte)0); // an unopened row stays transparent
            await Assert.That(opened.WorktreeAlpha).IsGreaterThan((byte)0);
        });
    }

    /// The tabless boot (spec §3, revised): the window opens on the Sessions surface — rail plus
    /// the launcher pane — with no TabControl anywhere in its visual tree; the rail's New session
    /// row is the deselect-to-launcher affordance.
    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task Window_boots_tabless_on_the_sessions_surface() {
        await AvaloniaSession.WithImmediateRxScheduler(async () => {
            var ok = await AvaloniaSession.DispatchAsync(() => {
                var service = new FakeDaemonClientService();
                service.SnapshotsSubject.OnNext(Snap());
                var (actions, _) = NewActions(service);
                var vm = new MainWindowViewModel(service, CancellationToken.None, TestActivity.New());
                var window = new MainWindow { DataContext = vm };
                window.Show();
                Dispatcher.UIThread.RunJobs();

                var noTabs = !window.GetVisualDescendants().OfType<TabControl>().Any();
                var railPresent = window.GetVisualDescendants().OfType<SessionRailView>().Any();
                var newSessionRow = window.GetVisualDescendants().OfType<Button>().Any(b => b.Name == "RailNewSessionButton");
                window.Close();
                Dispatcher.UIThread.RunJobs();
                return noTabs && railPresent && newSessionRow;
            });
            await Assert.That(ok).IsTrue();
        });
    }

    /// 310 of rail plus 400 of pane must never squeeze the center column to nothing.
    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task MainWindow_pins_its_minimum_width_to_the_default_width() {
        await AvaloniaSession.RunOnUiAsync(async () => {
            var window = new MainWindow { DataContext = new MainWindowViewModel(new FakeDaemonClientService(), CancellationToken.None, TestActivity.New()) };

            await Assert.That(window.MinWidth).IsEqualTo(1200);
            await Assert.That(window.Width).IsEqualTo(1200);
        });
    }
}
