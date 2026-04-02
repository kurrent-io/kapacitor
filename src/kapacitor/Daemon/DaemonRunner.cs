using kapacitor.Auth;
using kapacitor.Config;
using kapacitor.Daemon.Pty;
using kapacitor.Daemon.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace kapacitor.Daemon;

public static class DaemonRunner {
    public static async Task<int> RunAsync(string[] args) {
        var builder = Host.CreateApplicationBuilder(args);

        builder.Logging.AddSimpleConsole(opts => {
            opts.TimestampFormat = "yyyy-MM-dd HH:mm:ss.fff ";
            opts.UseUtcTimestamp = false;
        });

        var config = new DaemonConfig();

        // Resolve server URL from AppConfig
        var serverUrl = AppConfig.ResolvedServerUrl;

        // CLI arg overrides for daemon-specific settings
        for (var i = 0; i < args.Length - 1; i++) {
            switch (args[i]) {
                case "--name":       config.Name                = args[++i]; break;
                case "--server":     config.ServerUrl           = args[++i]; break;
                case "--server-url": config.ServerUrl           = args[++i]; break;
                case "--max-agents" when int.TryParse(args[i + 1], out var n) && n >= 1:
                    config.MaxConcurrentAgents = n;
                    i++;
                    break;
                case "--max-agents":
                    Console.Error.WriteLine($"Invalid --max-agents value: {args[i + 1]} (must be a positive integer)");
                    return 1;
            }
        }

        // If server URL wasn't set by CLI arg, use resolved URL
        if (string.IsNullOrEmpty(config.ServerUrl) && serverUrl is not null) {
            config.ServerUrl = serverUrl;
        }

        // Env var overrides
        if (Environment.GetEnvironmentVariable("KAPACITOR_URL") is { } envUrl && string.IsNullOrEmpty(config.ServerUrl)) {
            config.ServerUrl = envUrl;
        }

        if (Environment.GetEnvironmentVariable("KAPACITOR_DAEMON_NAME") is { } name) {
            config.Name = name;
        }

        if (Environment.GetEnvironmentVariable("KAPACITOR_MAX_AGENTS") is { } maxAgents) {
            if (int.TryParse(maxAgents, out var n) && n >= 1)
                config.MaxConcurrentAgents = n;
            else
                Console.Error.WriteLine($"Warning: ignoring invalid KAPACITOR_MAX_AGENTS={maxAgents}");
        }

        // Also load daemon settings from config file
        var appConfig = AppConfig.Load();
        if (appConfig?.Daemon is { } daemonSettings) {
            if (string.IsNullOrEmpty(config.Name) && !string.IsNullOrEmpty(daemonSettings.Name))
                config.Name = daemonSettings.Name;
            if (config.MaxConcurrentAgents == 5 && daemonSettings.MaxAgents != 5)
                config.MaxConcurrentAgents = daemonSettings.MaxAgents;
        }

        var errors = config.Validate();
        if (errors.Count > 0) {
            Console.Error.WriteLine("Configuration errors:");
            foreach (var e in errors) {
                Console.Error.WriteLine($"  - {e}");
            }
            return 1;
        }

        builder.Services.AddSingleton(config);
        builder.Services.AddSingleton<ServerConnection>();

        if (OperatingSystem.IsWindows()) {
            builder.Services.AddSingleton<IPtyProcessFactory, kapacitor.Daemon.Pty.Windows.WinPtyProcessFactory>();
        } else {
            builder.Services.AddSingleton<IPtyProcessFactory, kapacitor.Daemon.Pty.Unix.UnixPtyProcessFactory>();
        }

        builder.Services.AddSingleton<WorktreeManager>();
        builder.Services.AddHttpClient("Attachments",
            client => client.BaseAddress = new Uri(config.ServerUrl));
        builder.Services.AddSingleton<AgentOrchestrator>();

        var host   = builder.Build();
        var logger = host.Services.GetRequiredService<ILoggerFactory>().CreateLogger("kapacitor.Daemon");
        logger.LogInformation("kapacitor agent '{Name}' starting, connecting to {ServerUrl}",
            config.Name, config.ServerUrl);

        var lifetime   = host.Services.GetRequiredService<IHostApplicationLifetime>();
        var connection = host.Services.GetRequiredService<ServerConnection>();
        await connection.ConnectAsync(lifetime.ApplicationStopping);

        var worktreeManager = host.Services.GetRequiredService<WorktreeManager>();
        await worktreeManager.CleanupOrphanedAsync();

        await host.RunAsync();

        var orchestrator = host.Services.GetRequiredService<AgentOrchestrator>();
        await orchestrator.DisposeAsync();
        await connection.DisposeAsync();

        return 0;
    }
}
