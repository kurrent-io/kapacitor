namespace Capacitor.Cli.SessionStartMemory;

internal sealed class SessionStartMemoryOrchestrator(
    SessionStartMemoryLeaseStore store,
    SessionStartMemoryContextProvider provider,
    Action<string>? diagnostic = null) {

    public async Task<string?> GetFragmentAsync(SessionMemoryLifecycle lifecycle,
        SessionStartMemoryContextRequest request) {
        if (request.Disabled) return null;

        var started = System.Diagnostics.Stopwatch.GetTimestamp();
        TimeSpan Remaining() {
            var value = request.Budget - System.Diagnostics.Stopwatch.GetElapsedTime(started);
            return value > TimeSpan.Zero ? value : TimeSpan.Zero;
        }

        try {
            var decision = SessionStartMemoryLifecyclePolicy.Decide(lifecycle);
            if (decision is SessionMemoryLifecycleDecision.IneligibleNoCommit or SessionMemoryLifecycleDecision.RetryLaterNoCommit)
                return null;
            var key = SessionStartMemoryIdentity.Create(lifecycle.Harness, lifecycle.SessionId,
                lifecycle.LifecycleInstanceId);
            var lease = await store.TryBeginAsync(key, Remaining(), request.CancellationToken);
            if (lease is null) return null;

            var result = await provider.GetAsync(request with { Budget = Remaining() });
            if (result.Disposition == SessionStartMemoryDisposition.RetryableFailure) {
                await store.RetryAsync(lease, result.RetryAfter, Remaining(), request.CancellationToken);
                return null;
            }
            if (!await store.CompleteAsync(lease, result.Disposition, Remaining(), request.CancellationToken)) return null;
            return result.Disposition == SessionStartMemoryDisposition.Ready ? result.Fragment : null;
        } catch (Exception ex) when (ex is not OutOfMemoryException and not StackOverflowException) {
            diagnostic?.Invoke($"SessionStart memory orchestration skipped: {ex.Message}");
            return null;
        }
    }
}
