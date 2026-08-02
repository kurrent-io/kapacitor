using System.Text.Json;
using Capacitor.Cli.Core.Config;
using ProfileConfigJsonContextIndented = Capacitor.Cli.Core.Config.ProfileConfigJsonContextIndented;

namespace Capacitor.Cli.Commands;

public static class ConfigCommand {
    public static async Task<int> HandleAsync(string[] args) {
        if (args.Length < 2) {
            await Console.Error.WriteLineAsync("Usage: kcap config <show|set|unset> [key] [value]");

            return 1;
        }

        var subcommand = args[1];
        var skipProbe  = args.Contains("--no-probe");

        return subcommand switch {
            "show"                        => await Show(),
            "set" when args.Length >= 4   => await Set(args[2], args[3], skipProbe),
            "set"                         => SetUsage(),
            "unset" when args.Length >= 3 => await Unset(args[2]),
            "unset"                       => UnsetUsage(),
            _                             => UnknownSubcommand(subcommand)
        };
    }

    static async Task<int> Show() {
        var profileConfig = await AppConfig.LoadProfileConfig();
        var json          = JsonSerializer.Serialize(profileConfig, ProfileConfigJsonContextIndented.Default.ProfileConfig);
        await Console.Out.WriteLineAsync(json);
        await Console.Out.WriteLineAsync();
        await Console.Out.WriteLineAsync($"  Path: {AppConfig.GetConfigPath()}");

        return 0;
    }

    static async Task<int> Set(string key, string value, bool skipProbe) {
        if (key == "server_url") {
            var result = await ServerUrlNormalizer.NormalizeAsync(
                value, skipProbe, CancellationToken.None);

            if (result.Warning is not null)
                await Console.Error.WriteLineAsync($"Warning: {result.Warning}");

            value = result.Url;
        }

        var profileConfig = await AppConfig.LoadProfileConfig();
        var profileName   = profileConfig.ActiveProfile;
        var profile       = profileConfig.Profiles.GetValueOrDefault(profileName) ?? new Profile();

        profile = ApplySet(profile, key, value);

        var profiles = new Dictionary<string, Profile>(profileConfig.Profiles) { [profileName] = profile };
        profileConfig = profileConfig with { Profiles = profiles };
        await AppConfig.SaveProfileConfig(profileConfig);

        await Console.Out.WriteLineAsync($"Set {key} = {value} (profile: {profileName})");

        if (key == "flows.reviewer_vendor") {
            string[] knownVendors = ["agy", "claude", "codex", "copilot", "cursor", "gemini", "kiro", "opencode", "pi"];
            var normalized = value.Trim().ToLowerInvariant();
            if (!knownVendors.Contains(normalized))
                await Console.Error.WriteLineAsync(
                    $"Warning: '{normalized}' is not a vendor this kcap version knows; the server has the authoritative list and will reject an unknown vendor at start time.");
        }

        return 0;
    }

    static async Task<int> Unset(string key) {
        var profileConfig = await AppConfig.LoadProfileConfig();
        var profileName   = profileConfig.ActiveProfile;
        var profile       = profileConfig.Profiles.GetValueOrDefault(profileName) ?? new Profile();

        profile = ApplyUnset(profile, key);

        var profiles = new Dictionary<string, Profile>(profileConfig.Profiles) { [profileName] = profile };
        profileConfig = profileConfig with { Profiles = profiles };
        await AppConfig.SaveProfileConfig(profileConfig);

        await Console.Out.WriteLineAsync($"Unset {key} (profile: {profileName})");

        return 0;
    }

    /// <summary>
    /// Applies a single <c>key = value</c> update to a <see cref="Profile"/>. Pure function, exposed for testing.
    /// Throws <see cref="ArgumentException"/> on unknown keys or invalid values.
    /// </summary>
    public static Profile ApplySet(Profile profile, string key, string value) =>
        key switch {
            "server_url" => profile with { ServerUrl = value },
            "daemon.name" => profile with { Daemon = (profile.Daemon ?? new DaemonSettings()) with { Name = value } },
            "daemon.max_agents" when int.TryParse(value, out var n) => profile with { Daemon = (profile.Daemon ?? new DaemonSettings()) with { MaxAgents = n } },
            "daemon.claude_path" when !string.IsNullOrEmpty(value) => profile with { Daemon = (profile.Daemon ?? new DaemonSettings()) with { ClaudePath = value } },
            "daemon.claude_path" => throw new ArgumentException("Invalid value for daemon.claude_path: must not be empty."),
            "daemon.codex_path" when !string.IsNullOrEmpty(value) => profile with { Daemon = (profile.Daemon ?? new DaemonSettings()) with { CodexPath = value } },
            "daemon.codex_path" => throw new ArgumentException("Invalid value for daemon.codex_path: must not be empty."),
            "update_check" when bool.TryParse(value, out var b) => profile with { UpdateCheck = b },
            "update_check" => throw new ArgumentException($"Invalid value for update_check: '{value}'. Must be true or false."),
            "disable_session_guidelines" when bool.TryParse(value, out var b) => profile with { DisableSessionGuidelines = b },
            "disable_session_guidelines" => throw new ArgumentException($"Invalid value for disable_session_guidelines: '{value}'. Must be true or false."),
            "disable_memory_index" when bool.TryParse(value, out var b) => profile with { DisableMemoryIndex = b },
            "disable_memory_index" => throw new ArgumentException($"Invalid value for disable_memory_index: '{value}'. Must be true or false."),
            "use_provider_api_key" when bool.TryParse(value, out var b) => profile with { UseProviderApiKey = b },
            "use_provider_api_key" => throw new ArgumentException($"Invalid value for use_provider_api_key: '{value}'. Must be true or false."),
            "default_visibility" when value is "private" or "project" or "org_public" or "public" => profile with { DefaultVisibility = value },
            "default_visibility" => throw new ArgumentException("Invalid value. Must be: private, project, org_public, or public"),
            "excluded_repos" => profile with { ExcludedRepos = value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries) },
            "flows.reviewer_vendor" when !string.IsNullOrWhiteSpace(value) =>
                profile with { Flows = (profile.Flows ?? new FlowsSettings()) with { ReviewerVendor = value.Trim().ToLowerInvariant() } },
            "flows.reviewer_vendor" => throw new ArgumentException(
                "Invalid value for flows.reviewer_vendor: must not be empty. Use 'kcap config unset flows.reviewer_vendor' to remove it."),
            _ => throw new ArgumentException($"Unknown config key: {key}")
        };

    /// <summary>
    /// Applies a single key removal to a <see cref="Profile"/>. Pure function, exposed for testing.
    /// Throws <see cref="ArgumentException"/> on unknown or non-unsettable keys.
    /// </summary>
    public static Profile ApplyUnset(Profile profile, string key) =>
        key switch {
            "flows.reviewer_vendor" => profile with { Flows = (profile.Flows ?? new FlowsSettings()) with { ReviewerVendor = null } },
            _ => throw new ArgumentException($"Unknown or non-unsettable config key: {key}")
        };

    static int SetUsage() {
        Console.Error.WriteLine("Usage: kcap config set <key> <value> [--no-probe]");
        Console.Error.WriteLine();
        Console.Error.WriteLine("Keys:");
        Console.Error.WriteLine("  server_url                  Server URL");
        Console.Error.WriteLine("  daemon.name                 Daemon name");
        Console.Error.WriteLine("  daemon.max_agents           Max concurrent hosted coding agents");
        Console.Error.WriteLine("  daemon.claude_path          Path to claude binary (default: claude)");
        Console.Error.WriteLine("  daemon.codex_path           Path to codex binary (default: codex)");
        Console.Error.WriteLine("  update_check                Enable update check (true/false)");
        Console.Error.WriteLine("  default_visibility          Default session visibility (private, project, org_public, public)");
        Console.Error.WriteLine("  disable_session_guidelines  Skip injecting recurring-lessons context at SessionStart (true/false)");
        Console.Error.WriteLine("  use_provider_api_key        Keep ANTHROPIC_API_KEY/OPENAI_API_KEY in headless agent spawns (true/false)");
        Console.Error.WriteLine("  excluded_repos              Excluded repos, comma-separated (owner/repo,owner/repo)");
        Console.Error.WriteLine("  flows.reviewer_vendor       Preferred review-flow reviewer vendor (used only when the definition names none)");
        Console.Error.WriteLine();
        Console.Error.WriteLine("Flags:");
        Console.Error.WriteLine("  --no-probe                  Skip the reachability check when setting server_url");

        return 1;
    }

    static int UnsetUsage() {
        Console.Error.WriteLine("Usage: kcap config unset <key>");

        return 1;
    }

    static int UnknownSubcommand(string subcommand) {
        Console.Error.WriteLine($"Unknown config subcommand: {subcommand}");

        return 1;
    }
}
