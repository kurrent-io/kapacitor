using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using kapacitor.Config;

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
    static string LegacyTokenPath => PathHelpers.ConfigPath("tokens.json");
    static string TokenDir         => PathHelpers.ConfigPath("tokens");
    static string ProfileTokenPath(string profile) => Path.Combine(TokenDir, $"{profile}.json");

    // ── Profile-aware overloads ──────────────────────────────────────────────

    public static async Task<StoredTokens?> LoadAsync(string profile) {
        var path = ProfileTokenPath(profile);
        if (!File.Exists(path)) return null;
        var json = await File.ReadAllTextAsync(path);
        return JsonSerializer.Deserialize(json, KapacitorJsonContext.Default.StoredTokens);
    }

    public static async Task SaveAsync(string profile, StoredTokens tokens) {
        Directory.CreateDirectory(TokenDir);
        var path     = ProfileTokenPath(profile);
        var tempPath = $"{path}.tmp";
        await File.WriteAllTextAsync(tempPath, JsonSerializer.Serialize(tokens, KapacitorJsonContext.Default.StoredTokens));
        File.Move(tempPath, path, overwrite: true);

        if (!OperatingSystem.IsWindows()) {
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }

        // Migration: remove the pre-upgrade single-file token if it still exists
        if (File.Exists(LegacyTokenPath)) {
            try { File.Delete(LegacyTokenPath); } catch { /* best-effort */ }
        }
    }

    public static void Delete(string profile) {
        var path = ProfileTokenPath(profile);
        if (File.Exists(path)) File.Delete(path);
    }

    // ── Legacy (profile-resolving) overloads ────────────────────────────────

    public static async Task<StoredTokens?> LoadAsync() {
        var profile    = await ResolveActiveProfileAsync();
        var perProfile = await LoadAsync(profile);
        if (perProfile is not null) return perProfile;

        // Fall back to legacy single-file layout for pre-upgrade installs
        if (!File.Exists(LegacyTokenPath)) return null;
        var json = await File.ReadAllTextAsync(LegacyTokenPath);
        return JsonSerializer.Deserialize(json, KapacitorJsonContext.Default.StoredTokens);
    }

    public static async Task SaveAsync(StoredTokens tokens) {
        var profile = await ResolveActiveProfileAsync();
        await SaveAsync(profile, tokens);
    }

    public static async Task DeleteAsync() {
        if (File.Exists(LegacyTokenPath)) File.Delete(LegacyTokenPath);
        var profile = await ResolveActiveProfileAsync();
        Delete(profile);
    }

    static async Task<string> ResolveActiveProfileAsync() {
        var cfg = await AppConfig.LoadProfileConfig();
        return string.IsNullOrEmpty(cfg.ActiveProfile) ? "default" : cfg.ActiveProfile;
    }

    public static async Task<StoredTokens?> GetValidTokensAsync() {
        var tokens = await LoadAsync();

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
        if (tokens.Provider is "GitHubApp") {
            return await RefreshGitHubAsync(tokens);
        }

        return null;
    }

    static async Task<StoredTokens?> RefreshGitHubAsync(StoredTokens tokens) {
        var       baseUrl = AppConfig.ResolvedServerUrl ?? Environment.GetEnvironmentVariable("KAPACITOR_URL") ?? "http://localhost:5108";
        using var http    = new HttpClient();

        var requestBody = JsonSerializer.Serialize(
            new() { AccessToken = tokens.AccessToken },
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
                ExpiresAt = DateTimeOffset.UtcNow.AddSeconds(json.ExpiresIn)
            };

            await SaveAsync(refreshed);

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

        await SaveAsync(refreshed);

        return refreshed;
    }
}
