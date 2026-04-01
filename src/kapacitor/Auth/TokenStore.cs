using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace kapacitor.Auth;

public record StoredTokens {
    [JsonPropertyName("access_token")]
    public required string AccessToken { get; init; }

    [JsonPropertyName("refresh_token")]
    public string? RefreshToken { get; init; }

    [JsonPropertyName("expires_at")]
    public required DateTimeOffset ExpiresAt { get; init; }

    [JsonPropertyName("github_username")]
    public required string GitHubUsername { get; init; }

    [JsonPropertyName("provider")]
    public string Provider { get; init; } = "Auth0";

    [JsonPropertyName("auth0_domain")]
    public string? Auth0Domain { get; init; }

    [JsonPropertyName("client_id")]
    public string? ClientId { get; init; }

    public bool IsExpired => DateTimeOffset.UtcNow >= ExpiresAt - TimeSpan.FromSeconds(30);
}

public static class TokenStore {
    static readonly string TokenPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        ".config",
        "kapacitor",
        "tokens.json"
    );

    public static StoredTokens? Load() {
        if (!File.Exists(TokenPath)) {
            return null;
        }

        var json = File.ReadAllText(TokenPath);

        return JsonSerializer.Deserialize(json, KapacitorJsonContext.Default.StoredTokens);
    }

    public static void Save(StoredTokens tokens) {
        var dir = Path.GetDirectoryName(TokenPath)!;
        Directory.CreateDirectory(dir);
        var tempPath = TokenPath + ".tmp";
        File.WriteAllText(tempPath, JsonSerializer.Serialize(tokens, KapacitorJsonContext.Default.StoredTokens));
        File.Move(tempPath, TokenPath, overwrite: true);

        if (!OperatingSystem.IsWindows()) {
            File.SetUnixFileMode(TokenPath, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }
    }

    public static void Delete() {
        if (File.Exists(TokenPath)) {
            File.Delete(TokenPath);
        }
    }

    public static async Task<StoredTokens?> GetValidTokensAsync() {
        var tokens = Load();

        if (tokens is null) {
            return null;
        }

        if (!tokens.IsExpired) {
            return tokens;
        }

        // Auth0: use refresh token
        if (tokens is { Provider: "Auth0", RefreshToken: not null, Auth0Domain: not null, ClientId: not null }) {
            return await RefreshAuth0Async(tokens);
        }

        // GitHub: refresh via server's /auth/refresh endpoint
        if (tokens.Provider == "GitHub") {
            return await RefreshGitHubAsync(tokens);
        }

        return null;
    }

    static async Task<StoredTokens?> RefreshGitHubAsync(StoredTokens tokens) {
        var baseUrl = Environment.GetEnvironmentVariable("KAPACITOR_URL") ?? "http://localhost:5108";
        using var http = new HttpClient();

        var requestBody = JsonSerializer.Serialize(
            new RefreshTokenRequest { AccessToken = tokens.AccessToken },
            KapacitorJsonContext.Default.RefreshTokenRequest
        );
        var payload = new StringContent(requestBody, System.Text.Encoding.UTF8, "application/json");

        try {
            var response = await http.PostAsync($"{baseUrl}/auth/refresh", payload);

            if (!response.IsSuccessStatusCode) {
                return null;
            }

            var json = await response.Content.ReadFromJsonAsync(KapacitorJsonContext.Default.TokenExchangeResponse);

            if (json is null) {
                return null;
            }

            var refreshed = tokens with {
                AccessToken = json.AccessToken,
                ExpiresAt   = DateTimeOffset.UtcNow.AddSeconds(json.ExpiresIn)
            };

            Save(refreshed);

            return refreshed;
        } catch {
            return null;
        }
    }

    static async Task<StoredTokens?> RefreshAuth0Async(StoredTokens tokens) {
        using var http = new HttpClient();

        var response = await http.PostAsync(
            $"https://{tokens.Auth0Domain}/oauth/token",
            new FormUrlEncodedContent(
                new Dictionary<string, string> {
                    ["grant_type"]    = "refresh_token",
                    ["client_id"]     = tokens.ClientId!,
                    ["refresh_token"] = tokens.RefreshToken!
                }
            )
        );

        if (!response.IsSuccessStatusCode) {
            return null;
        }

        var json = (await response.Content.ReadFromJsonAsync(KapacitorJsonContext.Default.Auth0TokenResponse))!;

        var refreshed = tokens with {
            AccessToken = json.AccessToken,
            ExpiresAt = DateTimeOffset.UtcNow.AddSeconds(json.ExpiresIn),
            RefreshToken = json.RefreshToken ?? tokens.RefreshToken
        };

        Save(refreshed);

        return refreshed;
    }
}
