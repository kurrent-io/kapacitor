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

    public IObservable<string> Messages => _messages.AsObservable();

    public void Notify(string message) {
        Console.Error.WriteLine($"kcap: {message}");
        _messages.OnNext(message);
    }
}
