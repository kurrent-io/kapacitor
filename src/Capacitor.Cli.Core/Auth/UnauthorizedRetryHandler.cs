using System.Net;
using System.Net.Http.Headers;

namespace Capacitor.Cli.Core.Auth;

/// <summary>
/// Recovers from a single 401 by force-refreshing the token and resending once.
///
/// This exists because a token can be locally unexpired yet already rejected by the server
/// (clock skew, a server-side invalidation, an early re-issue). Without it, every interactive
/// command turns that into a hard failure telling the user to re-run `kcap login` when a refresh
/// would have sufficed.
///
/// Only ONE component may own 401-retry on a given client, or a single rejection multiplies into
/// several refreshes; this handler is installed exclusively by
/// <c>HttpClientExtensions.CreateAuthenticatedClientAsync</c>, and call sites that used to run
/// their own retry loop on top of that client have had theirs removed.
/// </summary>
internal sealed class UnauthorizedRetryHandler(StoredTokens initial) : DelegatingHandler {
    // Swapped as a whole reference, never mutated in place, so concurrent requests either see the
    // old token or the new one. Volatile because requests on a long-lived client run on many
    // threads and a refresh on one must become visible to the rest.
    StoredTokens _current = initial;

    protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken) {
        // Apply the handler's own token rather than trusting the client's default header: after a
        // refresh, that default still carries the token the server already rejected.
        var applied = Volatile.Read(ref _current);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", applied.AccessToken);

        var response = await base.SendAsync(request, cancellationToken);

        if (response.StatusCode != HttpStatusCode.Unauthorized) return response;
        if (!CanResend(request)) return response;

        // `applied` — not a re-read of _current — is what this request actually sent. A peer
        // request may have refreshed in the meantime, and attributing the rejection to its fresh
        // token would rotate a credential that was never rejected.
        var refreshed = await TokenStore.ForceRefreshAsync(applied.AccessToken, cancellationToken);

        if (refreshed is null) return response; // Nothing better to try — surface the original 401.

        Volatile.Write(ref _current, refreshed);
        response.Dispose();

        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", refreshed.AccessToken);

        // base.SendAsync, not a recursive call into this method: exactly one extra attempt, so a
        // second 401 reaches the caller instead of looping.
        return await base.SendAsync(request, cancellationToken);
    }

    // Buffered content re-serializes from its byte array on every send; a stream-backed body is
    // consumed by the first attempt and cannot be replayed. StringContent and FormUrlEncodedContent
    // both derive from ByteArrayContent, so this covers every body the CLI sends.
    static bool CanResend(HttpRequestMessage request) => request.Content is null or ByteArrayContent;
}
