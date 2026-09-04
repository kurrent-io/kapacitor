using Capacitor.App.Services;
using Capacitor.Cli.Core.WorkItems;

namespace Capacitor.App.Tests.Unit;

/// Scripted IWorkContextSource: reads answer from a queue, or park on a gate so a test can settle
/// them in a chosen order. Every read records the id it was asked for.
sealed class FakeWorkContextSource : IWorkContextSource {
    readonly Queue<WorkContextRead> _scripted = new();
    readonly Queue<TaskCompletionSource<WorkContextRead>> _gates = new();

    public readonly List<string> Requested = [];
    public WorkContextRead Default = WorkContextRead.Of(WorkContextReadKind.SessionUnknown);
    public int InFlight;

    public void Enqueue(params WorkContextRead[] reads) {
        foreach (var read in reads) _scripted.Enqueue(read);
    }

    /// The next read awaits the returned source instead of answering from the queue.
    public TaskCompletionSource<WorkContextRead> Gate() {
        var gate = new TaskCompletionSource<WorkContextRead>(TaskCreationOptions.RunContinuationsAsynchronously);
        _gates.Enqueue(gate);
        return gate;
    }

    public async Task<WorkContextRead> ReadAsync(string sessionId, CancellationToken ct) {
        Requested.Add(sessionId);
        Interlocked.Increment(ref InFlight);
        try {
            if (_gates.Count > 0) return await _gates.Dequeue().Task.WaitAsync(ct);
            await Task.Yield();
            return _scripted.Count > 0 ? _scripted.Dequeue() : Default;
        } finally {
            Interlocked.Decrement(ref InFlight);
        }
    }
}
