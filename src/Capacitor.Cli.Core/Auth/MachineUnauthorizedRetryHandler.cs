using System.Net;
using System.Net.Http.Headers;

namespace Capacitor.Cli.Core.Auth;

/// <summary>
/// Recovers from a single 401 on a machine-credentialed client by re-minting the token and resending
/// once — the machine-auth counterpart of <see cref="UnauthorizedRetryHandler"/>.
///
/// <para>Review (Qodo) caught that without this, machine auth had NO automatic 401 recovery. The
/// token-store path installs <c>UnauthorizedRetryHandler</c>; the machine path installed nothing, so a
/// token revoked mid-life — still unexpired by the local clock, so the proactive
/// <see cref="MachineTokenProvider.RenewMargin"/> re-mint never fires — produced repeated 401s until
/// the cache aged out. Re-minting needs only the credential (<c>client_credentials</c> has no refresh
/// token), so the caller need not, and mostly does not, thread <c>rejectedAccessToken</c> back:
/// <c>CreateAuthenticatedClientAsync</c>, the common path, does not. This closes that on the client
/// itself.</para>
///
/// <para>Only ONE component may own 401-retry on a client, or one rejection multiplies into several
/// mints. This is installed exclusively by the machine branch in
/// <c>HttpClientExtensions.CreateClientCoreAsync</c>, which for that reason attaches no other retry
/// handler.</para>
/// </summary>
internal sealed class MachineUnauthorizedRetryHandler(MachineCredential credential, string initialToken) : DelegatingHandler {
    // Swapped whole, never mutated in place, so a concurrent request sees either token — mirroring
    // UnauthorizedRetryHandler. Requests on a long-lived client run on many threads.
    string _current = initialToken;

    protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken) {
        var applied = Volatile.Read(ref _current);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", applied);

        var response = await base.SendAsync(request, cancellationToken);

        if (response.StatusCode != HttpStatusCode.Unauthorized) return response;
        if (!CanResend(request)) return response;

        // Pass `applied` — the token THIS request sent — as rejected, not a re-read of _current: a peer
        // may have re-minted already, and attributing the 401 to its fresh token would discard a
        // credential that was never refused. GetTokenAsync discards the cache only if `applied` is what
        // it still holds, so a concurrent re-mint is preserved.
        var minted = await MachineTokenProvider.GetTokenAsync(credential, rejectedToken: applied, cancellationToken);

        if (minted.Token is null) return response; // Nothing better to try — surface the original 401.

        Volatile.Write(ref _current, minted.Token);
        response.Dispose();

        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", minted.Token);

        // base.SendAsync, not recursion: exactly one extra attempt, so a second 401 reaches the caller.
        return await base.SendAsync(request, cancellationToken);
    }

    // Same replay rule as UnauthorizedRetryHandler: only bodies that re-serialize can be resent.
    static bool CanResend(HttpRequestMessage request) =>
        request.Content is null or ByteArrayContent or System.Net.Http.Json.JsonContent;
}
