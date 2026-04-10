using System.Text.Json;
using kapacitor.Config;

namespace kapacitor.Commands;

public static class UseCommand {
    public static async Task<int> HandleAsync(string[] args) {
        if (args.Length < 2) {
            await Console.Error.WriteLineAsync("Usage: kapacitor use <profile-name> [--global] [--save]");
            return 1;
        }

        var name = args[1];
        var global = args.Contains("--global");
        var save = args.Contains("--save");
        var repoPath = global ? null : Environment.CurrentDirectory;
        var configPath = AppConfig.GetConfigPath();

        return await SetProfile(configPath, name, repoPath, global, save, save ? Environment.CurrentDirectory : null);
    }

    internal static async Task<int> SetProfile(
        string configPath, string name, string? repoPath,
        bool global, bool save, string? savePath
    ) {
        var config = await LoadConfig(configPath);

        if (!config.Profiles.TryGetValue(name, out var profile)) {
            await Console.Error.WriteLineAsync($"Profile '{name}' not found. Run `kapacitor profile list` to see available profiles.");
            return 1;
        }

        if (global || repoPath is null) {
            config = config with { ActiveProfile = name };
            await Console.Out.WriteLineAsync($"Active profile set to '{name}' (global).");
        } else {
            var bindings = new Dictionary<string, string>(config.ProfileBindings) {
                [repoPath] = name
            };
            config = config with { ProfileBindings = bindings };
            await Console.Out.WriteLineAsync($"Profile '{name}' bound to {repoPath}.");
        }

        await SaveConfig(configPath, config);

        if (save && savePath is not null) {
            var repoConfig = new RepoConfig {
                Profile = name,
                ServerUrl = profile.ServerUrl
            };
            var repoConfigPath = Path.Combine(savePath, ".kapacitor.json");
            await File.WriteAllBytesAsync(repoConfigPath,
                JsonSerializer.SerializeToUtf8Bytes(repoConfig, RepoConfigJsonContextIndented.Default.RepoConfig));
            await Console.Out.WriteLineAsync($"Wrote {repoConfigPath} — commit this to share with your team.");
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
        await File.WriteAllBytesAsync(tempPath,
            JsonSerializer.SerializeToUtf8Bytes(config, ProfileConfigJsonContextIndented.Default.ProfileConfig));
        File.Move(tempPath, configPath, overwrite: true);
    }
}
