using Capacitor.App.Views;

namespace Capacitor.App.Services;

/// Owns the app's single main window across hide-to-tray cycles (spec §9). Closing the window
/// hides it — the app keeps living in the tray — and the tray's "Open Kurrent Capacitor" re-shows
/// that same instance. A window is only ever really closed during a quit, and a later
/// ShowMainWindow then builds a fresh one from the factory: Avalonia refuses to Show() a closed
/// window, and nothing is lost because no state lives in the view (everything displayed is
/// projected from the live service).
public sealed class MainWindowCoordinator(Func<MainWindow> windowFactory) {
    MainWindow? _window;

    /// Set by App.OnShutdownRequested's FIRST (deferring) pass, so the second pass's real window
    /// teardown is never cancelled by the interception below.
    public bool QuitInProgress { get; set; }

    /// The tracked window — live (visible or hidden) from the first ShowMainWindow until a real
    /// close. Null outside that window; App reads it once to assign desktop.MainWindow.
    public MainWindow? Window => _window;

    public void ShowMainWindow() {
        if (_window is null) {
            var window = windowFactory(); // the factory shows it (App.BuildAndShowMainWindow)
            window.CloseInterceptor = OnWindowClosing;
            // Only a REAL close reaches this (an intercepted one is cancelled before Closed) —
            // the identity check keeps a late event from a superseded window from clearing a
            // newer one.
            window.Closed += (_, _) => {
                if (ReferenceEquals(_window, window)) _window = null;
            };
            _window = window;
        }

        _window.Show();    // no-op when already visible
        _window.Activate(); // bring it forward when it was merely behind other windows
    }

    /// Called from MainWindow.Closing. True → the close must be cancelled; the window is hidden
    /// here instead.
    public bool OnWindowClosing() {
        if (QuitInProgress) return false;
        _window?.Hide();
        return true;
    }
}
