using Avalonia.Threading;
using AppUnderTest = Capacitor.App.App;

namespace Capacitor.App.Tests.Unit;

/// Regression coverage for a Critical bug found in review: OnFrameworkInitializationCompleted
/// kicks off App.StartAsync fire-and-forget and returns immediately; Avalonia's
/// StartWithClassicDesktopLifetime calls ShowMainWindow() exactly ONCE, synchronously, right
/// after Start — and at that moment desktop.MainWindow was still null, because
/// DaemonClientService.CreateDefaultAsync genuinely awaits real config I/O. By the time the
/// continuation resumed and assigned desktop.MainWindow, nothing else ever called .Show() —
/// the app booted a dispatcher loop showing nothing.
///
/// CreateDefaultAsync itself needs a real profile/daemon and isn't a seam a unit test can drive,
/// so this exercises the closest testable seam: App.BuildAndShowMainWindow (internal, exposed to
/// this assembly via InternalsVisibleTo) is the exact "build VM+window, assign, and Show()"
/// continuation extracted out of StartAsync — this test proves THAT method actually leaves the
/// window visible, against a fake service, without needing a real desktop lifetime or daemon.
public class AppStartupTests {
    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task BuildAndShowMainWindow_leaves_the_window_visible() {
        var isVisible = await AvaloniaSession.DispatchAsync(() => {
            var service = new FakeDaemonClientService();
            var window = AppUnderTest.BuildAndShowMainWindow(service, CancellationToken.None);
            Dispatcher.UIThread.RunJobs(); // flush the deferred Loaded post (diagnostic parity with the smoke test)

            var visible = window.IsVisible;
            window.Close();
            return visible;
        });

        await Assert.That(isVisible).IsTrue();
    }
}
