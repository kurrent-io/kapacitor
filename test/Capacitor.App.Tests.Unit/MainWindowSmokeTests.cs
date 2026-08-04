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

                var vm = new MainWindowViewModel(service, CancellationToken.None);
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
            var vm = new MainWindowViewModel(service, CancellationToken.None);
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
}
