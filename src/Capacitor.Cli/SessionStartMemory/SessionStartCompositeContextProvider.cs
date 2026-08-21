using System.Text.Json;

namespace Capacitor.Cli.SessionStartMemory;

/// <summary>
/// The eight non-Claude harnesses' SessionStart context provider: one
/// combined fragment carrying both the team-memory index and judge-fact
/// guidelines. Resolves the repo/machine scope ONCE and drives the memory lane
/// (<see cref="SessionStartMemoryContextProvider.FetchWithScopeAsync"/>) and the
/// guidelines lane (<see cref="SessionStartGuidelinesLane.FetchWithScopeAsync"/>)
/// in parallel under one budget, then composes a single string that rides the
/// harness's existing memory delivery seam unchanged.
///
/// <para>Disposition is computed over ENABLED lanes only: any content ⇒ commit;
/// all empty ⇒ complete-without-context; no content and ≥1 retryable failure ⇒
/// retry (with the max of the lanes' Retry-After hints). A disabled lane
/// contributes nothing and never blocks commit.</para>
/// </summary>
internal sealed class SessionStartCompositeContextProvider(
    ISessionStartMemoryScopeResolver scopeResolver,
    SessionStartMemoryContextProvider memory,
    SessionStartGuidelinesLane guidelines,
    Action<string>? diagnostic = null) : ISessionStartContextProvider {

    public async Task<SessionStartMemoryContextResult> GetAsync(SessionStartMemoryContextRequest request) {
        var memoryEnabled     = !request.Disabled;
        var guidelinesEnabled = !request.GuidelinesDisabled;
        if (!memoryEnabled && !guidelinesEnabled) return SessionStartMemoryContextResult.Empty;
        if (request.Budget <= TimeSpan.Zero) return SessionStartMemoryContextResult.Retry;

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(request.CancellationToken);
        cts.CancelAfter(request.Budget);

        SessionStartMemoryScope scope;
        try {
            scope = await scopeResolver.ResolveAsync(request.Cwd, request.Budget, cts.Token);
        } catch (Exception ex) when (IsFailOpen(ex)) {
            diagnostic?.Invoke($"SessionStart scope resolution skipped: {ex.Message}");
            return SessionStartMemoryContextResult.Retry;
        }

        // Start both enabled lanes before awaiting, so they run in parallel under the shared budget.
        var memoryTask = memoryEnabled
            ? RunLaneAsync(() => memory.FetchWithScopeAsync(scope, request, cts.Token))
            : null;
        var guidelinesTask = guidelinesEnabled
            ? RunLaneAsync(() => guidelines.FetchWithScopeAsync(scope, request, cts.Token))
            : null;

        var memoryResult     = memoryTask is null ? null : await memoryTask;
        var guidelinesResult = guidelinesTask is null ? null : await guidelinesTask;

        return Combine(memoryResult, guidelinesResult);
    }

    async Task<SessionStartMemoryContextResult> RunLaneAsync(Func<Task<SessionStartMemoryContextResult>> lane) {
        try {
            return await lane();
        } catch (Exception ex) when (IsFailOpen(ex)) {
            diagnostic?.Invoke($"SessionStart context lane skipped: {ex.Message}");
            return SessionStartMemoryContextResult.Retry;
        }
    }

    static SessionStartMemoryContextResult Combine(
            SessionStartMemoryContextResult? memoryResult, SessionStartMemoryContextResult? guidelinesResult) {
        var memoryFragment = memoryResult is { Disposition: SessionStartMemoryDisposition.Ready, Fragment: { } mf } ? mf : null;
        var guidelinesFragment = guidelinesResult is { Disposition: SessionStartMemoryDisposition.Ready, Fragment: { } gf } ? gf : null;

        if (memoryFragment is not null || guidelinesFragment is not null)
            return new SessionStartMemoryContextResult(
                SessionStartMemoryDisposition.Ready, Compose(memoryFragment, guidelinesFragment));

        // No content. If every enabled lane was empty (not failed), the session genuinely has nothing
        // to inject → complete the lease. Otherwise ≥1 lane failed retryably → hold for a later attempt.
        var anyRetry = memoryResult?.Disposition == SessionStartMemoryDisposition.RetryableFailure
                    || guidelinesResult?.Disposition == SessionStartMemoryDisposition.RetryableFailure;
        if (!anyRetry) return SessionStartMemoryContextResult.Empty;

        return new SessionStartMemoryContextResult(
            SessionStartMemoryDisposition.RetryableFailure, RetryAfter: MaxRetryAfter(memoryResult, guidelinesResult));
    }

    /// <summary>
    /// One fragment, marker-first. The memory fragment already opens with the
    /// shared <c>kcap-memory-index</c> marker; the guidelines fragment is
    /// marker-less, so in the guidelines-only case the marker is prepended here
    /// — Pi/OpenCode capture stdout only when it OPENS with that marker.
    /// </summary>
    static string Compose(string? memoryFragment, string? guidelinesFragment) {
        if (memoryFragment is not null && guidelinesFragment is not null)
            return memoryFragment + "\n\n" + guidelinesFragment;
        if (memoryFragment is not null) return memoryFragment;
        return MemoryIndexEmitter.FragmentMarker + "\n" + guidelinesFragment;
    }

    static TimeSpan? MaxRetryAfter(SessionStartMemoryContextResult? a, SessionStartMemoryContextResult? b) {
        var x = a?.RetryAfter;
        var y = b?.RetryAfter;
        if (x is null) return y;
        if (y is null) return x;
        return x.Value >= y.Value ? x : y;
    }

    static bool IsFailOpen(Exception ex) =>
        ex is HttpRequestException or IOException or JsonException or
              OperationCanceledException or UnauthorizedAccessException or InvalidDataException;
}
