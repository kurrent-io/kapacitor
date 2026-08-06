using System.Runtime.CompilerServices;
using Avalonia.Controls;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Capacitor.App.Services;
using Capacitor.Cli.Core.LocalIpc;
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

    sealed class NullProcessRunner : IProcessRunner {
        public Task<(int ExitCode, string Stderr)> RunAsync(string fileName, string[] args, CancellationToken ct) =>
            Task.FromResult((0, ""));
    }

    /// Minimal stand-in for LocalControlClient.RunAsync: yields Connecting once, then sits
    /// forever until its ct is cancelled (RestartLoopAsync/DisposeAsync's normal teardown path)
    /// — enough to prove a DaemonClientService actually has a LIVE loop to dispose.
    sealed class ForeverRunClient {
        public int LiveEnumerations;

        public async IAsyncEnumerable<LocalControlEvent> Run([EnumeratorCancellation] CancellationToken ct) {
            Interlocked.Increment(ref LiveEnumerations);
            try {
                yield return new LocalControlEvent.Connecting();
                await Task.Delay(Timeout.Infinite, ct);
            } finally {
                Interlocked.Decrement(ref LiveEnumerations);
            }
        }
    }

    static async Task WaitUntilAsync(Func<bool> condition, TimeSpan? timeout = null) {
        var deadline = DateTime.UtcNow + (timeout ?? TimeSpan.FromSeconds(5));
        while (!condition()) {
            if (DateTime.UtcNow > deadline) throw new TimeoutException("condition not met in time");
            await Task.Delay(10);
        }
    }

    /// Regression coverage for a P2 bug found in review: a startup failure that happened AFTER
    /// service.Start()/_service assignment (e.g. BuildAndShowMainWindow throwing) used to go
    /// straight to ShowStartupError, abandoning the live IPC pump/socket — closing the error
    /// window force-shuts-down via desktop.Shutdown(1), which bypasses OnShutdownRequested and
    /// its async DisposeAsync entirely, so nothing else would ever clean it up. Drives the
    /// extracted HandleStartupFailureAsync against a REAL DaemonClientService (constructed with
    /// fakes, so disposal is directly observable) and asserts: the shutdown token is cancelled,
    /// the service's loop actually ends (proving DisposeAsync ran, not just was called), and the
    /// error window is still shown afterward exactly as ShowStartupError already guarantees.
    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task HandleStartupFailureAsync_disposes_the_live_service_before_showing_the_error_window() {
        var runClient = new ForeverRunClient();
        var service = new DaemonClientService("daemon-a", runClient.Run, new NullProcessRunner(), "kcap");
        service.Start();
        await WaitUntilAsync(() => runClient.LiveEnumerations >= 1);

        var shutdown = new CancellationTokenSource();

        var (modeAfterShow, mainWindowAssigned) = await AvaloniaSession.DispatchAsync(async () => {
            var (desktop, fake) = FakeClassicDesktopLifetime.Create();
            await AppUnderTest.HandleStartupFailureAsync(
                desktop, new InvalidOperationException("boom"), service, shutdown);
            Dispatcher.UIThread.RunJobs();
            return (fake.ShutdownMode, fake.MainWindow is not null);
        });

        await Assert.That(shutdown.IsCancellationRequested).IsTrue();
        await Assert.That(modeAfterShow).IsEqualTo(ShutdownMode.OnExplicitShutdown);
        await Assert.That(mainWindowAssigned).IsTrue();
        await WaitUntilAsync(() => runClient.LiveEnumerations == 0, TimeSpan.FromSeconds(5));

        // A second DisposeAsync (mirroring the real catch path's `_service = null` guard against
        // a later OnShutdownRequested double-dispose) must be a safe no-op, not a throw.
        await service.DisposeAsync();
    }

    /// Regression coverage for a P2 bug found in re-review: TryShutdown() in the DEFERRED
    /// shutdown path (OnShutdownRequested -> DisposeAndShutdownAsync — e.g. Cmd+Q while the
    /// startup-error window is still up) used to be called with no exit code, defaulting to 0 —
    /// silently overwriting the startup failure with an apparent success. Drives the extracted
    /// DisposeAndConfirmShutdownAsync directly: a real DaemonClientService (fakes, disposal
    /// observable) and the same fake IClassicDesktopStyleApplicationLifetime used above, with
    /// exitCode: 1 (what StartAsync's catch now sets on _exitCode before a later
    /// OnShutdownRequested can reach this path). No Avalonia session needed — DispatchProxy and
    /// DaemonClientService are both plain .NET, same as DaemonClientServiceTests.
    [Test]
    public async Task DisposeAndConfirmShutdownAsync_disposes_then_confirms_then_carries_the_exit_code() {
        var runClient = new ForeverRunClient();
        var service = new DaemonClientService("daemon-a", runClient.Run, new NullProcessRunner(), "kcap");
        service.Start();
        await WaitUntilAsync(() => runClient.LiveEnumerations >= 1);

        var (desktop, fake) = FakeClassicDesktopLifetime.Create();
        // Ordering pin: markConfirmed must observably run BEFORE TryShutdown is called — proven
        // by checking fake.ShutdownCalls is still empty at the moment markConfirmed fires.
        var confirmedBeforeShutdownCall = false;

        await AppUnderTest.DisposeAndConfirmShutdownAsync(
            service.DisposeAsync,
            markConfirmed: () => confirmedBeforeShutdownCall = fake.ShutdownCalls.Count == 0,
            desktop,
            exitCode: 1);

        await Assert.That(confirmedBeforeShutdownCall).IsTrue();
        await Assert.That(fake.ShutdownCalls).IsEquivalentTo([1], CollectionOrdering.Matching);
        await WaitUntilAsync(() => runClient.LiveEnumerations == 0, TimeSpan.FromSeconds(5));
    }

    /// Same seam, but the normal (non-failure) exit code: a plain Cmd+Q with no prior startup
    /// failure must still carry 0 through — this fix must not change the happy path.
    [Test]
    public async Task DisposeAndConfirmShutdownAsync_normal_shutdown_carries_exit_code_zero() {
        var runClient = new ForeverRunClient();
        var service = new DaemonClientService("daemon-a", runClient.Run, new NullProcessRunner(), "kcap");
        service.Start();
        await WaitUntilAsync(() => runClient.LiveEnumerations >= 1);

        var (desktop, fake) = FakeClassicDesktopLifetime.Create();

        await AppUnderTest.DisposeAndConfirmShutdownAsync(service.DisposeAsync, markConfirmed: () => { }, desktop, exitCode: 0);

        await Assert.That(fake.ShutdownCalls).IsEquivalentTo([0], CollectionOrdering.Matching);
    }

    /// Regression coverage for a Qodo review finding: DisposeAndConfirmShutdownAsync used to call
    /// disposeAsync() with no surrounding try/catch/finally, so a throw left markConfirmed and
    /// TryShutdown never called — _shutdownConfirmed stuck false while _shutdownStarted stayed
    /// true, cancelling every later quit forever. Drives a disposeAsync delegate that throws and
    /// asserts confirm still happens and TryShutdown still carries the exit code.
    [Test]
    public async Task DisposeAndConfirmShutdownAsync_confirms_and_shuts_down_when_dispose_throws() {
        var (desktop, fake) = FakeClassicDesktopLifetime.Create();
        var confirmed = false;

        await AppUnderTest.DisposeAndConfirmShutdownAsync(
            disposeAsync: () => throw new InvalidOperationException("dispose-boom"),
            markConfirmed: () => confirmed = true,
            desktop,
            exitCode: 1);

        await Assert.That(confirmed).IsTrue();
        await Assert.That(fake.ShutdownCalls).IsEquivalentTo([1], CollectionOrdering.Matching);
    }
}
