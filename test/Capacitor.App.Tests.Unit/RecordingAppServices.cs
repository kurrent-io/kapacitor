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

/// AgentActionService's confirm-then-force seam (decision 5), gated by a per-call
/// TaskCompletionSource — mirrors ScriptedLocalControlOps's queue idiom so a test arms the NEXT
/// call's answer (or holds it open) before triggering it. Every invocation's label is recorded,
/// which is also how "confirm seam never invoked" is asserted for a non-protected stop.
sealed class RecordingConfirmer {
    readonly Queue<TaskCompletionSource<bool>> _confirms = new();
    public readonly List<string> Prompted = [];

    public TaskCompletionSource<bool> Arm() {
        var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        _confirms.Enqueue(tcs);
        return tcs;
    }

    public void Queue(bool result) => Arm().SetResult(result);

    public Func<string, Task<bool>> Confirm => label => {
        Prompted.Add(label);
        if (_confirms.Count == 0) throw new InvalidOperationException("RecordingConfirmer: unscripted confirm call");
        return _confirms.Dequeue().Task;
    };
}

/// Default confirmForceStop for suites that never exercise a protected kind (every dto/entry
/// they build uses kind "agent") — any invocation is therefore a bug, so it throws loudly rather
/// than silently answering, doubling as a passive "confirm seam never invoked" assertion across
/// every test that uses it.
static class NeverConfirm {
    public static Func<string, Task<bool>> Confirm => _ =>
        throw new InvalidOperationException("confirmForceStop invoked unexpectedly — kind should have been \"agent\"");
}
