namespace Capacitor.Cli.SessionStartMemory;

internal sealed class SessionStartMemoryOrchestrator(
    SessionStartMemoryLeaseStore store,
    ISessionStartContextProvider provider,
    Action<string>? diagnostic = null) {

    /// <param name="commitGate">
    /// Optional last check, awaited AFTER a successful fetch and BEFORE the once-per-session lease is
    /// committed: false releases the lease for retry instead of spending it.
    ///
    /// <para>Exists because a caller can only discover that its fragment is undeliverable after the
    /// fetch has already run. A Copilot hook whose lifecycle POST permanently fails must exit non-zero,
    /// and Copilot consumes hook stdout only on a zero exit — so committing the lease there would burn
    /// the session's single injection on output the host discards, and no later resume would retry.
    /// Gating the COMMIT rather than the fetch keeps the fetch overlapped with the POST.</para>
    ///
    /// <para>Callers that pass this MUST resolve it on every path before awaiting the returned task,
    /// or the task cannot complete. Null (the default) preserves the original behaviour exactly, which
    /// is what the Claude, Cursor and Codex adapters rely on.</para>
    /// </param>
    public async Task<string?> GetFragmentAsync(SessionMemoryLifecycle lifecycle,
        SessionStartMemoryContextRequest request,
        Func<CancellationToken, Task<bool>>? commitGate = null) {
        // Both lanes disabled ⇒ no lease is spent, so a flag flipped on mid-session can still
        // inject on a later callback (the disabled-lane disposition rule). A single disabled lane does NOT short-circuit
        // here — the composite provider runs the enabled lane and contributes its content.
        if (request.Disabled && request.GuidelinesDisabled) return null;

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
            // Undeliverable-after-fetch: release the lease rather than spend it, so the next start of
            // this session retries instead of being permanently denied its one injection.
            if (commitGate is not null && !await commitGate(request.CancellationToken)) {
                await store.RetryAsync(lease, retryAfter: null, Remaining(), request.CancellationToken);
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
