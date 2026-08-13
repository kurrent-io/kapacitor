using System.Text.Json;
using Capacitor.Cli.Core.Config;
using RepoConfigJsonContextIndented = Capacitor.Cli.Core.Config.RepoConfigJsonContextIndented;

namespace Capacitor.Cli.Commands;

public static class UseCommand {
    public static async Task<int> HandleAsync(string[] args) {
        if (args.Length < 2) {
            await Console.Error.WriteLineAsync("Usage: kcap use <profile-name> [--global] [--save]");
            return 1;
        }

        var name = args[1];
        var global = args.Contains("--global");
        var save = args.Contains("--save");
        var repoPath = global ? null : AppConfig.RepoRoot;
        var configPath = AppConfig.GetConfigPath();

        return await SetProfile(configPath, name, repoPath, global, save, save ? AppConfig.RepoRoot : null);
    }

    internal static async Task<int> SetProfile(
        string configPath, string name, string? repoPath,
        bool global, bool save, string? savePath
    ) {
        var config = await LoadConfig(configPath);

        if (!config.Profiles.TryGetValue(name, out var profile)) {
            await Console.Error.WriteLineAsync($"Profile '{name}' not found. Run `kcap profile list` to see available profiles.");
            return 1;
        }

        if (global || repoPath is null) {
            await ConfigMutator.MutateAsync(c => c with { ActiveProfile = name });
            await Console.Out.WriteLineAsync($"Active profile set to '{name}' (global).");
        } else {
            await ConfigMutator.MutateAsync(c => c with {
                ProfileBindings = new Dictionary<string, string>(c.ProfileBindings) { [repoPath] = name }
            });
            await Console.Out.WriteLineAsync($"Profile '{name}' bound to {repoPath}.");
        }

        if (save && savePath is not null) {
            var repoConfig = new RepoConfig {
                Profile = name,
                ServerUrl = profile.ServerUrl
            };
            var repoConfigPath = Path.Combine(savePath, ".kcap.json");
            await File.WriteAllBytesAsync(repoConfigPath,
                JsonSerializer.SerializeToUtf8Bytes(repoConfig, RepoConfigJsonContextIndented.Default.RepoConfig));
            await Console.Out.WriteLineAsync($"Wrote {repoConfigPath} — commit this to share with your team.");
        }

        return 0;
    }

    static async Task<ProfileConfig> LoadConfig(string configPath) {
        if (!File.Exists(configPath))
            return new() { Profiles = new() { ["default"] = new() } };

        var json = await File.ReadAllTextAsync(configPath);
        return ConfigMigration.MigrateIfNeeded(json).Config;
    }
}
