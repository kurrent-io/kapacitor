using System.Text.Json;
using kapacitor.Config;

namespace kapacitor.Commands;

public static class ConfigCommand {
    public static async Task<int> HandleAsync(string[] args) {
        if (args.Length < 2) {
            await Console.Error.WriteLineAsync("Usage: kapacitor config <show|set> [key] [value]");

            return 1;
        }

        var subcommand = args[1];

        return subcommand switch {
            "show"                      => await Show(),
            "set" when args.Length >= 4 => await Set(args[2], args[3]),
            "set"                       => SetUsage(),
            _                           => UnknownSubcommand(subcommand)
        };
    }

    static async Task<int> Show() {
        var config = await AppConfig.Load();

        if (config is null) {
            await Console.Out.WriteLineAsync("No config file found.");
            await Console.Out.WriteLineAsync($"  Path: {AppConfig.GetConfigPath()}");
            await Console.Out.WriteLineAsync("  Run `kapacitor setup` to create one.");

            return 0;
        }

        var json = JsonSerializer.Serialize(config, ConfigJsonContextIndented.Default.KapacitorConfig);
        await Console.Out.WriteLineAsync(json);
        await Console.Out.WriteLineAsync();
        await Console.Out.WriteLineAsync($"  Path: {AppConfig.GetConfigPath()}");

        return 0;
    }

    static async Task<int> Set(string key, string value) {
        var config = await AppConfig.Load() ?? new KapacitorConfig();

        config = key switch {
            "server_url" => config with { ServerUrl = value },
            "daemon.name" => config with { Daemon = (config.Daemon ?? new DaemonSettings()) with { Name = value } },
            "daemon.max_agents" when int.TryParse(value, out var n) => config with { Daemon = (config.Daemon ?? new DaemonSettings()) with { MaxAgents = n } },
            "update_check" when bool.TryParse(value, out var b) => config with { UpdateCheck = b },
            "default_visibility" when value is "private" or "org_public" or "public" => config with { DefaultVisibility = value },
            "default_visibility" => throw new ArgumentException("Invalid value. Must be: private, org_public, or public"),
            "excluded_repos" => config with { ExcludedRepos = value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries) },
            _ => throw new ArgumentException($"Unknown config key: {key}")
        };

        await AppConfig.Save(config);
        await Console.Out.WriteLineAsync($"Set {key} = {value}");

        return 0;
    }

    static int SetUsage() {
        Console.Error.WriteLine("Usage: kapacitor config set <key> <value>");
        Console.Error.WriteLine();
        Console.Error.WriteLine("Keys:");
        Console.Error.WriteLine("  server_url         Server URL");
        Console.Error.WriteLine("  daemon.name        Daemon name");
        Console.Error.WriteLine("  daemon.max_agents  Max concurrent agents");
        Console.Error.WriteLine("  update_check       Enable update check (true/false)");
        Console.Error.WriteLine("  default_visibility   Default session visibility (private, org_public, public)");
        Console.Error.WriteLine("  excluded_repos       Excluded repos, comma-separated (owner/repo,owner/repo)");

        return 1;
    }

    static int UnknownSubcommand(string subcommand) {
        Console.Error.WriteLine($"Unknown config subcommand: {subcommand}");

        return 1;
    }
}
