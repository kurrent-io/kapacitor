namespace Capacitor.Cli.Core.Auth;

/// <summary>
/// A machine credential read from the environment: the public client id and its secret.
/// </summary>
public sealed record MachineCredential(string ClientId, string ClientSecret);

/// <summary>
/// Reads the machine credential a headless runner carries, and resolves where to exchange it.
///
/// <para>A runner has no profile and no token store — it is a fresh container with two environment
/// variables. Everything else it needs it already has: the server comes from <c>KCAP_URL</c>, and
/// provider discovery over <c>/auth/config</c> needs no credential.</para>
/// </summary>
public static class MachineAuth {
    public const string ClientIdVar     = "KCAP_CLIENT_ID";
    public const string ClientSecretVar = "KCAP_CLIENT_SECRET";

    /// <summary>
    /// WorkOS AuthKit's OAuth2 token endpoint.
    ///
    /// <para>Hardcoded, with an env override, for the same reason
    /// <see cref="AuthProxyEndpoint.DefaultUrl"/> is: it is one value for the whole fleet, since every
    /// tenant shares a single WorkOS environment and application. It deliberately is NOT derived from
    /// the tenant's <c>/auth/config</c>, because the field that would carry it — <c>authkit_domain</c> —
    /// is blank on every tenant, so deriving it would produce a broken URL on all of them.</para>
    ///
    /// <para>Measured, not assumed: this host answers <c>grant_type=client_credentials</c> with an
    /// OAuth2 credential rejection for bad credentials. <c>api.workos.com/oauth2/token</c> 404s.</para>
    /// </summary>
    public const string DefaultTokenUrl = "https://signin.kcap.ai/oauth2/token";

    /// <summary>KCAP_WORKOS_TOKEN_URL is an internal dev/test override; not documented for end users.</summary>
    public static string TokenUrl =>
        (Environment.GetEnvironmentVariable("KCAP_WORKOS_TOKEN_URL") ?? DefaultTokenUrl).Trim();

    /// <summary>
    /// True when EITHER variable is present — i.e. someone intended machine auth. Deliberately not
    /// "both", so a half-configured runner is diagnosed rather than silently falling back to a token
    /// store it does not have and being told to run <c>kcap login</c>, which it cannot do.
    /// </summary>
    public static bool Intended =>
        !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(ClientIdVar))
        || !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(ClientSecretVar));

    /// <summary>
    /// Reads both halves. Returns null with a <paramref name="problem"/> naming the missing variable
    /// when only one is set.
    /// </summary>
    public static MachineCredential? TryRead(out string? problem) {
        var id     = Environment.GetEnvironmentVariable(ClientIdVar);
        var secret = Environment.GetEnvironmentVariable(ClientSecretVar);

        var haveId     = !string.IsNullOrWhiteSpace(id);
        var haveSecret = !string.IsNullOrWhiteSpace(secret);

        if (haveId && haveSecret) {
            problem = null;

            return new(id!.Trim(), secret!.Trim());
        }

        problem = (haveId, haveSecret) switch {
            (true, false) => $"{ClientIdVar} is set but {ClientSecretVar} is not — a machine needs both.",
            (false, true) => $"{ClientSecretVar} is set but {ClientIdVar} is not — a machine needs both.",
            _             => null
        };

        return null;
    }
}
