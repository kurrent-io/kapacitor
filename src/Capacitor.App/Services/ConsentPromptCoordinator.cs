using Avalonia.Threading;
using Capacitor.App.Views;

namespace Capacitor.App.Services;

/// Owns the single consent prompt window (spec §6): at most one instance at a time; closing
/// releases it (an explicit defer — the queue is untouched) and a later raise re-creates it.
/// The service knows nothing about windows — THIS class filters the unconditional EntryAdded
/// signal by visibility and marshals to the UI thread, because the signal originates on a socket
/// continuation. Additions while the window is already visible never re-activate it: no focus
/// stealing mid-interaction.
public sealed class ConsentPromptCoordinator : IDisposable {
    readonly Func<ConsentPromptWindow> _windowFactory;
    readonly IDisposable _subscription;
    ConsentPromptWindow? _window;

    public ConsentPromptCoordinator(IConsentService consent, Func<ConsentPromptWindow> windowFactory) {
        _windowFactory = windowFactory;
        _subscription  = consent.EntryAdded.Subscribe(_ => Dispatcher.UIThread.Post(RaiseIfHidden));
    }

    /// Every open-or-activate pass, including the one the tray menu drives — the "no re-activation
    /// while visible" rule is only assertable if the raise itself is countable.
    internal int Raises { get; private set; }

    /// The tray menu's "Review pending launches…" target, and the coordinator's own raise path.
    /// Must run on the UI thread.
    public void ShowPromptWindow() {
        Raises++;
        if (_window is null) {
            var window = _windowFactory();
            // Only the identity check keeps a late event from a superseded window from clearing a
            // newer one (MainWindowCoordinator's same guard).
            window.Closed += (_, _) => {
                if (ReferenceEquals(_window, window)) _window = null;
            };
            _window = window;
        }

        _window.Show();
        _window.Activate();
    }

    public void Dispose() {
        _subscription.Dispose();
        _window?.Close();
        _window = null;
    }

    void RaiseIfHidden() {
        if (_window is not { IsVisible: true }) ShowPromptWindow();
    }
}
