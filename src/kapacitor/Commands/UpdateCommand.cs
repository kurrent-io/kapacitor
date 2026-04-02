using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using kapacitor.Config;

namespace kapacitor.Commands;

public static class UpdateCommand {
    static readonly string CachePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        ".config", "kapacitor", "update-check.json"
    );

    public static async Task<int> HandleAsync() {
        var (latest, current) = await CheckForUpdateAsync(forceCheck: true);

        if (latest is null) {
            Console.Error.WriteLine("Could not check for updates.");
            return 1;
        }

        if (latest == current) {
            Console.WriteLine($"Already up to date: {current}");
            return 0;
        }

        Console.WriteLine($"Update available: {current} → {latest}");
        Console.WriteLine();
        Console.WriteLine("Run:");
        Console.WriteLine("  npm update -g @kurrent/kapacitor");

        return 0;
    }

    /// <summary>
    /// Print an update hint to stderr if a newer version is available.
    /// Called on every CLI invocation (cached, max once per 24h).
    /// </summary>
    public static async Task PrintUpdateHintIfAvailable() {
        var config = AppConfig.Load();
        if (config?.UpdateCheck == false) return;

        try {
            var (latest, current) = await CheckForUpdateAsync(forceCheck: false);
            if (latest is not null && current is not null && latest != current) {
                Console.Error.WriteLine();
                Console.Error.WriteLine($"Update available: {current} → {latest}");
                Console.Error.WriteLine("Run `npm update -g @kurrent/kapacitor` to update");
            }
        } catch {
            // Best effort — never break the CLI for update checks
        }
    }

    static async Task<(string? latest, string? current)> CheckForUpdateAsync(bool forceCheck) {
        var current = GetCurrentVersion();

        if (!forceCheck) {
            // Check cache
            if (File.Exists(CachePath)) {
                try {
                    var cacheJson = File.ReadAllText(CachePath);
                    var cache = JsonNode.Parse(cacheJson);
                    var checkedAt = cache?["checked_at"]?.GetValue<DateTimeOffset>();
                    var cachedVersion = cache?["latest_version"]?.GetValue<string>();

                    if (checkedAt is not null
                        && DateTimeOffset.UtcNow - checkedAt.Value < TimeSpan.FromHours(24)
                        && cachedVersion is not null) {
                        return (cachedVersion, current);
                    }
                } catch {
                    // Corrupted cache — re-check
                }
            }
        }

        // Query npm registry
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
        http.DefaultRequestHeaders.Add("User-Agent", "kapacitor-cli");

        try {
            var resp = await http.GetAsync("https://registry.npmjs.org/@kurrent/kapacitor/latest");
            if (!resp.IsSuccessStatusCode) return (null, current);

            var body = await resp.Content.ReadAsStringAsync();
            var json = JsonNode.Parse(body);
            var latest = json?["version"]?.GetValue<string>();

            // Cache result
            if (latest is not null) {
                var dir = Path.GetDirectoryName(CachePath)!;
                Directory.CreateDirectory(dir);
                var cacheObj = new JsonObject {
                    ["latest_version"] = latest,
                    ["checked_at"] = DateTimeOffset.UtcNow
                };
                var tempPath = CachePath + ".tmp";
                File.WriteAllText(tempPath, cacheObj.ToJsonString());
                File.Move(tempPath, CachePath, overwrite: true);
            }

            return (latest, current);
        } catch {
            return (null, current);
        }
    }

    static string? GetCurrentVersion() {
        return typeof(UpdateCommand).Assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?
            .InformationalVersion;
    }
}
