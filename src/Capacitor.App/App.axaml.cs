using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
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

    async Task StartAsync(IClassicDesktopStyleApplicationLifetime desktop) {
        var service = await DaemonClientService.CreateDefaultAsync();
        service.Start();
        _service = service;
        desktop.MainWindow = new MainWindow { DataContext = new MainWindowViewModel(service, _shutdown.Token) };
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
