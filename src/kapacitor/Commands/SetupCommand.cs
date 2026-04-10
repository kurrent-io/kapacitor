using System.Text.Json;
using System.Text.Json.Nodes;
using kapacitor.Auth;
using kapacitor.Config;
// ReSharper disable MethodHasAsyncOverload

namespace kapacitor.Commands;

public static class SetupCommand {
    public static async Task<int> HandleAsync(string[] args) {
        var serverUrlArg = GetArg(args, "--server-url");
        var noPrompt     = args.Contains("--no-prompt");

        await Console.Out.WriteLineAsync();
        await Console.Out.WriteLineAsync("Welcome to Kapacitor!");
        await Console.Out.WriteLineAsync();

        // Check if already configured
        var existing       = await AppConfig.Load();
        var existingTokens = await TokenStore.LoadAsync();

        if (existing?.ServerUrl is not null && existingTokens is not null && !noPrompt) {
            Console.Write($"Already configured for {existing.ServerUrl} as {existingTokens.GitHubUsername}. Re-run setup? [y/N] ");
            var answer = Console.ReadLine()?.Trim().ToLowerInvariant();

            if (answer is not "y" and not "yes") {
                await Console.Out.WriteLineAsync("Setup cancelled.");

                return 0;
            }
        }

        // Step 1: Server URL
        await Console.Out.WriteLineAsync("Step 1/5: Server");
        string serverUrl;

        if (serverUrlArg is not null) {
            serverUrl = serverUrlArg;
            await Console.Out.WriteLineAsync($"  Server URL: {serverUrl}");
        } else if (noPrompt) {
            Console.Error.WriteLine("  --server-url is required with --no-prompt");

            return 1;
        } else {
            Console.Write("  Enter your Capacitor server URL: ");
            serverUrl = Console.ReadLine()?.Trim() ?? "";

            if (string.IsNullOrEmpty(serverUrl)) {
                Console.Error.WriteLine("  Server URL is required.");

                return 1;
            }
        }

        // Normalize: strip trailing slashes to avoid double-slash URLs
        serverUrl = AppConfig.NormalizeUrl(serverUrl);

        // Validate server reachability
        Console.Write("  Checking server... ");
        string provider;

        try {
            provider = await HttpClientExtensions.DiscoverProviderAsync(serverUrl);
            await Console.Out.WriteLineAsync($"✓ Reachable. Auth provider: {provider}");
        } catch (Exception ex) {
            Console.Error.WriteLine($"✗ Cannot reach server: {ex.Message}");

            return 1;
        }

        await Console.Out.WriteLineAsync();

        // Step 2: Login
        await Console.Out.WriteLineAsync("Step 2/5: Login");

        if (provider == "None") {
            await Console.Out.WriteLineAsync("  Auth provider is None — no login required.");
        } else {
            var loginResult = await OAuthLoginFlow.LoginWithDiscoveryAsync(serverUrl);

            if (loginResult != 0) {
                Console.Error.WriteLine("  Login failed.");

                return 1;
            }

            var tokens = await TokenStore.LoadAsync();
            await Console.Out.WriteLineAsync($"  ✓ Logged in as {tokens?.GitHubUsername}");
        }

        await Console.Out.WriteLineAsync();

        // Step 3: Default session visibility
        await Console.Out.WriteLineAsync("Step 3/5: Default session visibility");
        await Console.Out.WriteLineAsync();
        await Console.Out.WriteLineAsync("  How should your sessions be visible to others by default?");
        await Console.Out.WriteLineAsync();
        await Console.Out.WriteLineAsync("    1) All private — only you can see your sessions");
        await Console.Out.WriteLineAsync("    2) Org repos public, others private (current default)");
        await Console.Out.WriteLineAsync("    3) All public — everyone can see all your sessions");
        await Console.Out.WriteLineAsync();

        string defaultVisibility;

        if (noPrompt) {
            defaultVisibility = (GetArg(args, "--default-visibility") ?? "org_public").ToLowerInvariant();

            if (defaultVisibility is not "private" and not "org_public" and not "public") {
                Console.Error.WriteLine($"  Invalid default-visibility: {defaultVisibility}. Must be: private, org_public, or public");

                return 1;
            }

            await Console.Out.WriteLineAsync($"  Default visibility: {defaultVisibility}");
        } else {
            while (true) {
                Console.Write("  Choose [1-3] (default: 2): ");
                var choice = Console.ReadLine()?.Trim();

                defaultVisibility = choice switch {
                    "" or null or "2" => "org_public",
                    "1"               => "private",
                    "3"               => "public",
                    _                 => ""
                };

                if (defaultVisibility != "") break;

                await Console.Out.WriteLineAsync("  Invalid choice. Please enter 1, 2, or 3.");
            }
        }

        await Console.Out.WriteLineAsync();

        // Step 4: Claude Code plugin
        await Console.Out.WriteLineAsync("Step 4/5: Claude Code Plugin");
        await Console.Out.WriteLineAsync("  The Kapacitor plugin provides hooks, skills, and collaborative memory.");
        await Console.Out.WriteLineAsync();

        var pluginPath = ResolvePluginPath();

        if (pluginPath is not null) {
            var marketplacePath = Path.GetDirectoryName(pluginPath)!;

            await Console.Out.WriteLineAsync("  Where should the plugin be installed?");
            await Console.Out.WriteLineAsync();
            await Console.Out.WriteLineAsync("    1) User-wide — all Claude Code sessions (recommended)");
            await Console.Out.WriteLineAsync("    2) This project only");
            await Console.Out.WriteLineAsync("    3) Skip — I'll install it manually");
            await Console.Out.WriteLineAsync();

            string pluginScope;

            if (noPrompt) {
                pluginScope = GetArg(args, "--plugin-scope") ?? "user";
                await Console.Out.WriteLineAsync($"  Plugin scope: {pluginScope}");
            } else {
                while (true) {
                    Console.Write("  Choose [1-3] (default: 1): ");
                    var choice = Console.ReadLine()?.Trim();

                    pluginScope = choice switch {
                        "" or null or "1" => "user",
                        "2"               => "project",
                        "3"               => "skip",
                        _                 => ""
                    };

                    if (pluginScope != "") break;

                    await Console.Out.WriteLineAsync("  Invalid choice. Please enter 1, 2, or 3.");
                }
            }

            if (pluginScope == "skip") {
                await Console.Out.WriteLineAsync("  Skipped. Install manually inside Claude Code:");
                await Console.Out.WriteLineAsync($"    /plugin install {pluginPath}");
            } else {
                var settingsPath = pluginScope == "project"
                    ? Path.Combine(Environment.CurrentDirectory, ".claude", "settings.local.json")
                    : ClaudePaths.UserSettings;

                var installed = InstallPlugin(settingsPath, marketplacePath);

                if (installed) {
                    var scope = pluginScope == "project" ? "project" : "user";
                    await Console.Out.WriteLineAsync($"  ✓ Plugin registered ({scope}: {settingsPath})");
                } else {
                    await Console.Out.WriteLineAsync("  ⚠ Could not update settings. Install manually inside Claude Code:");
                    await Console.Out.WriteLineAsync($"    /plugin install {pluginPath}");
                }
            }
        } else {
            await Console.Out.WriteLineAsync("  ⚠ Plugin directory not found. Re-install kapacitor via npm:");
            await Console.Out.WriteLineAsync("    npm install -g @kurrent/kapacitor");
        }

        await Console.Out.WriteLineAsync();

        // Step 5: Daemon name + save
        await Console.Out.WriteLineAsync("Step 5/5: Agent Daemon");

        var    defaultName = Environment.UserName.ToLowerInvariant();
        string daemonName;

        if (noPrompt) {
            daemonName = GetArg(args, "--daemon-name") ?? defaultName;
            await Console.Out.WriteLineAsync($"  Daemon name: {daemonName}");
        } else {
            Console.Write($"  Daemon name [{defaultName}]: ");
            var input = Console.ReadLine()?.Trim();
            daemonName = string.IsNullOrEmpty(input) ? defaultName : input;
        }

        await Console.Out.WriteLineAsync();

        // Save config
        var config = existing ?? new KapacitorConfig();

        config = config with {
            ServerUrl = serverUrl,
            DefaultVisibility = defaultVisibility,
            Daemon = (config.Daemon ?? new DaemonSettings()) with { Name = daemonName }
        };
        await AppConfig.Save(config);

        var finalTokens = await TokenStore.LoadAsync();
        await Console.Out.WriteLineAsync("Setup complete!");
        await Console.Out.WriteLineAsync($"  ✓ Server:  {serverUrl}");
        await Console.Out.WriteLineAsync($"  ✓ Visibility: {defaultVisibility}");
        await Console.Out.WriteLineAsync($"  ✓ Daemon:  {daemonName}");

        if (finalTokens is not null) {
            await Console.Out.WriteLineAsync($"  ✓ Auth:    {finalTokens.GitHubUsername} ({finalTokens.Provider})");
        }

        await Console.Out.WriteLineAsync($"  Config saved to {AppConfig.GetConfigPath()}");
        await Console.Out.WriteLineAsync();
        await Console.Out.WriteLineAsync("  Optional: start the agent daemon with `kapacitor agent start -d`");

        return 0;
    }

    static string? ResolvePluginPath() {
        var exePath = Environment.ProcessPath;

        if (exePath is null) return null;

        var exeDir = Path.GetDirectoryName(exePath);

        if (exeDir is null) return null;

        // Try: <exe_dir>/../../../../plugin  (npm optional-deps layout)
        // Binary is at <wrapper>/node_modules/@kurrent/<platform-pkg>/bin/kapacitor
        // Plugin is at <wrapper>/plugin
        var optDepsPluginPath = Path.GetFullPath(Path.Combine(exeDir, "..", "..", "..", "..", "plugin"));

        if (Directory.Exists(optDepsPluginPath))
            return optDepsPluginPath;

        // Try: <exe_dir>/../../kapacitor/plugin  (npm flat layout)
        var npmPluginPath = Path.GetFullPath(Path.Combine(exeDir, "..", "..", "kapacitor", "plugin"));

        if (Directory.Exists(npmPluginPath))
            return npmPluginPath;

        // Try: <exe_dir>/../plugin  (wrapper package direct layout)
        var wrapperPluginPath = Path.GetFullPath(Path.Combine(exeDir, "..", "plugin"));

        if (Directory.Exists(wrapperPluginPath))
            return wrapperPluginPath;

        // Try: repo root layout (dev mode)
        var repoPlugin = Path.GetFullPath(Path.Combine(exeDir, "..", "..", "plugin"));

        return Directory.Exists(repoPlugin) ? repoPlugin : null;
    }

    static string? GetArg(string[] args, string name) {
        var idx = Array.IndexOf(args, name);

        return idx >= 0 && idx + 1 < args.Length ? args[idx + 1] : null;
    }

    static readonly JsonSerializerOptions WriteOpts = new() { WriteIndented = true };

    /// <summary>
    /// Registers the kapacitor plugin in a Claude Code settings.json file by merging
    /// the marketplace source and enabling the plugin. Preserves all existing settings.
    /// </summary>
    internal static bool InstallPlugin(string settingsPath, string marketplacePath) {
        try {
            JsonObject root = [];

            if (File.Exists(settingsPath)) {
                try {
                    if (JsonNode.Parse(File.ReadAllText(settingsPath)) is JsonObject obj)
                        root = obj;
                } catch {
                    // Malformed JSON — start fresh
                }
            }

            // Ensure extraKnownMarketplaces.kurrent exists with the correct path
            if (root["extraKnownMarketplaces"] is not JsonObject marketplaces) {
                marketplaces                    = [];
                root["extraKnownMarketplaces"] = marketplaces;
            }

            marketplaces["kurrent"] = new JsonObject {
                ["source"] = new JsonObject {
                    ["source"] = "directory",
                    ["path"]   = marketplacePath
                }
            };

            // Ensure enabledPlugins.kapacitor@kurrent is true
            if (root["enabledPlugins"] is not JsonObject enabled) {
                enabled                = [];
                root["enabledPlugins"] = enabled;
            }

            enabled["kapacitor@kurrent"] = true;

            Directory.CreateDirectory(Path.GetDirectoryName(settingsPath)!);
            File.WriteAllText(settingsPath, root.ToJsonString(WriteOpts));

            return true;
        } catch {
            return false;
        }
    }
}
