using System.Reactive.Linq;
using System.Reactive.Subjects;

namespace Capacitor.App.Services;

public interface IAppNotifier {
    IObservable<string> Messages { get; }
    void Notify(string message);
}

/// Replay-0: a message emitted before a subscriber attaches is lost to the UI — the accepted
/// missed-banner-while-hidden limitation (spec §11). Notify ALSO writes to Console.Error, which
/// is the only channel that survives a hidden main window (spec §11).
public sealed class AppNotifier : IAppNotifier {
    readonly Subject<string> _messages = new();

    // Producers are concurrent Task.Run bodies (AgentActionService's per-agent stops, pause ops,
    // etc.) — Rx's grammar requires OnNext calls to a single Subject be serialized (never
    // overlapping), and a bare Subject does not do this itself. ONE lock around both effects also
    // keeps the stderr line and the pushed message in the same relative order across threads.
    readonly Lock _lock = new();

    public IObservable<string> Messages => _messages.AsObservable();

    public void Notify(string message) {
        lock (_lock) {
            Console.Error.WriteLine($"kcap: {message}");
            _messages.OnNext(message);
        }
    }
}
