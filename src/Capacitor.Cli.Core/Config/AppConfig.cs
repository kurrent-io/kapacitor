using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Capacitor.Cli.Core.Config;

/// <summary>
/// Legacy v1 flat config — a deserialization DTO used ONLY for one-way v1→v2
/// migration in <see cref="ConfigMigration"/>. Not a live config; do not read
/// this elsewhere.
/// </summary>
public record LegacyV1Config {
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

    [JsonPropertyName("claude_path")]
    public string? ClaudePath { get; init; }

    [JsonPropertyName("codex_path")]
    public string? CodexPath { get; init; }
}

[JsonSerializable(typeof(LegacyV1Config))]
internal partial class ConfigJsonContext : JsonSerializerContext;

public static class AppConfig {
    // Flipped the first time a legacy v1 config is migrated to v2 in this process,
    // so the deprecation notice in LoadProfileConfig fires at most once per run.
    static bool _v1MigrationSignalled;

    public static string RepoRoot => GetGitRepoRoot() ?? Environment.CurrentDirectory;

    /// <summary>
    /// Resolve server URL using only the active profile (or KCAP_PROFILE /
    /// KCAP_URL / --server-url overrides). Skips repo discovery and git
    /// remote matching — used by the daemon, which is not bound to a working
    /// directory.
    /// </summary>
    public static async Task<ProfileContext> ResolveActiveProfile(string[] args, ConfigRoot config) {
        var idx          = Array.IndexOf(args, "--server-url");
        var cliServerUrl = (idx >= 0 && idx + 1 < args.Length) ? args[idx + 1] : null;
        var envUrl       = Environment.GetEnvironmentVariable("KCAP_URL");
        var envProfile   = Environment.GetEnvironmentVariable("KCAP_PROFILE");

        var loaded   = await LoadProfileConfig(config);
        var resolver = new ProfileResolver(
            loaded,
            cliServerUrl,
            envUrl,
            envProfile,
            repoConfig: null,
            repoRemoteUrls: [],
            repoPath: null
        );

        var resolved = resolver.Resolve();

        if (resolved.Warning is not null) {
            await Console.Error.WriteLineAsync($"Warning: {resolved.Warning}");
        }

        return new(resolved, loaded);
    }

    public static async Task<ProfileContext> ResolveForRepo(string[] args, ConfigRoot root, int gitTimeoutMs = 5000) {
        var idx          = Array.IndexOf(args, "--server-url");
        var cliServerUrl = (idx >= 0 && idx + 1 < args.Length) ? args[idx + 1] : null;

        var envUrl     = Environment.GetEnvironmentVariable("KCAP_URL");
        var envProfile = Environment.GetEnvironmentVariable("KCAP_PROFILE");

        // Short-circuit: if explicit URL is provided, skip all profile/repo resolution
        if (cliServerUrl is not null || envUrl is not null) {
            var config = await LoadProfileConfig(root);

            var resolver = new ProfileResolver(
                config,
                cliServerUrl,
                envUrl,
                envProfile,
                repoConfig: null,
                repoRemoteUrls: [],
                repoPath: null
            );
            var quickResolved = resolver.Resolve();

            return new(quickResolved, config);
        }

        {
            var config = await LoadProfileConfig(root);

            var repoRoot = GetGitRepoRoot(gitTimeoutMs) ?? Environment.CurrentDirectory;

            RepoConfig? repoConfig     = null;
            var         repoConfigPath = Path.Combine(repoRoot, ".kcap.json");

            if (File.Exists(repoConfigPath)) {
                try {
                    var json = await File.ReadAllTextAsync(repoConfigPath);
                    repoConfig = JsonSerializer.Deserialize(json, RepoConfigJsonContext.Default.RepoConfig);
                } catch {
                    /* ignore malformed */
                }
            }

            var remoteUrls = GetGitRemoteUrls(gitTimeoutMs);

            var resolver = new ProfileResolver(
                config,
                cliServerUrl,
                envUrl,
                envProfile,
                repoConfig,
                remoteUrls,
                repoRoot
            );

            var resolved = resolver.Resolve();

            if (resolved.Warning is not null) {
                await Console.Error.WriteLineAsync($"Warning: {resolved.Warning}");
            }

            return new(resolved, config);
        }
    }

    static string[] GetGitRemoteUrls(int timeoutMs = 5000) {
        try {
            var psi = new ProcessStartInfo("git", "remote -v") {
                RedirectStandardOutput = true,
                RedirectStandardError  = true,
                UseShellExecute        = false,
                CreateNoWindow         = true
            };
            using var proc = Process.Start(psi);

            if (proc is null) return [];

            var output = proc.StandardOutput.ReadToEnd();

            if (!proc.WaitForExit(timeoutMs)) {
                try { proc.Kill(); } catch {
                    /* best effort */
                }

                return [];
            }

            return output.Split('\n', StringSplitOptions.RemoveEmptyEntries)
                .Select(line => line.Split('\t', ' ').ElementAtOrDefault(1))
                .Where(url => url is not null)
                .Distinct()
                .ToArray()!;
        } catch {
            return [];
        }
    }

    static string? GetGitRepoRoot(int timeoutMs = 5000) {
        try {
            var psi = new ProcessStartInfo("git", "rev-parse --show-toplevel") {
                RedirectStandardOutput = true,
                RedirectStandardError  = true,
                UseShellExecute        = false,
                CreateNoWindow         = true
            };
            using var proc = Process.Start(psi);

            if (proc is null) return null;

            var output = proc.StandardOutput.ReadToEnd().Trim();

            if (!proc.WaitForExit(timeoutMs)) {
                try { proc.Kill(); } catch {
                    /* best effort */
                }

                return null;
            }

            return proc.ExitCode == 0 && !string.IsNullOrEmpty(output) ? output : null;
        } catch {
            return null;
        }
    }

    /// <summary>
    /// Removes trailing slashes from a URL to prevent double-slash issues
    /// when appending paths (e.g., <c>https://example.com/</c> + <c>/auth/config</c>).
    /// </summary>
    public static string NormalizeUrl(string url) => url.TrimEnd('/');

    /// <summary>The full set of accepted <c>default_visibility</c> values, server-agnostic
    /// (a server that gates Projects off simply treats <c>project</c> as owner-only).
    /// Public (not internal) so other surfaces that validate or offer this value — CLI-side
    /// (e.g. <c>SetupCommand</c>'s <c>--default-visibility</c> flag and interactive wizard
    /// choice list) and the desktop app, which has no <c>InternalsVisibleTo</c> grant into
    /// this assembly — can't drift out of sync with what config actually accepts.</summary>
    public static readonly string[] ValidVisibilities = ["private", "project", "org_public", "public"];

    /// <summary>
    /// Whether any profile has actually been configured — NOT whether <see cref="LoadProfileConfig"/>
    /// returned one. That method synthesizes a `default` entry whenever config.json is missing or
    /// unreadable, so `Profiles.Count > 0` is true on a machine that has never run kcap and cannot
    /// distinguish a first-time setup from a re-run. A server URL is what setup actually persists,
    /// so it is what "configured" means here.
    /// </summary>
    public static bool HasConfiguredProfile(ProfileConfig config) =>
        config.Profiles.Values.Any(p => !string.IsNullOrWhiteSpace(p.ServerUrl));

    public static async Task<ProfileConfig> LoadProfileConfig(ConfigRoot config, CancellationToken ct = default) {
        var configPath = GetConfigPath(config);

        if (!File.Exists(configPath))
            return ProfileConfig.Fresh();

        string json;

        try {
            json = await File.ReadAllTextAsync(configPath, ct);
        } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) {
            await Console.Error.WriteLineAsync($"Warning: could not read config at {configPath}: {ex.Message}");

            return ProfileConfig.Fresh();
        }

        ConfigMigration.MigrationResult result;

        try {
            result = ConfigMigration.MigrateIfNeeded(json);
        } catch (JsonException ex) {
            await Console.Error.WriteLineAsync($"Warning: invalid config at {configPath}: {ex.Message}");

            return ProfileConfig.Fresh();
        }

        // Persist a v1→v2 migration when possible, but never drop the in-memory
        // migrated config if the write fails (e.g. read-only volume). Losing the
        // server URL here previously caused `ServerUrl is required` at daemon
        // startup despite the on-disk config being intact.
        if (result.ShouldPersist) {
            // Deprecation signal for the v1 config format. `ShouldPersist` is set ONLY
            // when a real v1 flat config was migrated (a fresh/empty config and an
            // already-v2 config both leave it false), so this marks exactly a genuine
            // v1 straggler. The v1 format is slated for removal; this one-time-per-run
            // line is how we watch whether any v1 configs still exist in the wild
            // before deleting the ConfigMigration path. Best-effort — a logging
            // failure must never break a config load.
            if (!_v1MigrationSignalled) {
                _v1MigrationSignalled = true;
                try {
                    await Console.Error.WriteLineAsync(
                        "kcap: migrated your config from the deprecated v1 format to the current format (one-time).");
                } catch {
                    /* best effort */
                }
            }

            try {
                // Identity mutation: MutateAsync re-reads the file fresh under the lock and
                // re-applies ConfigMigration itself, so it publishes the same migrated form
                // `result.Config` holds here — the mutate callback need not (and must not,
                // to avoid clobbering a concurrent writer) reuse this already-read snapshot.
                await ConfigMutator.MutateAsync(config, c => c, ct);
            } catch (Exception ex) when (ex is not OperationCanceledException) {
                // Broad on purpose: unlike the pre-ConfigMutator write, this path now also goes
                // through ConfigFileLock, which can throw TimeoutException (10s lock wait) or a
                // mutex-open failure that isn't UnauthorizedAccessException — every kcap command
                // hits this at startup, and the comment above still holds: the in-memory migrated
                // config must never be dropped just because the best-effort persist couldn't run.
                // OperationCanceledException is excluded so a caller's own cancellation still
                // propagates instead of being swallowed as a warning.
                await Console.Error.WriteLineAsync($"Warning: could not persist migrated config at {configPath}: {ex.Message}");
            }
        }

        return NormalizeProfileVisibilities(result.Config);
    }

    /// <summary>
    /// Coerce each profile's <c>default_visibility</c> to the same set the
    /// legacy v1 config read path used to enforce (lowercase, restricted to
    /// <see cref="ValidVisibilities"/>; fall back to <c>org_public</c>
    /// otherwise). Manual edits and v1→v2 migrations bypass the validation
    /// that <c>kcap config set</c> / <c>kcap setup</c> apply at write time,
    /// so a profile on disk can carry through values like <c>"Private"</c>
    /// or <c>"foo"</c> that the server would reject.
    /// </summary>
    static ProfileConfig NormalizeProfileVisibilities(ProfileConfig config) {
        Dictionary<string, Profile>? rebuilt = null;

        foreach (var (name, profile) in config.Profiles) {
            var raw        = profile.DefaultVisibility ?? "org_public";
            var normalized = raw.ToLowerInvariant();

            if (!ValidVisibilities.Contains(normalized)) normalized = "org_public";

            if (normalized == profile.DefaultVisibility) continue;

            rebuilt                                                 ??= new(config.Profiles);
            rebuilt[name] = profile with { DefaultVisibility = normalized };
        }

        return rebuilt is null ? config : config with { Profiles = rebuilt };
    }

    /// <summary>The profile config file. <c>config.json</c>'s name is AppConfig's to know, not the
    /// root's.</summary>
    public const string ConfigFileName = "config.json";

    public static string GetConfigPath(ConfigRoot config) => config.Path(ConfigFileName);
}
