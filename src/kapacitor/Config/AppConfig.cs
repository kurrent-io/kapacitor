using System.Text.Json;
using System.Text.Json.Serialization;

namespace kapacitor.Config;

public record KapacitorConfig {
    [JsonPropertyName("server_url")]
    public string? ServerUrl { get; init; }

    [JsonPropertyName("daemon")]
    public DaemonSettings? Daemon { get; init; }

    [JsonPropertyName("update_check")]
    public bool UpdateCheck { get; init; } = true;

    [JsonPropertyName("default_visibility")]
    public string DefaultVisibility { get; init; } = "org_public";

    [JsonPropertyName("excluded_repos")]
    public string[] ExcludedRepos { get; init; } = [];
}

public record DaemonSettings {
    [JsonPropertyName("name")]
    public string? Name { get; init; }

    [JsonPropertyName("max_agents")]
    public int MaxAgents { get; init; } = 5;
}

[JsonSerializable(typeof(KapacitorConfig))]
internal partial class ConfigJsonContext : JsonSerializerContext;

[JsonSerializable(typeof(KapacitorConfig))]
[JsonSourceGenerationOptions(WriteIndented = true)]
internal partial class ConfigJsonContextIndented : JsonSerializerContext;

public static class AppConfig {
    static readonly string ConfigPath = PathHelpers.ConfigPath("config.json");

    public static string? ResolvedServerUrl { get; private set; }

    public static ResolvedProfile? ResolvedProfile { get; private set; }

    public static async Task<string?> ResolveServerUrl(string[] args) {
        var idx = Array.IndexOf(args, "--server-url");
        var cliServerUrl = (idx >= 0 && idx + 1 < args.Length) ? args[idx + 1] : null;

        var envUrl = Environment.GetEnvironmentVariable("KAPACITOR_URL");
        var envProfile = Environment.GetEnvironmentVariable("KAPACITOR_PROFILE");

        var config = await LoadProfileConfig();

        RepoConfig? repoConfig = null;
        var repoConfigPath = Path.Combine(Environment.CurrentDirectory, ".kapacitor.json");
        if (File.Exists(repoConfigPath)) {
            try {
                var json = await File.ReadAllTextAsync(repoConfigPath);
                repoConfig = JsonSerializer.Deserialize(json, RepoConfigJsonContext.Default.RepoConfig);
            } catch { /* ignore malformed */ }
        }

        var remoteUrls = GetGitRemoteUrls();

        var resolver = new ProfileResolver(
            config, cliServerUrl, envUrl, envProfile,
            repoConfig, remoteUrls, Environment.CurrentDirectory
        );

        var resolved = resolver.Resolve();
        ResolvedProfile = resolved;
        ResolvedServerUrl = resolved.ServerUrl;

        if (resolved.Warning is not null) {
            await Console.Error.WriteLineAsync($"Warning: {resolved.Warning}");
        }

        return resolved.ServerUrl;
    }

    static string[] GetGitRemoteUrls() {
        try {
            var psi = new System.Diagnostics.ProcessStartInfo("git", "remote -v") {
                RedirectStandardOutput = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            using var proc = System.Diagnostics.Process.Start(psi);
            if (proc is null) return [];

            var output = proc.StandardOutput.ReadToEnd();
            proc.WaitForExit();

            return output.Split('\n', StringSplitOptions.RemoveEmptyEntries)
                .Select(line => line.Split('\t', ' ').ElementAtOrDefault(1))
                .Where(url => url is not null)
                .Distinct()
                .ToArray()!;
        } catch {
            return [];
        }
    }

    /// <summary>
    /// Removes trailing slashes from a URL to prevent double-slash issues
    /// when appending paths (e.g., <c>https://example.com/</c> + <c>/auth/config</c>).
    /// </summary>
    public static string NormalizeUrl(string url) => url.TrimEnd('/');

    static readonly string[] ValidVisibilities = ["private", "org_public", "public"];

    public static async Task<KapacitorConfig?> Load() {
        if (!File.Exists(ConfigPath))
            return null;

        try {
            var json   = await File.ReadAllTextAsync(ConfigPath);
            var config = JsonSerializer.Deserialize(json, ConfigJsonContext.Default.KapacitorConfig);

            if (config is null) return null;

            // Normalize default_visibility to lowercase; reset invalid values to default
            var vis = (config.DefaultVisibility ?? "org_public").ToLowerInvariant();

            if (!ValidVisibilities.Contains(vis)) vis = "org_public";

            return vis == config.DefaultVisibility ? config : config with { DefaultVisibility = vis };
        } catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException) {
            await Console.Error.WriteLineAsync($"Warning: could not read config at {ConfigPath}: {ex.Message}");

            return null;
        }
    }

    public static async Task Save(KapacitorConfig config) {
        var dir = Path.GetDirectoryName(ConfigPath)!;
        Directory.CreateDirectory(dir);
        var tempPath = $"{ConfigPath}.tmp";

        await File.WriteAllBytesAsync(
            tempPath,
            JsonSerializer.SerializeToUtf8Bytes(config, ConfigJsonContextIndented.Default.KapacitorConfig)
        );
        File.Move(tempPath, ConfigPath, overwrite: true);
    }

    public static async Task<ProfileConfig> LoadProfileConfig() {
        if (!File.Exists(ConfigPath))
            return new ProfileConfig { Profiles = new Dictionary<string, Profile> { ["default"] = new Profile() } };

        try {
            var json   = await File.ReadAllTextAsync(ConfigPath);
            var result = ConfigMigration.MigrateIfNeeded(json);

            if (result.WasMigrated) {
                await SaveProfileConfig(result.Config);
            }

            return result.Config;
        } catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException) {
            await Console.Error.WriteLineAsync($"Warning: could not read config at {ConfigPath}: {ex.Message}");
            return new ProfileConfig { Profiles = new Dictionary<string, Profile> { ["default"] = new Profile() } };
        }
    }

    public static async Task SaveProfileConfig(ProfileConfig config) {
        var dir      = Path.GetDirectoryName(ConfigPath)!;
        Directory.CreateDirectory(dir);
        var tempPath = $"{ConfigPath}.tmp";

        await File.WriteAllBytesAsync(
            tempPath,
            JsonSerializer.SerializeToUtf8Bytes(config, ProfileConfigJsonContextIndented.Default.ProfileConfig)
        );
        File.Move(tempPath, ConfigPath, overwrite: true);
    }

    public static string GetConfigPath() => ConfigPath;
}
