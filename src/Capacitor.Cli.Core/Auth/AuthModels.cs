using System.Text.Json.Serialization;

namespace Capacitor.Cli.Core.Auth;

// GET /auth/config — all fields optional since shape varies by provider
public record AuthDiscoveryResponse {
    [JsonPropertyName("provider")]
    public string Provider { get; init; } = "";

    [JsonPropertyName("authkit_domain")]
    public string? AuthKitDomain { get; init; }

    [JsonPropertyName("organization_id")]
    public string? OrganizationId { get; init; }

    [JsonPropertyName("client_id")]
    public string? ClientId { get; init; }

    [JsonPropertyName("github_client_id")]
    public string? GithubClientId { get; init; }

    [JsonPropertyName("token_exchange_url")]
    public string? TokenExchangeUrl { get; init; }

    // Server-mediated GitHub code-exchange endpoint. Required for the localhost
    // browser flow because GitHub Apps need client_secret on POST /login/oauth/access_token,
    // and the CLI can't ship a secret. When null, the CLI uses device flow.
    [JsonPropertyName("github_code_exchange_url")]
    public string? GithubCodeExchangeUrl { get; init; }
}

// POST {github_code_exchange_url} — CLI → server. Server adds client_id + client_secret
// and forwards to https://github.com/login/oauth/access_token.
public record GitHubCodeExchangeRequest {
    [JsonPropertyName("code")]
    public required string Code { get; init; }

    [JsonPropertyName("code_verifier")]
    public required string CodeVerifier { get; init; }

    [JsonPropertyName("redirect_uri")]
    public required string RedirectUri { get; init; }
}

// POST /auth/token request
public record TokenExchangeRequest {
    [JsonPropertyName("github_access_token")]
    public string? GithubAccessToken { get; init; }
}

// POST /auth/token response
public record TokenExchangeResponse {
    [JsonPropertyName("access_token")]
    public string AccessToken { get; init; } = "";

    [JsonPropertyName("expires_in")]
    public int ExpiresIn { get; init; }

    [JsonPropertyName("username")]
    public string Username { get; init; } = "";
}

// POST /auth/token error body (any failing status)
public record AuthErrorResponse {
    [JsonPropertyName("error")]
    public string? Error { get; init; }
}

// RFC 8628 device authorization response. Shared: GitHub and WorkOS return the same shape.
public record DeviceCodeResponse {
    [JsonPropertyName("device_code")]
    public string DeviceCode { get; init; } = "";

    [JsonPropertyName("user_code")]
    public string UserCode { get; init; } = "";

    [JsonPropertyName("verification_uri")]
    public string VerificationUri { get; init; } = "";

    /// <summary>RFC 8628 §3.3.1 — the same page with the code pre-filled. Open this when a browser is
    /// available; print <see cref="VerificationUri"/> for the code being typed on another device.</summary>
    [JsonPropertyName("verification_uri_complete")]
    public string? VerificationUriComplete { get; init; }

    /// <summary>What to open on a best-effort browser launch, falling back when the server omits it.</summary>
    public string BrowserUri =>
        string.IsNullOrEmpty(VerificationUriComplete) ? VerificationUri : VerificationUriComplete;

    // Both default to 0 when the server omits them: a property initializer does NOT survive
    // source-generated deserialization. Read them through the accessors below, never directly.
    [JsonPropertyName("interval")]
    public int Interval { get; init; }

    [JsonPropertyName("expires_in")]
    public int ExpiresIn { get; init; }

    /// <summary>RFC 8628 §3.5's default when the server omits it.</summary>
    public int IntervalOrDefault => Interval > 0 ? Interval : 5;

    /// <summary>Bounds the poll loop. §3.2 makes it REQUIRED, but an absent one must not mean forever.</summary>
    public int ExpiresInOrDefault => ExpiresIn > 0 ? ExpiresIn : 900;
}

// GitHub Device Flow: POST https://github.com/login/oauth/access_token
public record GitHubTokenResponse {
    [JsonPropertyName("access_token")]
    public string? AccessToken { get; init; }

    [JsonPropertyName("error")]
    public string? Error { get; init; }
}

// WorkOS: POST /user_management/authenticate (PKCE — no client secret)
public record WorkOSAuthResponse {
    [JsonPropertyName("user")]
    public WorkOSUserInfo? User { get; init; }

    [JsonPropertyName("organization_id")]
    public string? OrganizationId { get; init; }

    [JsonPropertyName("access_token")]
    public string AccessToken { get; init; } = "";

    [JsonPropertyName("refresh_token")]
    public string? RefreshToken { get; init; }

    /// <summary>Only the device grant polls this endpoint, so only it ever sees an error body here
    /// (<c>authorization_pending</c>, <c>slow_down</c>, <c>access_denied</c>, <c>expired_token</c>).</summary>
    [JsonPropertyName("error")]
    public string? Error { get; init; }

    // Deliberately does not model authkit_authorization_code: the device grant returns one, it is
    // exchangeable for tokens by another application, and nothing here needs it. Not modelling it is
    // what keeps it out of logs and transcripts.
}

public record WorkOSUserInfo {
    [JsonPropertyName("id")]
    public string Id { get; init; } = "";

    [JsonPropertyName("email")]
    public string? Email { get; init; }

    [JsonPropertyName("first_name")]
    public string? FirstName { get; init; }

    [JsonPropertyName("last_name")]
    public string? LastName { get; init; }
}

// POST /auth/refresh request
public record RefreshTokenRequest {
    [JsonPropertyName("access_token")]
    public required string AccessToken { get; init; }
}

// Auth proxy: GET /config
public sealed record ProxyConfigResponse {
    [JsonPropertyName("github_client_id")]         public string  GitHubClientId        { get; init; } = "";

    // Proxy-mediated GitHub code-exchange endpoint. When null, --discover falls back
    // to device flow because the CLI can't talk to GitHub's token endpoint directly
    // for a GitHub App without client_secret.
    [JsonPropertyName("github_code_exchange_url")] public string? GitHubCodeExchangeUrl { get; init; }

    // WorkOS discovery: the shared AuthKit Application's public client id + (optional) custom
    // AuthKit UI domain. Present (and client id non-empty) when the proxy serves WorkOS discovery.
    [JsonPropertyName("workos_client_id")]      public string? WorkOSClientId      { get; init; }
    [JsonPropertyName("workos_authkit_domain")] public string? WorkOSAuthKitDomain { get; init; }
}

// Auth proxy: POST /discover-tenants (GitHub) and /discover-tenants-workos (WorkOS) response item.
public sealed record DiscoveredTenant {
    [JsonPropertyName("org_id")]    public long   OrgId    { get; init; }
    [JsonPropertyName("org_login")] public string OrgLogin { get; init; } = "";
    [JsonPropertyName("origin")]    public string Origin   { get; init; } = "";

    // Provider-aware fields (additive; GitHub rows leave them defaulted).
    [JsonPropertyName("provider")]        public string  Provider       { get; init; } = AuthProvider.GitHubApp;
    [JsonPropertyName("organization_id")] public string? OrganizationId { get; init; }
    [JsonPropertyName("slug")]            public string? Slug           { get; init; }
    [JsonPropertyName("display_name")]    public string? DisplayName    { get; init; }

    // Profile key + picker label, provider-aware. WorkOS rows have no OrgLogin.
    // Compare provider case-insensitively: the proxy's /discover-tenants-workos emits "WorkOS"
    // while AuthProvider.WorkOS is "workos" (the tenant /auth/config casing).
    [JsonIgnore] public bool   IsWorkOS    => string.Equals(Provider, AuthProvider.WorkOS, StringComparison.OrdinalIgnoreCase);
    [JsonIgnore] public string ProfileName => IsWorkOS ? (Slug ?? OrganizationId ?? "tenant") : OrgLogin;
    [JsonIgnore] public string Label       => !string.IsNullOrEmpty(DisplayName) ? DisplayName : OrgLogin;
}

public enum DiscoveryError {
    None,
    ProxyUnreachable,
    TokenRejected,
    UpstreamError
}

public sealed record DiscoveryResult(DiscoveredTenant[] Tenants, DiscoveryError Error);
