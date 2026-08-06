using System.Reactive.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Capacitor.App.Services;
using Capacitor.App.ViewModels;
using Capacitor.App.Views;
using static Capacitor.App.Tests.Unit.FakeDaemonClientService;

namespace Capacitor.App.Tests.Unit;

/// Headless rendering acceptance for the deliverable's identity block (spec §8): boot a real
/// MainWindow against a fake service pre-fed (Connected, snapshot), Show() it, and assert the
/// rendered text actually contains the daemon name/version/server URL/agent count — not just
/// that the VM's properties hold the right values (MainWindowViewModelTests already covers
/// that in isolation).
public class MainWindowSmokeTests {
    // Real AppNotifier (not RecordingNotifier) — none of these tests exercise the banner, so the
    // production notifier is fine; it must be the SAME instance passed to both AgentActionService
    // and MainWindowViewModel, mirroring how App.axaml.cs wires the two together.
    static (AgentActionService Actions, IAppNotifier Notifier) NewActions(FakeDaemonClientService service) {
        var notifier = new AppNotifier();
        var actions = new AgentActionService(new ScriptedLocalControlOps(), notifier, new RecordingOpener(), service.SnapshotsSubject, CancellationToken.None);
        return (actions, notifier);
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

                var (actions, notifier) = NewActions(service);
                var vm = new MainWindowViewModel(service, actions, notifier, CancellationToken.None);
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
            var (actions, notifier) = NewActions(service);
            var vm = new MainWindowViewModel(service, actions, notifier, CancellationToken.None);
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
            var (actions, notifier) = NewActions(service);
            var vm = new MainWindowViewModel(service, actions, notifier, shutdown.Token);
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
    /// added to service.Agents here, so the real shared ticker inside MainWindowViewModel is
    /// never subscribed either way — but the real dispatcher is what MainWindowViewModel's own
    /// production ctor is meant to run under, so this stays close to that path.
    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task Empty_agents_grid_shows_no_agents_running_while_connected() {
        var (rendered, emptyStateVisible) = await AvaloniaSession.DispatchAsync(() => {
            var service = new FakeDaemonClientService();
            service.SnapshotsSubject.OnNext(Snap(connection: "connected"));
            service.StatusSubject.OnNext(new AttachStatus(AttachState.Connected, null, null));

            var (actions, notifier) = NewActions(service);
            var vm = new MainWindowViewModel(service, actions, notifier, CancellationToken.None);
            var window = new MainWindow { DataContext = vm };
            window.Show();
            Dispatcher.UIThread.RunJobs();

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
}
