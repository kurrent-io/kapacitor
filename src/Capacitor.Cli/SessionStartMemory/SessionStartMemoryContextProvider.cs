using System.Net;
using System.Text.Json;

namespace Capacitor.Cli.SessionStartMemory;

internal sealed class SessionStartMemoryContextProvider(
    ISessionStartMemoryScopeResolver scopeResolver,
    Func<string?, CancellationToken, Task<HttpClient>> clientFactory,
    Action<string>? diagnostic = null,
    bool disposeClients = false) : ISessionStartContextProvider {

    public async Task<SessionStartMemoryContextResult> GetAsync(SessionStartMemoryContextRequest request) {
        if (request.Disabled) return SessionStartMemoryContextResult.Empty;
        if (request.Budget <= TimeSpan.Zero) return SessionStartMemoryContextResult.Retry;
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(request.CancellationToken);
        cts.CancelAfter(request.Budget);
        try {
            var scope = await scopeResolver.ResolveAsync(request.Cwd, request.Budget, cts.Token);
            return await FetchWithScopeAsync(scope, request, cts.Token);
        } catch (Exception ex) when (ex is HttpRequestException or IOException or JsonException or
                                     OperationCanceledException or UnauthorizedAccessException or InvalidDataException) {
            diagnostic?.Invoke($"SessionStart memory fetch skipped: {ex.Message}");
            return SessionStartMemoryContextResult.Retry;
        }
    }

    /// <summary>
    /// Fetches and renders the memory-index fragment for an already-resolved
    /// <paramref name="scope"/>. Separated from <see cref="GetAsync"/> so
    /// <see cref="SessionStartCompositeContextProvider"/> can resolve the scope
    /// ONCE and drive both lanes with it, instead of walking the repo twice
    /// (matters under Cursor's tight budget). The caller owns the
    /// budget-linked <paramref name="ct"/> and the fail-open exception handling.
    /// </summary>
    public async Task<SessionStartMemoryContextResult> FetchWithScopeAsync(
            SessionStartMemoryScope scope, SessionStartMemoryContextRequest request, CancellationToken ct) {
        var outcome = await SessionStartContextFetch.FetchAsync(
            clientFactory, BuildUrl(request.BaseUrl, scope), disposeClients, ct);

        if (outcome.Status == HttpStatusCode.NoContent) return SessionStartMemoryContextResult.Empty;
        if (outcome.Status is HttpStatusCode.BadRequest or HttpStatusCode.NotFound) {
            diagnostic?.Invoke($"SessionStart memory endpoint contract mismatch: HTTP {(int)outcome.Status}.");
            return SessionStartMemoryContextResult.Empty;
        }
        if (outcome.Body is null)
            return new SessionStartMemoryContextResult(SessionStartMemoryDisposition.RetryableFailure, RetryAfter: outcome.RetryAfter);

        var entries = JsonSerializer.Deserialize(outcome.Body,
            SessionStartMemoryJsonContext.Default.SessionStartMemoryEntryArray);
        if (entries is null) return SessionStartMemoryContextResult.Retry;
        if (entries.Length == 0) return SessionStartMemoryContextResult.Empty;
        var fragment = MemoryIndexEmitter.BuildFragment(entries);
        return fragment is null
            ? SessionStartMemoryContextResult.Empty
            : new SessionStartMemoryContextResult(SessionStartMemoryDisposition.Ready, fragment);
    }

    internal static string BuildUrl(string baseUrl, SessionStartMemoryScope scope) {
        var query = new List<string>();
        if (scope.RepoHash is not null) query.Add("repo=" + Uri.EscapeDataString(scope.RepoHash));
        if (scope.MachineTag is not null) query.Add("machine=" + Uri.EscapeDataString(scope.MachineTag));
        return baseUrl.TrimEnd('/') + "/api/memories/index" + (query.Count == 0 ? "" : "?" + string.Join('&', query));
    }
}
