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
        Console.WriteLine("Step 1/5: Server");
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
        Console.WriteLine("Step 2/5: Login");

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

        // Step 3: Plugin
        Console.WriteLine("Step 3/5: Claude Code Plugin");

        var pluginPath = ResolvePluginPath();

        if (pluginPath is not null) {
            Console.WriteLine("  To complete setup, run this inside Claude Code:");
            Console.WriteLine();
            Console.WriteLine($"    /plugin install {pluginPath}");
            Console.WriteLine();

            if (!noPrompt) {
                Console.WriteLine("  Waiting for first hook to arrive...");
                Console.WriteLine("  (Start a Claude Code session after installing the plugin)");

                var verified = await WaitForHookAsync(serverUrl);

                if (verified) {
                    Console.WriteLine("  ✓ Hook received! Plugin is working.");
                } else {
                    Console.WriteLine("  ⏱ Timed out. You can verify later with `kapacitor status`.");
                }
            }
        } else {
            Console.WriteLine("  Could not determine plugin path. Install manually from the npm package.");
        }

        Console.WriteLine();

        // Step 4: Daemon name
        Console.WriteLine("Step 4/5: Agent Daemon");

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

        // Step 5: Save config
        Console.WriteLine("Step 5/5: Done");

        var config = existing ?? new KapacitorConfig();

        config = config with {
            ServerUrl = serverUrl,
            Daemon = (config.Daemon ?? new DaemonSettings()) with { Name = daemonName }
        };
        AppConfig.Save(config);

        var finalTokens = TokenStore.Load();
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

    static async Task<bool> WaitForHookAsync(string serverUrl) {
        using var client   = await HttpClientExtensions.CreateAuthenticatedClientAsync(serverUrl);
        var       timeout  = TimeSpan.FromMinutes(5);
        var       interval = TimeSpan.FromSeconds(5);
        var       elapsed  = TimeSpan.Zero;

        while (elapsed < timeout) {
            try {
                var resp = await client.GetAsync($"{serverUrl}/api/sessions?limit=1");

                if (resp.IsSuccessStatusCode) {
                    var body = await resp.Content.ReadAsStringAsync();

                    if (body.Contains("\"id\"")) {
                        return true;
                    }
                }
            } catch {
                // Ignore — keep polling
            }

            await Task.Delay(interval);
            elapsed += interval;
        }

        return false;
    }

    static string? GetArg(string[] args, string name) {
        var idx = Array.IndexOf(args, name);

        return idx >= 0 && idx + 1 < args.Length ? args[idx + 1] : null;
    }
}
