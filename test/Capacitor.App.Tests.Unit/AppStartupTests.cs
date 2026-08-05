using Avalonia.Controls;
using Avalonia.Threading;
using Avalonia.VisualTree;
using TUnit.Assertions.Enums;
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

    /// Regression coverage for a P2 bug found in review: the startup catch used to write to
    /// Console.Error and call desktop.Shutdown(1) directly — but App is OutputType=WinExe, so a
    /// normal GUI launch has no console, and a startup failure (bad config, window construction
    /// throw) made the app silently vanish with zero actionable error. BuildStartupErrorWindow
    /// is the replacement: a plain, visible window with a copyable (SelectableTextBlock) lead
    /// line plus the exception's full ToString(). This proves the rendered text actually carries
    /// both, the same way MainWindowSmokeTests proves bound VM text actually reaches the screen.
    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task BuildStartupErrorWindow_renders_lead_line_and_exception_details() {
        var rendered = await AvaloniaSession.DispatchAsync(() => {
            var window = AppUnderTest.BuildStartupErrorWindow(new InvalidOperationException("boom-marker"));
            window.Show();
            Dispatcher.UIThread.RunJobs();

            var texts = window.GetVisualDescendants()
                .Select(v => v switch {
                    SelectableTextBlock stb => stb.Text,
                    TextBlock tb => tb.Text,
                    _ => null,
                })
                .Where(t => t is not null);
            var joined = string.Join('\n', texts);

            window.Close();
            return joined;
        });

        await Assert.That(rendered).Contains("The app failed to start. Details:");
        await Assert.That(rendered).Contains("boom-marker");
    }

    /// Regression coverage for a P1 bug found in re-review: with the app's default ShutdownMode
    /// (OnLastWindowClose, never set elsewhere), closing the error window used to exit 0 instead
    /// of 1. Window.HandleClosed raises the CLR Closed event (our handler, calling Shutdown(1))
    /// BEFORE the routed WindowClosedEvent that OnLastWindowClose listens for; that routed event
    /// then drives an OnLastWindowClose TryShutdown() with its default exit code 0, which — via
    /// App.OnShutdownRequested's deferred cancel-then-retry dance — unconditionally overwrites
    /// the exit code back to 0. ShowStartupError now pins ShutdownMode to OnExplicitShutdown
    /// before showing the window, which disarms that whole branch. This drives ShowStartupError
    /// against a fake lifetime (no real desktop lifetime needed) and asserts: the mode is pinned
    /// and MainWindow assigned with no Shutdown call yet, then closing the window produces
    /// exactly one Shutdown call, with exit code 1.
    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task ShowStartupError_pins_explicit_shutdown_and_exits_with_code_1_on_close() {
        var (modeAfterShow, mainWindowAssigned, callsBeforeClose, callsAfterClose) =
            await AvaloniaSession.DispatchAsync(() => {
                var (desktop, fake) = FakeClassicDesktopLifetime.Create();

                AppUnderTest.ShowStartupError(desktop, new InvalidOperationException("boom"));
                Dispatcher.UIThread.RunJobs();

                var mode = fake.ShutdownMode;
                var assigned = fake.MainWindow is not null;
                var before = fake.ShutdownCalls.ToArray();

                fake.MainWindow!.Close();
                Dispatcher.UIThread.RunJobs();

                var after = fake.ShutdownCalls.ToArray();
                return (mode, assigned, before, after);
            });

        await Assert.That(modeAfterShow).IsEqualTo(ShutdownMode.OnExplicitShutdown);
        await Assert.That(mainWindowAssigned).IsTrue();
        await Assert.That(callsBeforeClose).IsEmpty();
        await Assert.That(callsAfterClose).IsEquivalentTo([1], CollectionOrdering.Matching);
    }
}
