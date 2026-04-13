using kapacitor.Config;
using kapacitor.Daemon.Pty;
using kapacitor.Daemon.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace kapacitor.Daemon;

public static partial class DaemonRunner {
    public static readonly string LogPath = PathHelpers.ConfigPath("agent.log");

    public static async Task<int> RunAsync(string[] args) {
        string? logFile = null;
        var     config  = new DaemonConfig();

        // Resolve server URL from AppConfig
        var serverUrl = AppConfig.ResolvedServerUrl;

        // CLI arg overrides for daemon-specific settings — parse before host builder
        for (var i = 0; i < args.Length - 1; i++) {
            switch (args[i]) {
                case "--name": config.Name = args[++i]; break;
                case "--server":
                case "--server-url": config.ServerUrl = args[++i]; break;
                case "--log-file": logFile = args[++i]; break;
                case "--max-agents" when int.TryParse(args[i + 1], out var n) && n >= 1:
                    config.MaxConcurrentAgents = n;
                    i++;

                    break;
                case "--max-agents":
                    await Console.Error.WriteLineAsync($"Invalid --max-agents value: {args[i + 1]} (must be a positive integer)");

                    return 1;
            }
        }

        // Strip our custom args before passing to host builder
        var hostArgs = Array.Empty<string>();
        var builder  = Host.CreateApplicationBuilder(hostArgs);

        // Configure logging: file when detached, console when foreground
        builder.Logging.ClearProviders();

        if (logFile is not null) {
            builder.Logging.AddProvider(new RollingFileLoggerProvider(logFile));
        } else {
            builder.Logging.AddSimpleConsole(opts => {
                    opts.TimestampFormat = "yyyy-MM-dd HH:mm:ss.fff ";
                    opts.UseUtcTimestamp = false;
                }
            );
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
                await Console.Error.WriteLineAsync($"Warning: ignoring invalid KAPACITOR_MAX_AGENTS={maxAgents}");
        }

        // Also load daemon settings from config file
        var appConfig = await AppConfig.Load();

        if (appConfig?.Daemon is { } daemonSettings) {
            if (string.IsNullOrEmpty(config.Name) && !string.IsNullOrEmpty(daemonSettings.Name))
                config.Name = daemonSettings.Name;

            if (config.MaxConcurrentAgents == 5 && daemonSettings.MaxAgents != 5)
                config.MaxConcurrentAgents = daemonSettings.MaxAgents;
        }

        // Fall back to OS username, then machine name, then a static default
        if (string.IsNullOrEmpty(config.Name)) {
            var userName = Environment.UserName;

            config.Name = !string.IsNullOrEmpty(userName)
                ? userName.ToLowerInvariant()
                : !string.IsNullOrEmpty(Environment.MachineName)
                    ? Environment.MachineName.ToLowerInvariant()
                    : "daemon";
        }

        var errors = config.Validate();

        if (errors.Count > 0) {
            await Console.Error.WriteLineAsync("Configuration errors:");

            foreach (var e in errors) {
                await Console.Error.WriteLineAsync($"  - {e}");
            }

            return 1;
        }

        builder.Services.AddSingleton(config);
        builder.Services.AddSingleton<ServerConnection>();

        if (OperatingSystem.IsWindows()) {
            builder.Services.AddSingleton<IPtyProcessFactory, Pty.Windows.WinPtyProcessFactory>();
        } else {
            builder.Services.AddSingleton<IPtyProcessFactory, Pty.Unix.UnixPtyProcessFactory>();
        }

        builder.Services.AddSingleton<WorktreeManager>();

        builder.Services.AddHttpClient("Attachments", client => client.BaseAddress = new Uri(config.ServerUrl));
        builder.Services.AddSingleton<AgentOrchestrator>();
        builder.Services.AddSingleton<EvalRunner>();

        var host   = builder.Build();
        var logger = host.Services.GetRequiredService<ILoggerFactory>().CreateLogger("kapacitor.Daemon");

        LogDaemonStarting(logger, config.Name, config.ServerUrl);

        var lifetime   = host.Services.GetRequiredService<IHostApplicationLifetime>();
        var connection = host.Services.GetRequiredService<ServerConnection>();
        await connection.ConnectAsync(lifetime.ApplicationStopping);

        var worktreeManager = host.Services.GetRequiredService<WorktreeManager>();
        await worktreeManager.CleanupOrphanedAsync();

        var orchestrator = host.Services.GetRequiredService<AgentOrchestrator>();
        // Instantiate EvalRunner so it subscribes to OnRunEval on the
        // ServerConnection. It's stateless beyond the subscription, so no
        // disposal dance is needed.
        _ = host.Services.GetRequiredService<EvalRunner>();

        try {
            await host.RunAsync();
        } finally {
            await orchestrator.DisposeAsync();
            await connection.DisposeAsync();
        }

        return 0;
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "kapacitor agent '{Name}' starting, connecting to {ServerUrl}")]
    static partial void LogDaemonStarting(ILogger logger, string name, string serverUrl);
}
