using System.Text.Json;
using kapacitor.Config;

namespace kapacitor.Commands;

public static class ConfigCommand {
    public static Task<int> HandleAsync(string[] args) {
        if (args.Length < 2) {
            Console.Error.WriteLine("Usage: kapacitor config <show|set> [key] [value]");
            return Task.FromResult(1);
        }

        var subcommand = args[1];

        return subcommand switch {
            "show" => Task.FromResult(Show()),
            "set" when args.Length >= 4 => Task.FromResult(Set(args[2], args[3])),
            "set" => Task.FromResult(SetUsage()),
            _ => Task.FromResult(UnknownSubcommand(subcommand))
        };
    }

    static int Show() {
        var config = AppConfig.Load();
        if (config is null) {
            Console.WriteLine("No config file found.");
            Console.WriteLine($"  Path: {AppConfig.GetConfigPath()}");
            Console.WriteLine("  Run `kapacitor setup` to create one.");
            return 0;
        }

        var json = JsonSerializer.Serialize(config, ConfigJsonContextIndented.Default.KapacitorConfig);
        Console.WriteLine(json);
        Console.WriteLine();
        Console.WriteLine($"  Path: {AppConfig.GetConfigPath()}");
        return 0;
    }

    static int Set(string key, string value) {
        var config = AppConfig.Load() ?? new KapacitorConfig();

        config = key switch {
            "server_url" => config with { ServerUrl = value },
            "daemon.name" => config with {
                Daemon = (config.Daemon ?? new DaemonSettings()) with { Name = value }
            },
            "daemon.max_agents" when int.TryParse(value, out var n) => config with {
                Daemon = (config.Daemon ?? new DaemonSettings()) with { MaxAgents = n }
            },
            "update_check" when bool.TryParse(value, out var b) => config with { UpdateCheck = b },
            _ => throw new ArgumentException($"Unknown config key: {key}")
        };

        AppConfig.Save(config);
        Console.WriteLine($"Set {key} = {value}");
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
        return 1;
    }

    static int UnknownSubcommand(string subcommand) {
        Console.Error.WriteLine($"Unknown config subcommand: {subcommand}");
        return 1;
    }
}
