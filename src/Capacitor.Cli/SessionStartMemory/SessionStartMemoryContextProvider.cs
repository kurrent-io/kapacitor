using System.Net;
using System.Text.Json;

namespace Capacitor.Cli.SessionStartMemory;

internal sealed class SessionStartMemoryContextProvider(
    ISessionStartMemoryScopeResolver scopeResolver,
    Func<bool, CancellationToken, Task<HttpClient>> clientFactory,
    Action<string>? diagnostic = null,
    bool disposeClients = false) {

    public async Task<SessionStartMemoryContextResult> GetAsync(SessionStartMemoryContextRequest request) {
        if (request.Disabled) return SessionStartMemoryContextResult.Empty;
        if (request.Budget <= TimeSpan.Zero) return SessionStartMemoryContextResult.Retry;
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(request.CancellationToken);
        cts.CancelAfter(request.Budget);
        try {
            var scope = await scopeResolver.ResolveAsync(request.Cwd, request.Budget, cts.Token);
            HttpClient? firstClient = null;
            HttpClient? refreshClient = null;
            try {
                firstClient = await clientFactory(false, cts.Token);
                var response = await SendAsync(firstClient, request.BaseUrl, scope, cts.Token);
                if (response.StatusCode == HttpStatusCode.Unauthorized) {
                    response.Dispose();
                    refreshClient = await clientFactory(true, cts.Token);
                    response = await SendAsync(refreshClient, request.BaseUrl, scope, cts.Token);
                }
                using (response) {
                if (response.StatusCode == HttpStatusCode.NoContent) return SessionStartMemoryContextResult.Empty;
                if (response.StatusCode is HttpStatusCode.BadRequest or HttpStatusCode.NotFound) {
                    diagnostic?.Invoke($"SessionStart memory endpoint contract mismatch: HTTP {(int)response.StatusCode}.");
                    return SessionStartMemoryContextResult.Empty;
                }
                if (!response.IsSuccessStatusCode)
                    return new SessionStartMemoryContextResult(SessionStartMemoryDisposition.RetryableFailure,
                        RetryAfter: ParseRetryAfter(response));

                var bytes = await ReadBoundedAsync(response.Content, cts.Token);
                var entries = JsonSerializer.Deserialize(bytes,
                    SessionStartMemoryJsonContext.Default.SessionStartMemoryEntryArray);
                if (entries is null) return SessionStartMemoryContextResult.Retry;
                if (entries.Length == 0) return SessionStartMemoryContextResult.Empty;
                var fragment = MemoryIndexEmitter.BuildFragment(entries);
                return fragment is null
                    ? SessionStartMemoryContextResult.Empty
                    : new SessionStartMemoryContextResult(SessionStartMemoryDisposition.Ready, fragment);
                }
            } finally {
                if (disposeClients) {
                    firstClient?.Dispose();
                    if (!ReferenceEquals(firstClient, refreshClient)) refreshClient?.Dispose();
                }
            }
        } catch (Exception ex) when (ex is HttpRequestException or IOException or JsonException or
                                     OperationCanceledException or UnauthorizedAccessException or InvalidDataException) {
            diagnostic?.Invoke($"SessionStart memory fetch skipped: {ex.Message}");
            return SessionStartMemoryContextResult.Retry;
        }
    }

    static Task<HttpResponseMessage> SendAsync(HttpClient client, string baseUrl, SessionStartMemoryScope scope,
        CancellationToken ct) => client.GetAsync(BuildUrl(baseUrl, scope), HttpCompletionOption.ResponseHeadersRead, ct);

    internal static string BuildUrl(string baseUrl, SessionStartMemoryScope scope) {
        var query = new List<string>();
        if (scope.RepoHash is not null) query.Add("repo=" + Uri.EscapeDataString(scope.RepoHash));
        if (scope.MachineTag is not null) query.Add("machine=" + Uri.EscapeDataString(scope.MachineTag));
        return baseUrl.TrimEnd('/') + "/api/memories/index" + (query.Count == 0 ? "" : "?" + string.Join('&', query));
    }

    static async Task<byte[]> ReadBoundedAsync(HttpContent content, CancellationToken ct) {
        await using var stream = await content.ReadAsStreamAsync(ct);
        var buffer = new byte[SessionStartMemoryConstants.MaxResponseBytes + 1];
        var total = 0;
        while (total < buffer.Length) {
            var read = await stream.ReadAsync(buffer.AsMemory(total, buffer.Length - total), ct);
            if (read == 0) break;
            total += read;
        }
        if (total > SessionStartMemoryConstants.MaxResponseBytes)
            throw new InvalidDataException("Memory index response exceeded 256 KiB.");
        return buffer.AsSpan(0, total).ToArray();
    }

    static TimeSpan? ParseRetryAfter(HttpResponseMessage response) {
        if (response.StatusCode != HttpStatusCode.TooManyRequests || response.Headers.RetryAfter is null) return null;
        if (response.Headers.RetryAfter.Delta is { } delta) return delta;
        if (response.Headers.RetryAfter.Date is { } date) {
            var value = date - DateTimeOffset.UtcNow;
            return value > TimeSpan.Zero ? value : null;
        }
        return null;
    }
}
