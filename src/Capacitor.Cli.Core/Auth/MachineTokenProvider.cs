using System.Net.Http.Json;
using System.Text.Json.Serialization;

namespace Capacitor.Cli.Core.Auth;

/// <summary>WorkOS <c>client_credentials</c> token response.</summary>
public record MachineTokenResponse {
    [JsonPropertyName("access_token")]
    public string AccessToken { get; init; } = "";

    [JsonPropertyName("expires_in")]
    public int ExpiresIn { get; init; }
}

/// <summary>
/// Exchanges a machine credential for a short-lived bearer, and holds it IN MEMORY ONLY.
///
/// <para><b>Never the token store.</b> A machine token is minted from a credential the runner already
/// has, so caching it on disk buys nothing and costs the property the whole design rests on — that a
/// machine's bearer exists only for the life of the process that needs it. It also has no refresh
/// token: <c>client_credentials</c> returns an access token and nothing else, so "refresh" here means
/// "mint another", which needs only the credential.</para>
///
/// <para><b>Single-flight.</b> One process can build many clients (hooks, the watcher, MCP servers), so
/// the mint is serialised behind a semaphore and the result shared. Without it a burst of concurrent
/// callers would each mint a token — WorkOS would allow it, but it is pure waste and makes the token a
/// moving target while debugging.</para>
/// </summary>
public static class MachineTokenProvider {
    /// <summary>
    /// Re-mint this long before nominal expiry. A token that expires mid-flight surfaces as a 401 the
    /// caller must interpret; spending a few seconds of a 3600s lifetime avoids that entirely.
    /// </summary>
    internal static readonly TimeSpan RenewMargin = TimeSpan.FromSeconds(60);

    static readonly SemaphoreSlim Gate = new(1, 1);

    static string?         cachedToken;
    static DateTimeOffset  cachedExpiry;

    /// <summary>Test seam — the cache is process-wide static state.</summary>
    internal static void ResetForTesting() {
        cachedToken  = null;
        cachedExpiry = default;
    }

    /// <summary>
    /// Returns a usable bearer for <paramref name="credential"/>, minting one if the cache is empty,
    /// near expiry, or holds the token the server just rejected.
    ///
    /// <para><paramref name="rejectedToken"/> is how a 401 becomes a re-mint: the caller passes back the
    /// token the server refused, and if that is what is cached it is discarded before the check. Without
    /// this a revoked-then-reissued credential would keep serving the dead token until its clock ran
    /// out.</para>
    ///
    /// <para>Returns null with <paramref name="problem"/> set on failure. It does NOT throw: this runs on
    /// the client-construction path, whose whole contract is to report an auth outcome rather than
    /// explode — a hook that cannot authenticate must exit quietly, not stack-trace into a transcript.</para>
    /// </summary>
    public static async Task<string?> GetTokenAsync(
            MachineCredential credential,
            string?           rejectedToken,
            CancellationToken ct,
            HttpClient?       client = null
        ) {
        await Gate.WaitAsync(ct);

        try {
            if (rejectedToken is not null && string.Equals(cachedToken, rejectedToken, StringComparison.Ordinal)) {
                cachedToken  = null;
                cachedExpiry = default;
            }

            if (cachedToken is not null && DateTimeOffset.UtcNow < cachedExpiry - RenewMargin) return cachedToken;

            var (token, expiresIn, failure) = await MintAsync(credential, ct, client);

            if (token is null) {
                Problem = failure;

                return null;
            }

            cachedToken = token;

            // A server that omits or zeroes expires_in must not produce a token treated as valid
            // forever. Fall back to a short life so the next call re-mints rather than reusing
            // something whose lifetime we never learned.
            cachedExpiry = DateTimeOffset.UtcNow.AddSeconds(expiresIn > 0 ? expiresIn : 300);
            Problem      = null;

            return cachedToken;
        }
        finally {
            Gate.Release();
        }
    }

    /// <summary>Why the last <see cref="GetTokenAsync"/> failed, for the caller to surface.</summary>
    public static string? Problem { get; private set; }

    static async Task<(string? Token, int ExpiresIn, string? Failure)> MintAsync(
            MachineCredential credential, CancellationToken ct, HttpClient? provided) {
        var http  = provided ?? new HttpClient();
        var owned = provided is null;

        try {
            using var form = new FormUrlEncodedContent([
                new KeyValuePair<string, string>("grant_type", "client_credentials"),
                new KeyValuePair<string, string>("client_id", credential.ClientId),
                new KeyValuePair<string, string>("client_secret", credential.ClientSecret)
            ]);

            using var response = await http.PostAsync(MachineAuth.TokenUrl, form, ct);

            if (!response.IsSuccessStatusCode) {
                // Deliberately does NOT echo the response body. A token endpoint's error body is
                // attacker-influenced and, on some providers, reflects the request — which here contains
                // the secret. The status is the diagnostic; the body is not worth the risk.
                return (null, 0, $"the machine credential was rejected by {MachineAuth.TokenUrl} "
                              + $"(HTTP {(int)response.StatusCode}). Check {MachineAuth.ClientIdVar}/"
                              + $"{MachineAuth.ClientSecretVar}, or re-issue with 'kcap machine create'.");
            }

            var body = await response.Content.ReadFromJsonAsync(CapacitorJsonContext.Default.MachineTokenResponse, ct);

            return string.IsNullOrEmpty(body?.AccessToken)
                ? (null, 0, $"{MachineAuth.TokenUrl} returned success with no access_token.")
                : (body.AccessToken, body.ExpiresIn, null);
        }
        catch (Exception ex) when (ex is not OperationCanceledException) {
            return (null, 0, $"could not reach {MachineAuth.TokenUrl}: {ex.Message}");
        }
        finally {
            if (owned) http.Dispose();
        }
    }
}
