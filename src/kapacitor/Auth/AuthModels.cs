using System.Text.Json.Serialization;

namespace kapacitor.Auth;

// GET /auth/config — all fields optional since shape varies by provider
public record AuthDiscoveryResponse {
    [JsonPropertyName("provider")]
    public string Provider { get; init; } = "";

    [JsonPropertyName("auth0_domain")]
    public string? Auth0Domain { get; init; }

    [JsonPropertyName("client_id")]
    public string? ClientId { get; init; }

    [JsonPropertyName("audience")]
    public string? Audience { get; init; }

    [JsonPropertyName("github_client_id")]
    public string? GithubClientId { get; init; }

    [JsonPropertyName("token_exchange_url")]
    public string? TokenExchangeUrl { get; init; }
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

// GitHub Device Flow: POST https://github.com/login/device/code
public record GitHubDeviceCodeResponse {
    [JsonPropertyName("device_code")]
    public string DeviceCode { get; init; } = "";

    [JsonPropertyName("user_code")]
    public string UserCode { get; init; } = "";

    [JsonPropertyName("verification_uri")]
    public string VerificationUri { get; init; } = "";

    [JsonPropertyName("interval")]
    public int Interval { get; init; } = 5;
}

// GitHub Device Flow: POST https://github.com/login/oauth/access_token
public record GitHubTokenResponse {
    [JsonPropertyName("access_token")]
    public string? AccessToken { get; init; }

    [JsonPropertyName("error")]
    public string? Error { get; init; }
}

// Auth0: POST /oauth/token
public record Auth0TokenResponse {
    [JsonPropertyName("access_token")]
    public string AccessToken { get; init; } = "";

    [JsonPropertyName("refresh_token")]
    public string? RefreshToken { get; init; }

    [JsonPropertyName("expires_in")]
    public int ExpiresIn { get; init; }

    [JsonPropertyName("id_token")]
    public string? IdToken { get; init; }
}

// Auth0 ID token JWT payload (decoded from id_token)
public record Auth0IdTokenClaims {
    [JsonPropertyName("nickname")]
    public string? Nickname { get; init; }
}
