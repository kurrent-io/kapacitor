using System.Text.Json;
using System.Text.Json.Nodes;
using kapacitor.Auth;
using kapacitor.Config;

namespace kapacitor.Commands;

public static class SetupCommand {
    public static async Task<int> HandleAsync(string[] args) {
        var serverUrlArg = GetArg(args, "--server-url");
        var noPrompt     = args.Contains("--no-prompt");

        Console.WriteLine();
        Console.WriteLine("Welcome to Kapacitor!");
        Console.WriteLine();

        // Check if already configured
        var existing       = AppConfig.Load();
        var existingTokens = TokenStore.Load();

        if (existing?.ServerUrl is not null && existingTokens is not null && !noPrompt) {
            Console.Write($"Already configured for {existing.ServerUrl} as {existingTokens.GitHubUsername}. Re-run setup? [y/N] ");
            var answer = Console.ReadLine()?.Trim().ToLowerInvariant();

            if (answer is not "y" and not "yes") {
                Console.WriteLine("Setup cancelled.");

                return 0;
            }
        }

        // Step 1: Server URL
        Console.WriteLine("Step 1/4: Server");
        string serverUrl;

        if (serverUrlArg is not null) {
            serverUrl = serverUrlArg;
            Console.WriteLine($"  Server URL: {serverUrl}");
        } else if (noPrompt) {
            await Console.Error.WriteLineAsync("  --server-url is required with --no-prompt");

            return 1;
        } else {
            Console.Write("  Enter your Capacitor server URL: ");
            serverUrl = Console.ReadLine()?.Trim() ?? "";

            if (string.IsNullOrEmpty(serverUrl)) {
                await Console.Error.WriteLineAsync("  Server URL is required.");

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
            Console.WriteLine($"✓ Reachable. Auth provider: {provider}");
        } catch (Exception ex) {
            await Console.Error.WriteLineAsync($"✗ Cannot reach server: {ex.Message}");

            return 1;
        }

        Console.WriteLine();

        // Step 2: Login
        Console.WriteLine("Step 2/4: Login");

        if (provider == "None") {
            Console.WriteLine("  Auth provider is None — no login required.");
        } else {
            var loginResult = await OAuthLoginFlow.LoginWithDiscoveryAsync(serverUrl);

            if (loginResult != 0) {
                await Console.Error.WriteLineAsync("  Login failed.");

                return 1;
            }

            var tokens = TokenStore.Load();
            Console.WriteLine($"  ✓ Logged in as {tokens?.GitHubUsername}");
        }

        Console.WriteLine();

        // Step 3: Claude Code hooks
        Console.WriteLine("Step 3/4: Claude Code Hooks");
        Console.WriteLine("  Kapacitor uses Claude Code hooks to capture session data.");
        Console.WriteLine();

        var pluginPath = ResolvePluginPath();

        if (noPrompt) {
            if (pluginPath is not null) {
                Console.WriteLine($"  Plugin path: {pluginPath}");
                Console.WriteLine("  Install hooks by running this inside Claude Code:");
                Console.WriteLine($"    /plugin install {pluginPath}");
            }
        } else {
            Console.Write("  Do you want to set up Claude Code hooks now? [Y/n] ");
            var hookAnswer = Console.ReadLine()?.Trim().ToLowerInvariant();

            if (hookAnswer is not "n" and not "no") {
                Console.WriteLine();
                Console.WriteLine("  Where should hooks be installed?");
                Console.WriteLine("    1) User settings   — all projects (~/.claude/settings.json)");
                Console.WriteLine("    2) Project settings — this repo only (.claude/settings.json)");

                if (pluginPath is not null) {
                    Console.WriteLine("    3) Plugin install   — full plugin with skills (recommended)");
                }

                Console.Write($"  Choose [{(pluginPath is not null ? "1-3" : "1-2")}]: ");
                var scopeAnswer = Console.ReadLine()?.Trim();

                switch (scopeAnswer) {
                    case "1": {
                        var settingsPath = Path.Combine(
                            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                            ".claude",
                            "settings.json"
                        );
                        var installed = await InstallHooksToSettingsAsync(settingsPath, pluginPath);

                        if (installed) {
                            Console.WriteLine($"  ✓ Hooks installed to {settingsPath}");
                        }

                        break;
                    }
                    case "2": {
                        var settingsPath = Path.Combine(
                            Directory.GetCurrentDirectory(),
                            ".claude",
                            "settings.json"
                        );
                        var installed = await InstallHooksToSettingsAsync(settingsPath, pluginPath);

                        if (installed) {
                            Console.WriteLine($"  ✓ Hooks installed to {settingsPath}");
                        }

                        break;
                    }
                    case "3" when pluginPath is not null: {
                        Console.WriteLine();
                        Console.WriteLine("  Run this inside Claude Code:");
                        Console.WriteLine();
                        Console.WriteLine($"    /plugin install {pluginPath}");
                        Console.WriteLine();
                        Console.WriteLine("  The plugin includes hooks + session recap/error skills.");

                        break;
                    }
                    default:
                        Console.WriteLine("  Skipped hook installation.");

                        break;
                }
            } else {
                Console.WriteLine("  Skipped. You can set up hooks later by re-running `kapacitor setup`.");
            }
        }

        Console.WriteLine();

        // Step 4: Daemon name + save
        Console.WriteLine("Step 4/4: Agent Daemon");

        var    defaultName = Environment.UserName.ToLowerInvariant();
        string daemonName;

        if (noPrompt) {
            daemonName = GetArg(args, "--daemon-name") ?? defaultName;
            Console.WriteLine($"  Daemon name: {daemonName}");
        } else {
            Console.Write($"  Daemon name [{defaultName}]: ");
            var input = Console.ReadLine()?.Trim();
            daemonName = string.IsNullOrEmpty(input) ? defaultName : input;
        }

        Console.WriteLine();

        // Save config
        var config = existing ?? new KapacitorConfig();

        config = config with {
            ServerUrl = serverUrl,
            Daemon = (config.Daemon ?? new DaemonSettings()) with { Name = daemonName }
        };
        AppConfig.Save(config);

        var finalTokens = TokenStore.Load();
        Console.WriteLine("Setup complete!");
        Console.WriteLine($"  ✓ Server:  {serverUrl}");
        Console.WriteLine($"  ✓ Daemon:  {daemonName}");

        if (finalTokens is not null) {
            Console.WriteLine($"  ✓ Auth:    {finalTokens.GitHubUsername} ({finalTokens.Provider})");
        }

        Console.WriteLine($"  Config saved to {AppConfig.GetConfigPath()}");
        Console.WriteLine();
        Console.WriteLine("  Optional: start the agent daemon with `kapacitor agent start -d`");

        return 0;
    }

    /// <summary>
    /// Writes kapacitor hooks into a Claude Code settings.json file,
    /// merging with any existing hooks without overwriting non-kapacitor entries.
    /// </summary>
    static async Task<bool> InstallHooksToSettingsAsync(string settingsPath, string? pluginPath) {
        try {
            // Read existing settings
            JsonObject settings;

            if (File.Exists(settingsPath)) {
                var json = await File.ReadAllTextAsync(settingsPath);
                settings = JsonNode.Parse(json)?.AsObject() ?? new JsonObject();
            } else {
                settings = new JsonObject();
            }

            var existingHooks = settings["hooks"]?.AsObject() ?? new JsonObject();

            // Resolve script paths from plugin directory
            string? persistScript   = ResolveScript(pluginPath, "persist-session-id.sh");
            string? titleScript     = ResolveScript(pluginPath, "set-title-prompt.sh");

            // Build kapacitor hook entries per event type
            MergeHookEvent(existingHooks, "SessionStart", BuildSessionStartHooks(persistScript));
            MergeHookEvent(existingHooks, "SessionEnd", [MakeHookGroup("kapacitor session-end", timeout: 15)]);
            MergeHookEvent(existingHooks, "SubagentStart", [MakeHookGroup("kapacitor subagent-start", timeout: 5)]);
            MergeHookEvent(existingHooks, "SubagentStop", [MakeHookGroup("kapacitor subagent-stop", timeout: 5)]);
            MergeHookEvent(existingHooks, "Notification", [MakeHookGroup("kapacitor notification", timeout: 5)]);
            MergeHookEvent(existingHooks, "Stop", [MakeHookGroup("kapacitor stop", timeout: 5)]);
            MergeHookEvent(existingHooks, "PermissionRequest", [MakeHookGroup("kapacitor permission-request", timeout: 36000)]);

            if (titleScript is not null) {
                MergeHookEvent(existingHooks, "UserPromptSubmit", [MakeHookGroup(titleScript, timeout: 2)]);
            }

            settings["hooks"] = existingHooks;

            // Write back with indentation
            var dir = Path.GetDirectoryName(settingsPath);

            if (dir is not null) Directory.CreateDirectory(dir);

            var options  = new JsonSerializerOptions { WriteIndented = true };
            var tempPath = settingsPath + ".tmp";
            await File.WriteAllTextAsync(tempPath, settings.ToJsonString(options));
            File.Move(tempPath, settingsPath, overwrite: true);

            return true;
        } catch (Exception ex) {
            await Console.Error.WriteLineAsync($"  ✗ Failed to install hooks: {ex.Message}");

            return false;
        }
    }

    static JsonArray BuildSessionStartHooks(string? persistScript) {
        var groups = new JsonArray { (JsonNode)MakeHookGroup("kapacitor session-start", timeout: 5, async_: true) };

        if (persistScript is not null) {
            groups.Add((JsonNode)MakeHookGroup(persistScript, timeout: 3));
        }

        return groups;
    }

    /// <summary>
    /// Merges kapacitor hook groups into an event's hook array,
    /// removing any previously-installed kapacitor hooks to avoid duplicates.
    /// Filters at the individual hook level so non-kapacitor hooks sharing
    /// a group with kapacitor hooks are preserved.
    /// </summary>
    internal static void MergeHookEvent(JsonObject allHooks, string eventName, JsonArray kapacitorGroups) {
        var existing = allHooks[eventName]?.AsArray() ?? [];

        // Remove previous kapacitor hooks from each group, preserving non-kapacitor hooks
        var filtered = new JsonArray();

        foreach (var group in existing) {
            if (group is null) continue;

            var hooks = group["hooks"]?.AsArray();

            // Skip malformed entries without a hooks array — preserving them
            // would mix flat objects with properly structured hook groups
            if (hooks is null) continue;

            // Keep only non-kapacitor hooks within this group
            var kept = new JsonArray();

            foreach (var h in hooks) {
                if (h is null) continue;

                var cmd = h["command"]?.GetValue<string>();

                if (cmd is not null && (cmd.StartsWith("kapacitor ") || cmd.Contains("persist-session-id") || cmd.Contains("set-title-prompt")))
                    continue;

                kept.Add(h.DeepClone());
            }

            // Only keep the group if it still has hooks after filtering
            if (kept.Count > 0) {
                var cleanGroup = group.DeepClone().AsObject();
                cleanGroup["hooks"] = kept;
                filtered.Add((JsonNode)cleanGroup);
            }
        }

        // Append new kapacitor groups
        foreach (var g in kapacitorGroups) {
            if (g is not null)
                filtered.Add(g.DeepClone());
        }

        allHooks[eventName] = filtered;
    }

    internal static JsonObject MakeHookGroup(string command, int timeout, bool async_ = false) {
        var hook = new JsonObject {
            ["type"]    = "command",
            ["command"] = command,
            ["timeout"] = timeout
        };

        if (async_) {
            hook["async"] = true;
        }

        return new JsonObject {
            ["hooks"] = new JsonArray { (JsonNode)hook }
        };
    }

    static string? ResolveScript(string? pluginPath, string scriptName) {
        if (pluginPath is null) return null;

        var path = Path.Combine(pluginPath, "hooks", scriptName);

        return File.Exists(path) ? path : null;
    }

    static string? ResolvePluginPath() {
        var exePath = Environment.ProcessPath;

        if (exePath is null) return null;

        var exeDir = Path.GetDirectoryName(exePath);

        if (exeDir is null) return null;

        // Try: <exe_dir>/../../kapacitor/plugin  (npm layout)
        var npmPluginPath = Path.GetFullPath(Path.Combine(exeDir, "..", "..", "kapacitor", "plugin"));

        if (Directory.Exists(npmPluginPath))
            return npmPluginPath;

        // Try: <exe_dir>/../plugin  (wrapper package direct layout)
        var wrapperPluginPath = Path.GetFullPath(Path.Combine(exeDir, "..", "plugin"));

        if (Directory.Exists(wrapperPluginPath))
            return wrapperPluginPath;

        // Try: repo root layout (dev mode)
        var repoPlugin = Path.GetFullPath(Path.Combine(exeDir, "..", "..", "plugin"));

        if (Directory.Exists(repoPlugin))
            return repoPlugin;

        return null;
    }

    static string? GetArg(string[] args, string name) {
        var idx = Array.IndexOf(args, name);

        return idx >= 0 && idx + 1 < args.Length ? args[idx + 1] : null;
    }
}
