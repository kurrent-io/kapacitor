using System.Text.Json;
using kapacitor.Config;

namespace kapacitor.Commands;

public static class ProfileCommand {
    public static async Task<int> HandleAsync(string[] args) {
        if (args.Length < 2) {
            await PrintUsage();

            return 1;
        }

        var configPath = AppConfig.GetConfigPath();

        return args[1] switch {
            "add"                          => await HandleAdd(configPath, args),
            "list"                         => await HandleList(configPath),
            "remove" when args.Length >= 3 => await RemoveProfile(configPath, args[2]),
            "show"                         => await HandleShow(configPath, args),
            _                              => await PrintUsage()
        };
    }

    static async Task<int> HandleAdd(string configPath, string[] args) {
        if (args.Length < 3) {
            await Console.Error.WriteLineAsync("Usage: kapacitor profile add <name> --server-url <url> [--remote <pattern>]...");

            return 1;
        }

        var name      = args[2];
        var serverUrl = GetArg(args, "--server-url");

        if (serverUrl is null) {
            await Console.Error.WriteLineAsync("--server-url is required");

            return 1;
        }

        var remotes = new List<string>();

        for (var i = 0; i < args.Length; i++) {
            if (args[i] == "--remote" && i + 1 < args.Length)
                remotes.Add(args[++i]);
        }

        return await AddProfile(configPath, name, serverUrl, remotes.ToArray());
    }

    internal static async Task<int> AddProfile(string configPath, string name, string serverUrl, string[] remotes) {
        var config = await LoadConfig(configPath);

        if (config.Profiles.ContainsKey(name)) {
            await Console.Error.WriteLineAsync($"Profile '{name}' already exists. Remove it first.");

            return 1;
        }

        var profiles = new Dictionary<string, Profile>(config.Profiles) {
            [name] = new() {
                ServerUrl = AppConfig.NormalizeUrl(serverUrl),
                Remotes   = remotes
            }
        };

        config = config with { Profiles = profiles };
        await SaveConfig(configPath, config);

        await Console.Out.WriteLineAsync($"Profile '{name}' added.");

        return 0;
    }

    static async Task<int> HandleList(string configPath) {
        var config = await LoadConfig(configPath);

        foreach (var (name, profile) in config.Profiles) {
            var active = name == config.ActiveProfile ? " (active)" : "";
            var url    = profile.ServerUrl ?? "(no server URL)";
            await Console.Out.WriteLineAsync($"  {name}{active} — {url}");

            if (profile.Remotes is { Length: > 0 }) {
                foreach (var remote in profile.Remotes)
                    await Console.Out.WriteLineAsync($"    remote: {remote}");
            }
        }

        return 0;
    }

    internal static async Task<int> RemoveProfile(string configPath, string name) {
        if (name == "default") {
            await Console.Error.WriteLineAsync("Cannot remove the default profile.");

            return 1;
        }

        var config = await LoadConfig(configPath);

        if (!config.Profiles.ContainsKey(name)) {
            await Console.Error.WriteLineAsync($"Profile '{name}' not found.");

            return 1;
        }

        var profiles = new Dictionary<string, Profile>(config.Profiles);
        profiles.Remove(name);

        var bindings  = new Dictionary<string, string>(config.ProfileBindings);
        var staleKeys = bindings.Where(kv => kv.Value == name).Select(kv => kv.Key).ToList();
        foreach (var key in staleKeys) bindings.Remove(key);

        config = config with {
            Profiles = profiles,
            ProfileBindings = bindings,
            ActiveProfile = config.ActiveProfile == name ? "default" : config.ActiveProfile
        };

        await SaveConfig(configPath, config);
        await Console.Out.WriteLineAsync($"Profile '{name}' removed.");

        return 0;
    }

    static async Task<int> HandleShow(string configPath, string[] args) {
        var config = await LoadConfig(configPath);
        var name   = args.Length >= 3 ? args[2] : config.ActiveProfile;

        if (!config.Profiles.TryGetValue(name, out var profile)) {
            await Console.Error.WriteLineAsync($"Profile '{name}' not found.");

            return 1;
        }

        await Console.Out.WriteLineAsync($"Profile: {name}");
        await Console.Out.WriteLineAsync($"  server_url: {profile.ServerUrl ?? "(not set)"}");
        await Console.Out.WriteLineAsync($"  default_visibility: {profile.DefaultVisibility}");
        await Console.Out.WriteLineAsync($"  update_check: {profile.UpdateCheck}");
        await Console.Out.WriteLineAsync($"  daemon.name: {profile.Daemon?.Name            ?? "(not set)"}");
        await Console.Out.WriteLineAsync($"  daemon.max_agents: {profile.Daemon?.MaxAgents ?? 5}");

        if (profile.Remotes is { Length: > 0 }) {
            await Console.Out.WriteLineAsync($"  remotes:");

            foreach (var r in profile.Remotes)
                await Console.Out.WriteLineAsync($"    - {r}");
        }

        if (profile.ExcludedRepos is { Length: > 0 }) {
            await Console.Out.WriteLineAsync($"  excluded_repos: {string.Join(", ", profile.ExcludedRepos)}");
        }

        return 0;
    }

    static async Task<ProfileConfig> LoadConfig(string configPath) {
        if (!File.Exists(configPath))
            return new ProfileConfig { Profiles = new Dictionary<string, Profile> { ["default"] = new() } };

        var json = await File.ReadAllTextAsync(configPath);

        return ConfigMigration.MigrateIfNeeded(json).Config;
    }

    static async Task SaveConfig(string configPath, ProfileConfig config) {
        var dir = Path.GetDirectoryName(configPath)!;
        Directory.CreateDirectory(dir);
        var tempPath = $"{configPath}.tmp";

        await File.WriteAllBytesAsync(
            tempPath,
            JsonSerializer.SerializeToUtf8Bytes(config, ProfileConfigJsonContextIndented.Default.ProfileConfig)
        );
        File.Move(tempPath, configPath, overwrite: true);
    }

    static async Task<int> PrintUsage() {
        await Console.Error.WriteLineAsync("Usage: kapacitor profile <add|list|remove|show>");
        await Console.Error.WriteLineAsync();
        await Console.Error.WriteLineAsync("  add <name> --server-url <url> [--remote <pattern>]...");
        await Console.Error.WriteLineAsync("  list                          Show all profiles");
        await Console.Error.WriteLineAsync("  remove <name>                 Remove a profile");
        await Console.Error.WriteLineAsync("  show [name]                   Show profile details");

        return 1;
    }

    static string? GetArg(string[] args, string name) {
        var idx = Array.IndexOf(args, name);

        return idx >= 0 && idx + 1 < args.Length ? args[idx + 1] : null;
    }
}
