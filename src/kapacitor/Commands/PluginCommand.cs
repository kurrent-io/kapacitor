using System.Text.Json;
using System.Text.Json.Nodes;
using kapacitor.Config;

namespace kapacitor.Commands;

public static class PluginCommand {
    public static async Task<int> HandleAsync(string[] args) {
        if (args.Length < 2) {
            PrintUsage();
            return 1;
        }

        return args[1] switch {
            "install" => await Install(args),
            "remove"  => await Remove(args),
            _         => PrintUsage()
        };
    }

    static async Task<int> Install(string[] args) {
        var pluginPath = SetupCommand.ResolvePluginPath();

        if (pluginPath is null) {
            Console.Error.WriteLine("Plugin directory not found. Re-install kapacitor via npm:");
            Console.Error.WriteLine("  npm install -g @kurrent/kapacitor");
            return 1;
        }

        var scope = "user";

        if (args.Contains("--project"))
            scope = "project";

        var settingsPath = scope == "project"
            ? Path.Combine(Environment.CurrentDirectory, ".claude", "settings.local.json")
            : ClaudePaths.UserSettings;

        var installed = SetupCommand.InstallPlugin(settingsPath, pluginPath);

        if (installed) {
            await Console.Out.WriteLineAsync($"Plugin installed ({scope}: {settingsPath})");
        } else {
            Console.Error.WriteLine("Could not update settings file.");
            return 1;
        }

        return 0;
    }

    static async Task<int> Remove(string[] args) {
        var scope = "user";

        if (args.Contains("--project"))
            scope = "project";

        var settingsPath = scope == "project"
            ? Path.Combine(Environment.CurrentDirectory, ".claude", "settings.local.json")
            : ClaudePaths.UserSettings;

        if (!File.Exists(settingsPath)) {
            await Console.Out.WriteLineAsync("Nothing to remove — settings file not found.");
            return 0;
        }

        try {
            var text = File.ReadAllText(settingsPath);

            if (JsonNode.Parse(text) is not JsonObject root) {
                await Console.Out.WriteLineAsync("Nothing to remove.");
                return 0;
            }

            var changed = false;

            if (root["enabledPlugins"] is JsonObject enabled) {
                changed |= enabled.Remove("kapacitor@kapacitor");
                changed |= enabled.Remove("kapacitor@kurrent"); // stale entry
            }

            if (root["extraKnownMarketplaces"] is JsonObject marketplaces) {
                changed |= marketplaces.Remove("kapacitor");
                changed |= marketplaces.Remove("kurrent"); // stale entry
            }

            if (changed) {
                var opts = new JsonSerializerOptions { WriteIndented = true };
                File.WriteAllText(settingsPath, root.ToJsonString(opts));
                await Console.Out.WriteLineAsync($"Plugin removed ({scope}: {settingsPath})");
            } else {
                await Console.Out.WriteLineAsync("Plugin was not installed.");
            }

            return 0;
        } catch (Exception ex) {
            Console.Error.WriteLine($"Could not update settings: {ex.Message}");
            return 1;
        }
    }

    static int PrintUsage() {
        Console.Error.WriteLine("Usage: kapacitor plugin <install|remove> [--project]");
        return 1;
    }
}
