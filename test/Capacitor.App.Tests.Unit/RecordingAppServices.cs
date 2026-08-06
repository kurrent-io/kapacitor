using System.Reactive.Subjects;
using Capacitor.App.Services;

namespace Capacitor.App.Tests.Unit;

/// Recording IAppNotifier — every Notify call is appended to Notified (assertable order) and
/// also pushed through Messages, mirroring AppNotifier's real shape. Shared by
/// AgentActionServiceTests and TrayViewModelTests.
sealed class RecordingNotifier : IAppNotifier {
    readonly Subject<string> _messages = new();
    public IObservable<string> Messages => _messages;
    public readonly List<string> Notified = [];
    public void Notify(string message) {
        Notified.Add(message);
        _messages.OnNext(message);
    }
}

/// Recording IUrlOpener — records every URL passed to Open, optionally throwing to exercise the
/// opener-exception banner path.
sealed class RecordingOpener : IUrlOpener {
    public readonly List<string> Opened = [];
    public Exception? ThrowOnOpen;
    public void Open(string url) {
        Opened.Add(url);
        if (ThrowOnOpen is not null) throw ThrowOnOpen;
    }
}
