using System.Reactive.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Capacitor.App.Services;
using Capacitor.App.ViewModels;
using Capacitor.App.Views;
using Capacitor.Cli.Core.LocalIpc;
using DynamicData;
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

    // Home is the default-selected tab; the Agents TabItem carries no x:Name
    // (only HomeTabItem/ActivityTabItem do), so it's found by its header text instead — same
    // Name-scope lookup style as everything else in this file, one step removed.
    static void SelectAgentsTab(MainWindow window) {
        var tabs = window.GetVisualDescendants().OfType<TabControl>().First(t => t.Name == "MainTabs");
        var agentsTab = window.GetVisualDescendants().OfType<TabItem>().First(t => t.Header as string == "Agents");
        tabs.SelectedItem = agentsTab;
        Dispatcher.UIThread.RunJobs();
    }

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

    /// Spec §8 empty state: "No agents running" renders only while Connected AND the Agents
    /// cache is empty. Deliberately NOT wrapped in WithImmediateRxScheduler: no agent is ever
    /// added to service.Agents here, so the injected FakeTicker is never subscribed either way —
    /// but the real dispatcher is what MainWindowViewModel's own production ctor is meant to run
    /// under, so this stays close to that path.
    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task Empty_agents_grid_shows_no_agents_running_while_connected() {
        var (rendered, emptyStateVisible) = await AvaloniaSession.DispatchAsync(() => {
            var service = new FakeDaemonClientService();
            service.SnapshotsSubject.OnNext(Snap(connection: "connected"));
            service.StatusSubject.OnNext(new AttachStatus(AttachState.Connected, null, null));

            var (actions, _) = NewActions(service);
            var vm = new MainWindowViewModel(service, actions, new FakeTicker(), CancellationToken.None, TestActivity.New());
            var window = new MainWindow { DataContext = vm };
            window.Show();
            Dispatcher.UIThread.RunJobs();

            // Home is the default-selected tab — this test asserts what the
            // Agents tab CONTAINS, so select it explicitly rather than relying on it opening first.
            SelectAgentsTab(window);

            var emptyState = window.GetVisualDescendants().OfType<TextBlock>()
                .FirstOrDefault(t => t.Name == "EmptyStateText");
            var texts = string.Join('\n', window.GetVisualDescendants().OfType<TextBlock>().Select(t => t.Text ?? ""));

            window.Close();
            Dispatcher.UIThread.RunJobs();

            return (texts, emptyState is { IsVisible: true });
        });

        await Assert.That(rendered).Contains("No agents running");
        await Assert.That(emptyStateVisible).IsTrue();
    }

    /// Fix-round 2: the column-header row (Kind/Vendor/Repo/...) rendered even with zero agents
    /// and read as noise above "No agents running". Hidden while the Agents collection is empty,
    /// visible as soon as a row exists — the "Agents" section title and the empty-state line both
    /// stay either way (spec §8, Converters.cs HeaderRowVisibleConverter doc comment).
    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task Agents_grid_header_hidden_when_empty_and_visible_once_a_row_exists() {
        var (headerVisibleEmpty, headerVisibleWithRow) = await AvaloniaSession.DispatchAsync(() => {
            var service = new FakeDaemonClientService();
            service.SnapshotsSubject.OnNext(Snap(connection: "connected"));
            service.StatusSubject.OnNext(new AttachStatus(AttachState.Connected, null, null));

            var (actions, _) = NewActions(service);
            var vm = new MainWindowViewModel(service, actions, new FakeTicker(), CancellationToken.None, TestActivity.New());
            var window = new MainWindow { DataContext = vm };
            window.Show();
            Dispatcher.UIThread.RunJobs();

            // Home is the default-selected tab — this test asserts what the
            // Agents tab CONTAINS, so select it explicitly rather than relying on it opening first.
            SelectAgentsTab(window);

            Grid Header() => window.GetVisualDescendants().OfType<Grid>().First(g => g.Name == "AgentsGridHeader");

            var emptyVisible = Header().IsVisible;

            service.Agents.AddOrUpdate(new AgentStatusDto(
                "a", "agent", "claude", "/repos/kcap-cli", "Running", null, null, null, DateTime.UtcNow, null, null));
            Dispatcher.UIThread.RunJobs();
            var withRowVisible = Header().IsVisible;

            window.Close();
            Dispatcher.UIThread.RunJobs();

            return (emptyVisible, withRowVisible);
        });

        await Assert.That(headerVisibleEmpty).IsFalse();
        await Assert.That(headerVisibleWithRow).IsTrue();
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

    // ---- Activity tab visibility wiring (spec §7) ----
    //
    // Proves the real production wiring end to end — MainWindow.axaml's TabControl selection and
    // the window's own IsVisible (Show()/Hide()) both drive ActivityViewModel.OnTabVisibleChanged
    // through the code-behind, not just that the ViewModel reacts correctly in isolation
    // (ActivityViewModelTests already covers that). Selecting Agents leaves the read count
    // unchanged; each TRUE transition (select Activity, then re-Show after a Hide) issues exactly
    // one more immediate read; Hide is a FALSE transition and reads nothing. Each TRUE transition
    // is awaited via PendingRefreshForTesting — the VM's stat+read now hops off the UI thread,
    // so RunJobs() alone no longer guarantees the read has landed.
    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task Activity_tab_visibility_follows_selection_and_window_IsVisible() {
        var (afterAgents, afterActivity, afterHide, afterReshow) = await AvaloniaSession.DispatchAsync(async () => {
            var service = new FakeDaemonClientService();
            var (actions, _) = NewActions(service);
            var reader = new ScriptedReader();
            reader.Set(new ConsentLogReadResult([], true));
            var activity = new ActivityViewModel(reader.Read, () => "k", new FakeTicker());
            var vm = new MainWindowViewModel(service, actions, new FakeTicker(), CancellationToken.None, activity);
            var window = new MainWindow { DataContext = vm };
            window.Show();
            Dispatcher.UIThread.RunJobs();

            var readsAgents = reader.ReadCalls; // Agents tab selected by default — no Activity read

            var tabs = window.GetVisualDescendants().OfType<TabControl>().First(t => t.Name == "MainTabs");
            var activityTab = window.GetVisualDescendants().OfType<TabItem>().First(t => t.Name == "ActivityTabItem");
            tabs.SelectedItem = activityTab;
            Dispatcher.UIThread.RunJobs();
            await activity.PendingRefreshForTesting!;
            var readsActivity = reader.ReadCalls;

            window.Hide();
            Dispatcher.UIThread.RunJobs();
            var readsHidden = reader.ReadCalls;

            window.Show();
            Dispatcher.UIThread.RunJobs();
            await activity.PendingRefreshForTesting!;
            var readsReshown = reader.ReadCalls;

            window.Close();
            Dispatcher.UIThread.RunJobs();

            return (readsAgents, readsActivity, readsHidden, readsReshown);
        });

        await Assert.That(afterAgents).IsEqualTo(0);
        await Assert.That(afterActivity).IsEqualTo(1); // selecting Activity: one immediate read
        await Assert.That(afterHide).IsEqualTo(1); // hiding is a FALSE transition — no read
        await Assert.That(afterReshow).IsEqualTo(2); // re-showing: another TRUE transition
    }
}
