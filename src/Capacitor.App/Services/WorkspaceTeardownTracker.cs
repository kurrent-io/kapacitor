namespace Capacitor.App.Services;

/// App-lifetime registry of asynchronous workspace teardowns — the piece the synchronous
/// disposal pass cannot express. Sealed at shutdown drain.
public sealed class WorkspaceTeardownTracker(TimeProvider time, Action<string, Exception>? diagnostics = null) {
    static readonly TimeSpan DrainBound = TimeSpan.FromSeconds(5);

    readonly Lock _lock = new();
    readonly Lock _diagnosticsLock = new();
    readonly List<Task> _pending = [];
    bool _sealed;
    Task? _drain;

    /// Registers and starts observing a teardown. Post-seal: the teardown is executed and
    /// observed immediately rather than refused — belt-and-braces, a path that slips past the
    /// shutdown latch must still not hold a socket. Pre- and post-seal run through the exact
    /// same observed task, so there is one execution path, never two.
    public void Track(Func<Task> teardown) {
        var task = ObserveAsync(teardown);
        lock (_lock) {
            if (!_sealed) _pending.Add(task);
        }
    }

    async Task ObserveAsync(Func<Task> teardown) {
        try {
            await teardown().ConfigureAwait(false);
        } catch (Exception ex) {
            Report("workspace teardown", ex);
        }
    }

    // Losing/faulting teardowns land here exactly once; a throwing sink is contained so it
    // can never poison the drain or a sibling teardown.
    void Report(string context, Exception ex) {
        if (diagnostics is null) return;
        lock (_diagnosticsLock) {
            try { diagnostics(context, ex); } catch { /* contained by contract */ }
        }
    }

    /// Seals atomically (no registration races past the snapshot), awaits all pending
    /// teardowns bounded by 5 seconds total, then returns. Idempotent.
    public Task DrainAsync() {
        lock (_lock) {
            if (_drain is not null) return _drain;
            _sealed = true;
            var snapshot = _pending.ToArray();
            return _drain = WaitBoundedAsync(snapshot);
        }
    }

    // Every teardown already consumes and logs its own fault inside ObserveAsync, so
    // Task.WhenAll here never faults — this only ever races the drain bound. A straggler left
    // running past the bound keeps the same observer, so its eventual fault (or success) is
    // still consumed exactly once, just later.
    async Task WaitBoundedAsync(Task[] snapshot) =>
        await Task.WhenAny(Task.WhenAll(snapshot), Task.Delay(DrainBound, time)).ConfigureAwait(false);
}
