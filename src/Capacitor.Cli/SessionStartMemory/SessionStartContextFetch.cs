using System.Net;

namespace Capacitor.Cli.SessionStartMemory;

/// <summary>
/// The transport outcome of one SessionStart context GET. <see cref="Body"/> is
/// the bounded response bytes on a 2xx (empty array for 204), and null on any
/// non-success status. Status mapping is deliberately left to the caller: the
/// memory lane and the guidelines lane treat 404 differently (the guidelines visibility-race rule), so
/// this helper carries the raw status through rather than pre-deciding.
/// </summary>
internal readonly record struct SessionStartFetchOutcome(HttpStatusCode Status, byte[]? Body, TimeSpan? RetryAfter);

/// <summary>
/// Shared HTTP mechanics for the SessionStart context lanes: authenticated GET
/// with a single 401-refresh retry (adopting a peer process's refreshed token
/// rather than rotating a second time), a 256 KiB bounded body read on success,
/// and <c>Retry-After</c> parsing. Extracted from
/// <see cref="SessionStartMemoryContextProvider"/> so the memory and guidelines
/// lanes share one implementation. Behaviour is byte-for-byte what the
/// memory lane did before the extraction.
/// </summary>
internal static class SessionStartContextFetch {
    public static async Task<SessionStartFetchOutcome> FetchAsync(
            Func<string?, CancellationToken, Task<HttpClient>> clientFactory,
            string url,
            bool disposeClients,
            CancellationToken ct) {
        HttpClient? firstClient   = null;
        HttpClient? refreshClient = null;
        try {
            firstClient  = await clientFactory(null, ct);
            var response = await SendAsync(firstClient, url, ct);
            if (response.StatusCode == HttpStatusCode.Unauthorized) {
                response.Dispose();
                // Retry against the token this client actually sent, so a peer process that
                // refreshed in the meantime is adopted rather than rotated a second time. A
                // null here (no token was attached) simply means there is nothing to force —
                // the retry still picks up whatever is stored now.
                var rejected  = firstClient.DefaultRequestHeaders.Authorization?.Parameter;
                refreshClient = await clientFactory(rejected, ct);
                response      = await SendAsync(refreshClient, url, ct);
            }
            using (response) {
                var retryAfter = ParseRetryAfter(response);
                if (!response.IsSuccessStatusCode)
                    return new SessionStartFetchOutcome(response.StatusCode, Body: null, retryAfter);
                var bytes = await ReadBoundedAsync(response.Content, ct);
                return new SessionStartFetchOutcome(response.StatusCode, bytes, RetryAfter: null);
            }
        } finally {
            if (disposeClients) {
                firstClient?.Dispose();
                if (!ReferenceEquals(firstClient, refreshClient)) refreshClient?.Dispose();
            }
        }
    }

    static Task<HttpResponseMessage> SendAsync(HttpClient client, string url, CancellationToken ct) =>
        client.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct);

    static async Task<byte[]> ReadBoundedAsync(HttpContent content, CancellationToken ct) {
        await using var stream = await content.ReadAsStreamAsync(ct);
        var buffer = new byte[SessionStartMemoryConstants.MaxResponseBytes + 1];
        var total  = 0;
        while (total < buffer.Length) {
            var read = await stream.ReadAsync(buffer.AsMemory(total, buffer.Length - total), ct);
            if (read == 0) break;
            total += read;
        }
        if (total > SessionStartMemoryConstants.MaxResponseBytes)
            throw new InvalidDataException("SessionStart context response exceeded 256 KiB.");
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
