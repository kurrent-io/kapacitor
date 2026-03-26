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

        // Only Auth0 supports token refresh
        if (tokens.Provider == "Auth0" && tokens.RefreshToken is not null && tokens.Auth0Domain is not null && tokens.ClientId is not null) {
            return await RefreshAsync(tokens);
        }

        // GitHub tokens can't be refreshed
        return null;
    }

    static async Task<StoredTokens?> RefreshAsync(StoredTokens tokens) {
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
