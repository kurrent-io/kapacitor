using System.Reflection;
using System.Text.Json.Nodes;
using kapacitor.Config;
// ReSharper disable MethodHasAsyncOverload

namespace kapacitor.Commands;

public static class UpdateCommand {
    static readonly string CachePath = PathHelpers.ConfigPath("update-check.json");

    public static async Task<int> HandleAsync() {
        var (latest, current) = await CheckForUpdateAsync(forceCheck: true);

        if (latest is null) {
            Console.Error.WriteLine("Could not check for updates.");

            return 1;
        }

        if (!IsNewer(latest, current)) {
            await Console.Out.WriteLineAsync($"Already up to date: {current}");

            return 0;
        }

        await Console.Out.WriteLineAsync($"Update available: {current} → {latest}");
        await Console.Out.WriteLineAsync();
        await Console.Out.WriteLineAsync("Run:");
        await Console.Out.WriteLineAsync("  npm update -g @kurrent/kapacitor");

        return 0;
    }

    /// <summary>
    /// Print an update hint to stderr if a newer version is available.
    /// Called on every CLI invocation (cached, max once per 24h).
    /// </summary>
    public static async Task PrintUpdateHintIfAvailable() {
        var config = await AppConfig.Load();

        if (config?.UpdateCheck == false) return;

        try {
            var (latest, current) = await CheckForUpdateAsync(forceCheck: false);

            if (latest is not null && current is not null && IsNewer(latest, current)) {
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
                    var cacheJson     = await File.ReadAllTextAsync(CachePath);
                    var cache         = JsonNode.Parse(cacheJson);
                    var checkedAt     = cache?["checked_at"]?.GetValue<DateTimeOffset>();
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
        using var http = new HttpClient();
        http.Timeout = TimeSpan.FromSeconds(5);
        http.DefaultRequestHeaders.Add("User-Agent", "kapacitor-cli");

        try {
            var resp = await http.GetAsync("https://registry.npmjs.org/@kurrent/kapacitor/latest");

            if (!resp.IsSuccessStatusCode) return (null, current);

            var body   = await resp.Content.ReadAsStringAsync();
            var json   = JsonNode.Parse(body);
            var latest = json?["version"]?.GetValue<string>();

            // Cache result
            if (latest is not null) {
                var dir = Path.GetDirectoryName(CachePath)!;
                Directory.CreateDirectory(dir);

                var cacheObj = new JsonObject {
                    ["latest_version"] = latest,
                    ["checked_at"]     = DateTimeOffset.UtcNow
                };
                var tempPath = $"{CachePath}.tmp";
                await File.WriteAllTextAsync(tempPath, cacheObj.ToJsonString());
                File.Move(tempPath, CachePath, overwrite: true);
            }

            return (latest, current);
        } catch {
            return (null, current);
        }
    }

    /// <summary>
    /// Returns true if <paramref name="latest"/> is a newer semver than <paramref name="current"/>.
    /// Prerelease suffixes (e.g. "-alpha.0.1") are stripped so that a local prerelease build
    /// is not considered older than the stable version it's based on.
    /// </summary>
    static bool IsNewer(string? latest, string? current) {
        if (latest is null || current is null) return false;

        // Strip prerelease suffix (everything after first '-') for comparison
        static Version? ParseCore(string v) {
            var dash = v.IndexOf('-');
            if (dash >= 0) v = v[..dash];
            return Version.TryParse(v, out var parsed) ? parsed : null;
        }

        var latestVersion  = ParseCore(latest);
        var currentVersion = ParseCore(current);
        if (latestVersion is null || currentVersion is null) return latest != current;

        return latestVersion > currentVersion;
    }

    static string? GetCurrentVersion() {
        var version = typeof(UpdateCommand).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;

        // Strip build metadata (everything after '+') so that
        // "0.1.2+d727aeea..." compares equal to npm's "0.1.2"
        if (version is not null) {
            var plusIndex = version.IndexOf('+');

            if (plusIndex >= 0)
                version = version[..plusIndex];
        }

        return version;
    }
}
