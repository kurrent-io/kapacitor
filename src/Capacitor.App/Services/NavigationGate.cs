namespace Capacitor.App.Services;

/// App-lifetime navigation state, owned by the composition root and shared by every
/// MainWindowViewModel the coordinator builds — including one built BETWEEN the two shutdown
/// passes, which is exactly why the latch cannot live on a single window's ViewModel (spec §3).
///
/// Generation is the launch auto-open's staleness token: a launch captures it before the call, and
/// a success arriving at a different one opens nothing. Every navigation, every close-to-hide and
/// the shutdown latch bump it, so an unchanged generation is the only thing that can mean "the user
/// has not navigated since".
public sealed class NavigationGate {
    readonly Lock _lock = new();
    int _generation;
    bool _latched;

    public int Generation {
        get { lock (_lock) return _generation; }
    }

    /// Once true, never false again: shutdown has begun, so no new workspace — and therefore no new
    /// attach — may be created, in this window or any later one.
    public bool ShutdownLatched {
        get { lock (_lock) return _latched; }
    }

    public int Bump() {
        lock (_lock) return ++_generation;
    }

    /// Latching also bumps: a launch already in flight when shutdown began is then stale on its own
    /// terms, not only because the latch happens to reject it.
    public void Latch() {
        lock (_lock) {
            _latched = true;
            _generation++;
        }
    }
}
