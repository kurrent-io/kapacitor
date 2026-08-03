namespace Capacitor.Cli.Daemon.Services;

/// <summary>
/// Monotonic change generation behind DaemonStatus pushes. Version and the shared rearm
/// source live under ONE lock: Pulse() increments and completes-and-rearms in the same
/// critical section; WaitBeyondAsync reads Version and captures the current source in that
/// same critical section — no torn interleaving between the check and the capture. This is
/// a broadcast: N subscribers each hold their own cursor (the `seen` they pass in) and can
/// never consume each other's signal. Call sites must mutate state FIRST and Pulse() second,
/// or a subscriber could snapshot old state at the new version and then wait forever.
/// </summary>
internal sealed class DaemonStatusNotifier {
    readonly Lock _lock = new();
    long _version;
    // RunContinuationsAsynchronously: completed under _lock — a waiter's continuation must
    // not run inline while the lock is held.
    TaskCompletionSource _next = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public long Version {
        get { lock (_lock) return _version; }
    }

    public void Pulse() {
        lock (_lock) {
            _version++;
            var done = _next;
            _next = new(TaskCreationOptions.RunContinuationsAsynchronously);
            done.TrySetResult();
        }
    }

    public Task WaitBeyondAsync(long seen, CancellationToken ct) {
        TaskCompletionSource wait;
        lock (_lock) {
            if (_version > seen) return Task.CompletedTask;
            wait = _next;
        }
        // A timeout/cancellation here only stops THIS waiter's wait — the shared source is
        // never completed or replaced by a consumer.
        return wait.Task.WaitAsync(ct);
    }
}
