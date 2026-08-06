using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Media;
using Capacitor.App.Services;
using Capacitor.App.ViewModels;
using Capacitor.App.Views;
using Capacitor.Cli.Core.LocalIpc;

namespace Capacitor.App;

public partial class App : Application {
    // Linked to the app's shutdown sequence below; the token StartDaemonCommand's WAIT is
    // built against (Task 4 carry-note: never CancellationToken.None — an unbounded wait would
    // survive app exit).
    readonly CancellationTokenSource _shutdown = new();
    DaemonClientService? _service; // concrete type: IAsyncDisposable is not on the interface
    bool _shutdownStarted;
    bool _shutdownConfirmed;
    // 0 = normal shutdown. Set to 1 on a startup failure so the DEFERRED shutdown path (Cmd+Q /
    // platform shutdown while the error window is showing — OnShutdownRequested ->
    // DisposeAndShutdownAsync) still reports failure, instead of TryShutdown()'s platform
    // default of 0 silently overwriting it.
    int _exitCode;

    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted() {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop) {
            desktop.ShutdownRequested += OnShutdownRequested;
            _ = StartAsync(desktop);
        }

        base.OnFrameworkInitializationCompleted();
    }

    // This continuation is the ONLY path to a visible window: OnFrameworkInitializationCompleted
    // fires it fire-and-forget and returns immediately, so an exception escaping here would
    // otherwise leave a live process with an empty dispatcher loop, no window, and no error
    // surface (stderr is invisible for a GUI-launched WinExe) — it must fail loudly instead.
    async Task StartAsync(IClassicDesktopStyleApplicationLifetime desktop) {
        try {
            var service = await DaemonClientService.CreateDefaultAsync();
            service.Start();
            _service = service;
            desktop.MainWindow = BuildAndShowMainWindow(service, _shutdown.Token);
        } catch (Exception ex) {
            // BEFORE any await: a shutdown request can arrive while cleanup below is still
            // awaiting (or if the helper itself throws), and the deferred path reads this.
            _exitCode = 1;
            Console.Error.WriteLine($"kcap app failed to start: {ex}");
            await HandleStartupFailureAsync(desktop, ex, _service, _shutdown);
            _service = null; // already disposed above — never let a later OnShutdownRequested
                              // (e.g. Cmd+Q while the error window is up) dispose it a second time
        }
    }

    // Split out of the catch so a test can drive "dispose-then-show-error" against a real
    // DaemonClientService (constructed with fakes, disposal observable) and the same fake
    // IClassicDesktopStyleApplicationLifetime AppStartupTests already uses for ShowStartupError.
    // Ordering matters: dispose WHILE WE STILL CAN. `service` may already be live (Start()
    // called, socket/IPC pump running) if the failure happened later in StartAsync (e.g.
    // BuildAndShowMainWindow throwing) — and the error window's own close handler force-shuts-
    // down via desktop.Shutdown(1), which bypasses OnShutdownRequested/DisposeAndShutdownAsync
    // entirely, so nothing else would ever run this cleanup.
    internal static async Task HandleStartupFailureAsync(
            IClassicDesktopStyleApplicationLifetime desktop, Exception ex, DaemonClientService? service,
            CancellationTokenSource shutdown) {
        if (service is not null) {
            shutdown.Cancel();
            try {
                await service.DisposeAsync();
            } catch (Exception disposeEx) {
                // The ORIGINAL startup exception (ex, already captured and about to be shown
                // below) must never be masked by a secondary dispose failure — append it to the
                // same Console.Error channel instead of letting it propagate.
                Console.Error.WriteLine($"kcap app failed to dispose the daemon client service during startup-failure cleanup: {disposeEx}");
            }
        }
        ShowStartupError(desktop, ex);
    }

    // Split out of the catch so a test can drive it against a fake
    // IClassicDesktopStyleApplicationLifetime (no real windowing/desktop lifetime needed) and
    // assert the ShutdownMode pin, the MainWindow assignment, and the deferred Shutdown(1) all
    // happen in the right order.
    internal static void ShowStartupError(IClassicDesktopStyleApplicationLifetime desktop, Exception ex) {
        // Decompiler-verified: the app never sets ShutdownMode elsewhere, so it defaults to
        // OnLastWindowClose. Window.HandleClosed raises the CLR Closed event (our handler below,
        // which calls Shutdown(1)) BEFORE the routed WindowClosedEvent that OnLastWindowClose
        // listens for. So closing the error window used to run: our Shutdown(1) (sets
        // _exitCode=1) -> THEN the routed event -> _windows hits 0 -> an OnLastWindowClose-driven
        // TryShutdown() with its default exit code 0 -> App.OnShutdownRequested's deferred
        // dance -> a second TryShutdown() whose DoShutdown unconditionally overwrites _exitCode
        // with 0. Net effect: the most common startup failure exited 0. Pinning
        // OnExplicitShutdown disarms that whole OnLastWindowClose branch, so our explicit
        // Shutdown(1) below is the only shutdown and nothing overwrites its exit code.
        desktop.ShutdownMode = ShutdownMode.OnExplicitShutdown;

        // Showing a window here is legal before Avalonia's main loop starts — it's exactly what
        // StartWithClassicDesktopLifetime's own ShowMainWindow() does right after Start. Calling
        // desktop.Shutdown(1) directly, as this catch used to, is what previously threw when
        // startup faulted synchronously (before the main loop began) — so this shape resolves
        // that pre-main-loop edge case rather than worsening it.
        var errorWindow = BuildStartupErrorWindow(ex);
        if (desktop.MainWindow is null) desktop.MainWindow = errorWindow;
        errorWindow.Closed += (_, _) => desktop.Shutdown(1);
        errorWindow.Show();
    }

    // Last-resort UI for a startup failure: Console.Error above is invisible on a normal GUI
    // launch (OutputType=WinExe has no console), so this window is the only channel that
    // actually reaches the user. SelectableTextBlock (not TextBlock) keeps the stack trace
    // copyable for a bug report.
    internal static Window BuildStartupErrorWindow(Exception ex) =>
        new() {
            Title = "Kurrent Capacitor — startup failed",
            Width = 640,
            Height = 400,
            Content = new ScrollViewer {
                Content = new SelectableTextBlock {
                    Text = $"The app failed to start. Details:\n{ex}",
                    TextWrapping = TextWrapping.Wrap,
                },
            },
        };

    // Split out of StartAsync so a test can drive "build VM+window, assign, and Show()" against
    // a fake service without needing a real daemon/profile (CreateDefaultAsync does real config
    // I/O). This is also the actual bug fix: Avalonia's StartWithClassicDesktopLifetime calls
    // ShowMainWindow() exactly ONCE, synchronously, right after Start — and at that moment
    // desktop.MainWindow is still null, because CreateDefaultAsync genuinely awaits (config.json
    // read). By the time this continuation resumes and assigns desktop.MainWindow, nothing else
    // will ever call .Show() for us, so this method must call it explicitly. Show() on an
    // already-visible window is a no-op, so this stays correct even if a future edit changes the
    // timing such that ShowMainWindow() DOES still see a non-null MainWindow.
    internal static MainWindow BuildAndShowMainWindow(IDaemonClientService service, CancellationToken shutdownToken) {
        // Real implementations, constructed inline: full DI composition is a later task (spec
        // §7, §11) — this just needs to keep the app wired end to end for THIS slice.
        var notifier = new AppNotifier();
        var ops = new LocalControlOps(service.DaemonName);
        var actions = new AgentActionService(ops, notifier, new ShellUrlOpener(), service.Snapshots, shutdownToken);

        var window = new MainWindow { DataContext = new MainWindowViewModel(service, actions, notifier, shutdownToken) };
        window.Show();
        return window;
    }

    // Async-safe shutdown: ShutdownRequested fires on the UI thread and can be cancelled, so the
    // FIRST pass defers it (e.Cancel = true), cancels the shutdown token (abandoning any
    // in-flight StartDaemonAsync WAIT — never the spawned daemon), and disposes the service in
    // the background (no live socket read/child-process wait may survive app exit, spec §5).
    // Once that completes, TryShutdown() re-raises this same event; the SECOND pass is let
    // through. This never blocks the UI thread on the async disposal.
    void OnShutdownRequested(object? sender, ShutdownRequestedEventArgs e) {
        if (_shutdownConfirmed) return;

        e.Cancel = true;
        _shutdown.Cancel();
        if (_shutdownStarted) return; // e.g. a rapid double Cmd+Q — disposal is already in flight
        _shutdownStarted = true;
        _ = DisposeAndShutdownAsync();
    }

    async Task DisposeAndShutdownAsync() {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop) {
            await DisposeAndConfirmShutdownAsync(
                _service is null ? null : _service.DisposeAsync, () => _shutdownConfirmed = true, desktop, _exitCode);
        } else {
            if (_service is not null) await _service.DisposeAsync();
            _shutdownConfirmed = true;
        }
    }

    // Split out of DisposeAndShutdownAsync so a test can drive the full deferred-shutdown pass —
    // dispose, THEN mark confirmed, THEN shut down carrying an exit code — against a fake
    // IClassicDesktopStyleApplicationLifetime, without needing a live App instance.
    // `disposeAsync` is a delegate (not the concrete DaemonClientService) so a test can inject a
    // throwing disposal without depending on how DaemonClientService itself might fail.
    // Regression coverage for a P2 bug found in re-review: TryShutdown() used to be called with
    // no exit code (defaulting to 0), so Cmd+Q/platform shutdown while the startup-error window
    // was still showing silently overwrote the startup-failure exit code with success. Ordering
    // is preserved exactly from the original inline body: `markConfirmed` MUST run before
    // `TryShutdown`, because TryShutdown can re-raise ShutdownRequested synchronously and
    // OnShutdownRequested's early-return guard (`if (_shutdownConfirmed) return;`) depends on
    // that happening first.
    internal static async Task DisposeAndConfirmShutdownAsync(
            Func<ValueTask>? disposeAsync, Action markConfirmed, IClassicDesktopStyleApplicationLifetime desktop,
            int exitCode) {
        // A throwing disposeAsync must never skip markConfirmed/TryShutdown — otherwise
        // _shutdownConfirmed is never set while _shutdownStarted stays true, and every later
        // quit is cancelled forever.
        try {
            if (disposeAsync is not null) await disposeAsync();
        } catch (Exception ex) {
            Console.Error.WriteLine($"kcap app failed to dispose the daemon client service during shutdown: {ex}");
        } finally {
            markConfirmed();
            desktop.TryShutdown(exitCode);
        }
    }
}
