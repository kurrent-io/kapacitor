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
    static readonly string ConfigPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        ".config", "kapacitor", "config.json"
    );

    public static string? ResolvedServerUrl { get; private set; }

    public static string? ResolveServerUrl(string[] args) {
        // 1. CLI arg: --server-url <url>
        var idx = Array.IndexOf(args, "--server-url");
        if (idx >= 0 && idx + 1 < args.Length) {
            ResolvedServerUrl = args[idx + 1];
            return ResolvedServerUrl;
        }

        // 2. Env var
        var envUrl = Environment.GetEnvironmentVariable("KAPACITOR_URL");
        if (!string.IsNullOrEmpty(envUrl)) {
            ResolvedServerUrl = envUrl;
            return ResolvedServerUrl;
        }

        // 3. Config file
        var config = Load();
        if (!string.IsNullOrEmpty(config?.ServerUrl)) {
            ResolvedServerUrl = config.ServerUrl;
            return ResolvedServerUrl;
        }

        // 4. No default
        ResolvedServerUrl = null;
        return null;
    }

    public static KapacitorConfig? Load() {
        if (!File.Exists(ConfigPath))
            return null;

        try {
            var json = File.ReadAllText(ConfigPath);
            return JsonSerializer.Deserialize(json, ConfigJsonContext.Default.KapacitorConfig);
        } catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException) {
            Console.Error.WriteLine($"Warning: could not read config at {ConfigPath}: {ex.Message}");
            return null;
        }
    }

    public static void Save(KapacitorConfig config) {
        var dir = Path.GetDirectoryName(ConfigPath)!;
        Directory.CreateDirectory(dir);
        var tempPath = ConfigPath + ".tmp";
        File.WriteAllBytes(
            tempPath,
            JsonSerializer.SerializeToUtf8Bytes(config, ConfigJsonContextIndented.Default.KapacitorConfig)
        );
        File.Move(tempPath, ConfigPath, overwrite: true);
    }

    public static string GetConfigPath() => ConfigPath;
}
