using System.Text.Json;
using System.Text.Json.Nodes;
using kapacitor.Auth;
using kapacitor.Config;
using Spectre.Console;
using Profile = kapacitor.Config.Profile;

namespace kapacitor.Commands;

public static class SetupCommand {
    public static async Task<int> HandleAsync(string[] args) {
        var serverUrlArg = GetArg(args, "--server-url");
        var noPrompt     = args.Contains("--no-prompt");

        AnsiConsole.Write(new Rule("[bold green]Welcome to Kapacitor[/]").Centered());

        // Check if already configured
        var existingProfile = await AppConfig.LoadProfileConfig();
        var existing        = existingProfile.Profiles.GetValueOrDefault("default");
        var existingTokens  = await TokenStore.LoadAsync();

        if (existing?.ServerUrl is not null && existingTokens is not null && !noPrompt) {
            var rerun = AnsiConsole.Prompt(
                new ConfirmationPrompt($"Already configured for [cyan]{Markup.Escape(existing.ServerUrl)}[/] as [cyan]{Markup.Escape(existingTokens.GitHubUsername ?? "?")}[/]. Re-run setup?")
                    { DefaultValue = false });

            if (!rerun) {
                AnsiConsole.MarkupLine("[dim]Setup cancelled.[/]");

                return 0;
            }
        }

        // Step 1: Server URL
        AnsiConsole.Write(new Rule("[yellow]Step 1/5 — Server[/]").LeftJustified());
        string serverUrl;

        if (serverUrlArg is not null) {
            serverUrl = serverUrlArg;
            await Console.Out.WriteLineAsync($"  Server URL: {serverUrl}");
        } else if (noPrompt) {
            await Console.Error.WriteLineAsync("  --server-url is required with --no-prompt");

            return 1;
        } else {
            serverUrl = AnsiConsole.Prompt(
                new TextPrompt<string>("Capacitor server URL:")
                    .Validate(u => !string.IsNullOrWhiteSpace(u)
                        ? ValidationResult.Success()
                        : ValidationResult.Error("[red]URL cannot be empty[/]")));
        }

        // Normalize: strip trailing slashes to avoid double-slash URLs
        serverUrl = AppConfig.NormalizeUrl(serverUrl);

        // Validate server reachability
        string provider;

        try {
            provider = await AnsiConsole.Status()
                .Spinner(Spinner.Known.Dots)
                .StartAsync("Checking server…", async _ =>
                    await HttpClientExtensions.DiscoverProviderAsync(serverUrl));

            AnsiConsole.MarkupLine($"  [green]✓[/] Reachable · auth provider: [cyan]{Markup.Escape(provider)}[/]");
        } catch (Exception ex) {
            AnsiConsole.MarkupLine($"  [red]✗[/] Cannot reach server: {Markup.Escape(ex.Message)}");

            return 1;
        }

        await Console.Out.WriteLineAsync();

        // Step 2: Login
        AnsiConsole.Write(new Rule("[yellow]Step 2/5 — Login[/]").LeftJustified());

        if (provider == "None") {
            await Console.Out.WriteLineAsync("  Auth provider is None — no login required.");
        } else {
            var loginResult = await OAuthLoginFlow.LoginWithDiscoveryAsync(serverUrl);

            if (loginResult != 0) {
                await Console.Error.WriteLineAsync("  Login failed.");

                return 1;
            }

            var tokens = await TokenStore.LoadAsync();
            await Console.Out.WriteLineAsync($"  ✓ Logged in as {tokens?.GitHubUsername}");
        }

        await Console.Out.WriteLineAsync();

        // Step 3: Default session visibility
        AnsiConsole.Write(new Rule("[yellow]Step 3/5 — Default session visibility[/]").LeftJustified());

        string defaultVisibility;

        if (noPrompt) {
            defaultVisibility = (GetArg(args, "--default-visibility") ?? "org_public").ToLowerInvariant();

            if (defaultVisibility is not "private" and not "org_public" and not "public") {
                await Console.Error.WriteLineAsync($"  Invalid default-visibility: {defaultVisibility}. Must be: private, org_public, or public");

                return 1;
            }

            await Console.Out.WriteLineAsync($"  Default visibility: {defaultVisibility}");
        } else {
            defaultVisibility = AnsiConsole.Prompt(
                new SelectionPrompt<string>()
                    .Title("How should your sessions be visible to others by default?")
                    .AddChoices("org_public", "private", "public")
                    .UseConverter(v => v switch {
                        "private"    => "All private — only you can see your sessions",
                        "org_public" => "Org repos public, others private (default)",
                        "public"     => "All public — everyone can see all your sessions",
                        _            => v
                    }));
        }

        await Console.Out.WriteLineAsync();

        // Step 4: Claude Code plugin
        AnsiConsole.Write(new Rule("[yellow]Step 4/5 — Claude Code Plugin[/]").LeftJustified());
        await Console.Out.WriteLineAsync("  The Kapacitor plugin provides hooks, skills, and collaborative memory.");
        await Console.Out.WriteLineAsync();

        var pluginPath = ResolvePluginPath();

        if (pluginPath is not null) {
            // The plugin directory itself is the marketplace root (single-plugin marketplace)
            var marketplacePath = pluginPath;

            string pluginScope;

            if (noPrompt) {
                pluginScope = GetArg(args, "--plugin-scope") ?? "user";
                await Console.Out.WriteLineAsync($"  Plugin scope: {pluginScope}");
            } else {
                pluginScope = AnsiConsole.Prompt(
                    new SelectionPrompt<string>()
                        .Title("Where should the plugin be installed?")
                        .AddChoices("user", "project", "skip")
                        .UseConverter(v => v switch {
                            "user"    => "User-wide — all Claude Code sessions (recommended)",
                            "project" => "This project only",
                            "skip"    => "Skip — I'll install it manually",
                            _         => v
                        }));
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
        AnsiConsole.Write(new Rule("[yellow]Step 5/5 — Agent Daemon[/]").LeftJustified());

        var    defaultName = Environment.UserName.ToLowerInvariant();
        string daemonName;

        if (noPrompt) {
            daemonName = GetArg(args, "--daemon-name") ?? defaultName;
            await Console.Out.WriteLineAsync($"  Daemon name: {daemonName}");
        } else {
            daemonName = AnsiConsole.Prompt(
                new TextPrompt<string>("Daemon name:")
                    .DefaultValue(defaultName)
                    .ShowDefaultValue());
        }

        await Console.Out.WriteLineAsync();

        // Save config
        var profileConfig  = await AppConfig.LoadProfileConfig();
        var defaultProfile = profileConfig.Profiles.GetValueOrDefault("default") ?? new Profile();

        defaultProfile = defaultProfile with {
            ServerUrl = serverUrl,
            DefaultVisibility = defaultVisibility,
            Daemon = (defaultProfile.Daemon ?? new DaemonSettings()) with { Name = daemonName }
        };

        var profiles = new Dictionary<string, Profile>(profileConfig.Profiles) {
            ["default"] = defaultProfile
        };
        profileConfig = profileConfig with { Profiles = profiles };
        await AppConfig.SaveProfileConfig(profileConfig);

        var finalTokens = await TokenStore.LoadAsync();
        AnsiConsole.Write(new Rule("[green]Setup complete[/]").LeftJustified());

        var grid = new Grid().AddColumn().AddColumn();
        grid.AddRow("[bold]Server[/]",     Markup.Escape(serverUrl));
        grid.AddRow("[bold]Visibility[/]", Markup.Escape(defaultVisibility));
        grid.AddRow("[bold]Daemon[/]",     Markup.Escape(daemonName));

        if (finalTokens is not null) {
            grid.AddRow("[bold]Auth[/]", Markup.Escape($"{finalTokens.GitHubUsername} ({finalTokens.Provider})"));
        }

        grid.AddRow("[bold]Config[/]", Markup.Escape(AppConfig.GetConfigPath()));

        AnsiConsole.Write(grid);
        AnsiConsole.MarkupLine("\n[dim]Optional:[/] start the agent daemon with [cyan]kapacitor agent start -d[/]");

        return 0;
    }

    internal static string? ResolvePluginPath() {
        var exePath = Environment.ProcessPath;

        if (exePath is null) return null;

        var exeDir = Path.GetDirectoryName(exePath);

        if (exeDir is null) return null;

        // Try: <exe_dir>/../../../../plugin  (npm optional-deps layout)
        // Binary is at <wrapper>/node_modules/@kurrent/<platform-pkg>/bin/kapacitor
        // Plugin is at <wrapper>/plugin
        var optDepsPluginPath = Path.GetFullPath(Path.Combine(exeDir, "..", "..", "..", "..", "kapacitor"));

        if (Directory.Exists(optDepsPluginPath))
            return optDepsPluginPath;

        // Try: <exe_dir>/../../kapacitor/plugin  (npm flat layout)
        var npmPluginPath = Path.GetFullPath(Path.Combine(exeDir, "..", "..", "kapacitor", "kapacitor"));

        if (Directory.Exists(npmPluginPath))
            return npmPluginPath;

        // Try: <exe_dir>/../plugin  (wrapper package direct layout)
        var wrapperPluginPath = Path.GetFullPath(Path.Combine(exeDir, "..", "kapacitor"));

        if (Directory.Exists(wrapperPluginPath))
            return wrapperPluginPath;

        // Try: repo root layout (dev mode)
        var repoPlugin = Path.GetFullPath(Path.Combine(exeDir, "..", "..", "kapacitor"));

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

            // Ensure extraKnownMarketplaces.kapacitor exists with the correct path
            if (root["extraKnownMarketplaces"] is not JsonObject marketplaces) {
                marketplaces                   = [];
                root["extraKnownMarketplaces"] = marketplaces;
            }

            marketplaces["kapacitor"] = new JsonObject {
                ["source"] = new JsonObject {
                    ["source"] = "directory",
                    ["path"]   = marketplacePath
                }
            };

            // Remove stale kurrent marketplace entry if present
            marketplaces.Remove("kurrent");

            // Ensure enabledPlugins.kapacitor@kapacitor is true
            if (root["enabledPlugins"] is not JsonObject enabled) {
                enabled                = [];
                root["enabledPlugins"] = enabled;
            }

            enabled["kapacitor@kapacitor"] = true;

            // Remove stale plugin entry if present
            enabled.Remove("kapacitor@kurrent");

            Directory.CreateDirectory(Path.GetDirectoryName(settingsPath)!);
            File.WriteAllText(settingsPath, root.ToJsonString(WriteOpts));

            return true;
        } catch {
            return false;
        }
    }
}
