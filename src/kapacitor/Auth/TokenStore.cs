using System.Text.Json;
using System.Text.Json.Serialization;

namespace kapacitor.Auth;

public record StoredTokens {
    [JsonPropertyName("access_token")]
    public required string AccessToken { get; init; }

    [JsonPropertyName("refresh_token")]
    public required string RefreshToken { get; init; }

    [JsonPropertyName("expires_at")]
    public required DateTimeOffset ExpiresAt { get; init; }

    [JsonPropertyName("github_username")]
    public required string GitHubUsername { get; init; }

    [JsonPropertyName("auth0_domain")]
    public required string Auth0Domain { get; init; }

    [JsonPropertyName("client_id")]
    public required string ClientId { get; init; }

    public bool IsExpired => DateTimeOffset.UtcNow >= ExpiresAt - TimeSpan.FromSeconds(30);
}

public static class TokenStore {
    static readonly string TokenPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        ".config", "kapacitor", "tokens.json"
    );

    public static StoredTokens? Load() {
        if (!File.Exists(TokenPath)) return null;
        var json = File.ReadAllText(TokenPath);
        return JsonSerializer.Deserialize<StoredTokens>(json);
    }

    public static void Save(StoredTokens tokens) {
        var dir = Path.GetDirectoryName(TokenPath)!;
        Directory.CreateDirectory(dir);
        var tempPath = TokenPath + ".tmp";
        File.WriteAllText(tempPath, JsonSerializer.Serialize(tokens, new JsonSerializerOptions { WriteIndented = true }));
        File.Move(tempPath, TokenPath, overwrite: true);

        // Set file permissions to 0600 on Unix
        if (!OperatingSystem.IsWindows()) {
            File.SetUnixFileMode(TokenPath, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }
    }

    public static void Delete() {
        if (File.Exists(TokenPath)) File.Delete(TokenPath);
    }

    public static async Task<StoredTokens?> GetValidTokensAsync() {
        var tokens = Load();
        if (tokens is null) return null;
        if (!tokens.IsExpired) return tokens;
        return await RefreshAsync(tokens);
    }

    static async Task<StoredTokens?> RefreshAsync(StoredTokens tokens) {
        using var http = new HttpClient();
        var response = await http.PostAsync(
            $"https://{tokens.Auth0Domain}/oauth/token",
            new FormUrlEncodedContent(new Dictionary<string, string> {
                ["grant_type"] = "refresh_token",
                ["client_id"] = tokens.ClientId,
                ["refresh_token"] = tokens.RefreshToken
            })
        );

        if (!response.IsSuccessStatusCode) return null;

        var responseBody = await response.Content.ReadAsStringAsync();
        var json = JsonSerializer.Deserialize<JsonElement>(responseBody);
        var refreshed = tokens with {
            AccessToken = json.GetProperty("access_token").GetString()!,
            ExpiresAt = DateTimeOffset.UtcNow.AddSeconds(json.GetProperty("expires_in").GetInt32()),
            RefreshToken = json.TryGetProperty("refresh_token", out var rt) ? rt.GetString()! : tokens.RefreshToken
        };

        Save(refreshed);
        return refreshed;
    }
}
