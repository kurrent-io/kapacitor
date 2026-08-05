using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Media;
using Capacitor.App.Services;
using Capacitor.App.ViewModels;
using Capacitor.App.Views;

namespace Capacitor.App;

public partial class App : Application {
    // Linked to the app's shutdown sequence below; the token StartDaemonCommand's WAIT is
    // built against (Task 4 carry-note: never CancellationToken.None — an unbounded wait would
    // survive app exit).
    readonly CancellationTokenSource _shutdown = new();
    DaemonClientService? _service; // concrete type: IAsyncDisposable is not on the interface
    bool _shutdownStarted;
    bool _shutdownConfirmed;

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
            Console.Error.WriteLine($"kcap app failed to start: {ex}");

            // Showing a window here is legal before Avalonia's main loop starts — it's exactly
            // what StartWithClassicDesktopLifetime's own ShowMainWindow() does right after Start.
            // Calling desktop.Shutdown(1) directly, as this catch used to, is what previously
            // threw when startup faulted synchronously (before the main loop began) — so this
            // shape resolves that pre-main-loop edge case rather than worsening it.
            var errorWindow = BuildStartupErrorWindow(ex);
            if (desktop.MainWindow is null) desktop.MainWindow = errorWindow;
            errorWindow.Closed += (_, _) => desktop.Shutdown(1);
            errorWindow.Show();
        }
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
        var window = new MainWindow { DataContext = new MainWindowViewModel(service, shutdownToken) };
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
        if (_service is not null) await _service.DisposeAsync();
        _shutdownConfirmed = true;

        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop) desktop.TryShutdown();
    }
}
