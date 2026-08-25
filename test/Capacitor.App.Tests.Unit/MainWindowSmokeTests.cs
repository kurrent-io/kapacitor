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
            () => new FakeTerminalSurface(), new FakeTimeProvider());

    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task MainWindow_renders_daemon_identity_server_url_and_agent_count() {
        await AvaloniaSession.WithImmediateRxScheduler(async () => {
            var rendered = await AvaloniaSession.DispatchAsync(() => {
                var service = new FakeDaemonClientService();
                service.SnapshotsSubject.OnNext(Snap(
                    daemon: "daemon-a", version: "1.2.3", serverUrl: "http://localhost:9999",
                    connection: "connected", active: 1, max: 5));
                service.StatusSubject.OnNext(new AttachStatus(AttachState.Connected, null, null));

                var (actions, _) = NewActions(service);
                var vm = new MainWindowViewModel(service, actions, new FakeTicker(), CancellationToken.None, TestActivity.New());
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

            await Assert.That(rendered).Contains("daemon-a");
            await Assert.That(rendered).Contains("1.2.3");
            await Assert.That(rendered).Contains("http://localhost:9999");
            await Assert.That(rendered).Contains("1 of 5 agents");
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
            var vm = new MainWindowViewModel(service, actions, new FakeTicker(), CancellationToken.None, TestActivity.New());
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
            var vm = new MainWindowViewModel(service, actions, new FakeTicker(), shutdown.Token, TestActivity.New());
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

    /// StartMessageText/ReasonText must not reserve dead space when there is nothing to say
    /// (spec: "collapse when empty"): both start out empty (Connecting, no failed attempt yet),
    /// then Reason appears on Unreachable and StartMessage appears once a start attempt fails.
    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task StartMessage_and_reason_text_collapse_when_empty_and_appear_once_set() {
        var (reasonInitially, startMessageInitially, reasonWhileUnreachable, startMessageAfterFailure) =
            await AvaloniaSession.DispatchAsync(async () => {
                var service = new FakeDaemonClientService();
                var (actions, _) = NewActions(service);
                var vm = new MainWindowViewModel(service, actions, new FakeTicker(), CancellationToken.None, TestActivity.New());
                var window = new MainWindow { DataContext = vm };
                window.Show();
                Dispatcher.UIThread.RunJobs();

                TextBlock Find(string name) =>
                    window.GetVisualDescendants().OfType<TextBlock>().First(t => t.Name == name);

                var reasonInit = Find("ReasonText").IsVisible;
                var startMessageInit = Find("StartMessageText").IsVisible;

                service.StatusSubject.OnNext(new AttachStatus(AttachState.Unreachable, "daemon_unreachable", null));
                Dispatcher.UIThread.RunJobs();
                var reasonUnreachable = Find("ReasonText").IsVisible;

                service.StartBehavior = _ => Task.FromResult(new StartDaemonResult(false, "boom: could not bind socket"));
                await vm.StartDaemonCommand.Execute().ToTask();
                Dispatcher.UIThread.RunJobs();
                var startMessageAfter = Find("StartMessageText").IsVisible;

                window.Close();
                Dispatcher.UIThread.RunJobs();

                return (reasonInit, startMessageInit, reasonUnreachable, startMessageAfter);
            });

        await Assert.That(reasonInitially).IsFalse();
        await Assert.That(startMessageInitially).IsFalse();
        await Assert.That(reasonWhileUnreachable).IsTrue();
        await Assert.That(startMessageAfterFailure).IsTrue();
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
            var vm = new MainWindowViewModel(service, actions, new FakeTicker(), CancellationToken.None, TestActivity.New());
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
    // Proves the real production wiring end to end — the ActivityExpander's expansion, the shell
    // view, and the window's own IsVisible (Show()/Hide()) all drive
    // ActivityViewModel.OnTabVisibleChanged through the code-behind, not just that the ViewModel
    // reacts correctly in isolation (ActivityViewModelTests already covers that). Each of the three
    // flips the gate off on its own; each TRUE transition issues exactly one more immediate read,
    // and is awaited via PendingRefreshForTesting — the VM's stat+read hops off the UI thread, so
    // RunJobs() alone no longer guarantees the read has landed.
    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task Activity_polls_only_while_expanded_on_home_in_a_visible_window() {
        var reads = await AvaloniaSession.DispatchAsync(async () => {
            var service = new FakeDaemonClientService();
            var (actions, _) = NewActions(service);
            var reader = new ScriptedReader();
            reader.Set(new ConsentLogReadResult([], true));
            var activity = new ActivityViewModel(reader.Read, () => "k", new FakeTicker());
            var vm = new MainWindowViewModel(service, actions, new FakeTicker(), CancellationToken.None, activity);
            var window = new MainWindow { DataContext = vm };
            window.Show();
            Dispatcher.UIThread.RunJobs();

            var expander = window.GetVisualDescendants().OfType<Expander>().First(e => e.Name == "ActivityExpander");
            var collapsed = reader.ReadCalls; // starts collapsed — no read

            expander.IsExpanded = true;
            Dispatcher.UIThread.RunJobs();
            await activity.PendingRefreshForTesting!;
            var expanded = reader.ReadCalls;

            vm.ShowSessionsCommand.Execute().Subscribe();
            Dispatcher.UIThread.RunJobs();
            var onSessions = reader.ReadCalls;

            vm.ShowHomeCommand.Execute().Subscribe();
            Dispatcher.UIThread.RunJobs();
            await activity.PendingRefreshForTesting!;
            var backOnHome = reader.ReadCalls;

            expander.IsExpanded = false;
            Dispatcher.UIThread.RunJobs();
            var afterCollapse = reader.ReadCalls;

            expander.IsExpanded = true;
            Dispatcher.UIThread.RunJobs();
            await activity.PendingRefreshForTesting!;
            var afterReexpand = reader.ReadCalls;

            window.Hide();
            Dispatcher.UIThread.RunJobs();
            var afterHide = reader.ReadCalls;

            window.Show();
            Dispatcher.UIThread.RunJobs();
            await activity.PendingRefreshForTesting!;
            var afterReshow = reader.ReadCalls;

            window.Close();
            Dispatcher.UIThread.RunJobs();

            return (collapsed, expanded, onSessions, backOnHome, afterCollapse, afterReexpand, afterHide, afterReshow);
        });

        await Assert.That(reads.collapsed).IsEqualTo(0);
        await Assert.That(reads.expanded).IsEqualTo(1); // expanding on Home: one immediate read
        await Assert.That(reads.onSessions).IsEqualTo(1); // leaving Home is a FALSE transition
        await Assert.That(reads.backOnHome).IsEqualTo(2);
        await Assert.That(reads.afterCollapse).IsEqualTo(2); // collapsing is a FALSE transition
        await Assert.That(reads.afterReexpand).IsEqualTo(3);
        await Assert.That(reads.afterHide).IsEqualTo(3); // hiding is a FALSE transition
        await Assert.That(reads.afterReshow).IsEqualTo(4);
    }

    /// The surface swap itself (spec §3) — the XAML side of what WorkspaceNavigationTests pins on
    /// the ViewModel. WorkspaceView is materialized from a template rather than always present, so
    /// this also proves the terminal control is CONSTRUCTED only once a workspace exists; closing
    /// it lands on the Sessions surface's placeholder, never back on Home.
    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task Opening_a_session_swaps_the_window_from_home_to_the_workspace() {
        await AvaloniaSession.WithImmediateRxScheduler(async () => {
            var swap = await AvaloniaSession.DispatchAsync(() => {
                var service = new FakeDaemonClientService();
                var (actions, _) = NewActions(service);
                var attach = new FakeTerminalAttachClientFactory();
                var vm = new MainWindowViewModel(
                    service, actions, new FakeTicker(), CancellationToken.None, TestActivity.New(),
                    workspaceFactory: agentId => new WorkspaceViewModel(
                        agentId, service, actions, attach.Factory, () => new FakeTerminalSurface(), new FakeTimeProvider()));
                var window = new MainWindow { DataContext = vm };
                window.Show();
                Dispatcher.UIThread.RunJobs();

                Control Surface(string name) =>
                    window.GetVisualDescendants().OfType<Control>().First(c => c.Name == name);

                var homeVisible = Surface("HomeSurface").IsVisible;
                var workspacesBefore = window.GetVisualDescendants().OfType<WorkspaceView>().Count();

                vm.OpenSession("0123456789abcdef0123456789abcdef");
                Dispatcher.UIThread.RunJobs();
                var homeHidden = Surface("HomeSurface").IsVisible;
                var sessionsVisible = Surface("SessionsSurface").IsVisible;
                var opened = window.GetVisualDescendants().OfType<WorkspaceView>().ToList();
                // Read NOW, not in the return tuple: closing below detaches the view and clears the
                // very DataContext this is asserting on.
                var boundToWorkspace = opened.FirstOrDefault()?.DataContext is WorkspaceViewModel;

                vm.CloseWorkspace();
                Dispatcher.UIThread.RunJobs();
                var stillSessions = Surface("SessionsSurface").IsVisible;
                var placeholderBack = Surface("WorkspacePlaceholder").IsVisible;
                var workspacesAfter = window.GetVisualDescendants().OfType<WorkspaceView>().Count();

                window.Close();
                Dispatcher.UIThread.RunJobs();

                return (homeVisible, workspacesBefore, homeHidden, sessionsVisible, OpenedCount: opened.Count,
                    boundToWorkspace, stillSessions, placeholderBack, workspacesAfter);
            });

            await Assert.That(swap.homeVisible).IsTrue();
            await Assert.That(swap.workspacesBefore).IsEqualTo(0); // nothing terminal-shaped until a session is opened
            await Assert.That(swap.homeHidden).IsFalse();
            await Assert.That(swap.sessionsVisible).IsTrue();
            await Assert.That(swap.OpenedCount).IsEqualTo(1);
            await Assert.That(swap.boundToWorkspace).IsTrue();
            await Assert.That(swap.stillSessions).IsTrue();
            await Assert.That(swap.placeholderBack).IsTrue();
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
                var rail = new SessionRailViewModel(service, id => vm!.OpenSession(id),
                    p => p.Contains("/wt/", StringComparison.Ordinal)
                        ? p[..p.IndexOf("/wt/", StringComparison.Ordinal)]
                        : p);
                vm = new MainWindowViewModel(service, actions, new FakeTicker(), CancellationToken.None, TestActivity.New(),
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

    /// The tabless boot (spec §3): the window opens on Home with no TabControl anywhere in its
    /// visual tree, and the one way across to Sessions is HomeSessionsButton.
    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task Window_boots_tabless_on_home_with_a_sessions_entry() {
        await AvaloniaSession.WithImmediateRxScheduler(async () => {
            var ok = await AvaloniaSession.DispatchAsync(() => {
                var service = new FakeDaemonClientService();
                service.SnapshotsSubject.OnNext(Snap());
                var (actions, _) = NewActions(service);
                var vm = new MainWindowViewModel(service, actions, new FakeTicker(), CancellationToken.None, TestActivity.New());
                var window = new MainWindow { DataContext = vm };
                window.Show();
                Dispatcher.UIThread.RunJobs();

                var noTabs = !window.GetVisualDescendants().OfType<TabControl>().Any();
                var sessionsButton = window.GetVisualDescendants().OfType<Button>().Any(b => b.Name == "HomeSessionsButton");
                window.Close();
                Dispatcher.UIThread.RunJobs();
                return noTabs && sessionsButton;
            });
            await Assert.That(ok).IsTrue();
        });
    }
}
