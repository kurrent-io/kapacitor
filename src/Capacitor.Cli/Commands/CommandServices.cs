using Capacitor.Cli.Commands.Harness;
using Capacitor.Cli.Core;
using Capacitor.Cli.Core.Auth;
using Capacitor.Cli.Core.Config;
using Capacitor.Cli.Core.Harness;
using Capacitor.Cli.Core.Http;
using Microsoft.Extensions.DependencyInjection;

namespace Capacitor.Cli.Commands;

public static class CommandServices {
    /// <summary>
    /// The CLI's whole composition root, so the set of registrations a command is resolved against is
    /// one thing rather than a sequence a caller has to reproduce. <paramref name="baseUrl"/> is null
    /// until a profile resolves one, and the server is a factory so a command that never speaks HTTP
    /// does not pay for it.
    /// </summary>
    public static IServiceCollection AddCapacitorCli(
            this IServiceCollection services, ConfigRoot config, UserHome home, DaemonStore daemons,
            ProfileContext profiles, HookClock clock, string? baseUrl) {
        services
            .AddCapacitorContext(config, home, daemons, profiles)
            .AddCapacitorCommands();

        services.AddSingleton(clock);
        services.AddSingleton<IBrowserLauncher>(SystemBrowser.Instance);

        // Both probe the filesystem, and only a handful of commands take either, so they stay
        // factories: resolving a command that wants neither must not pay for them.
        services.AddSingleton(sp => HarnessRegistry.FromEnvironment(sp.GetRequiredService<UserHome>()));
        services.AddSingleton(sp => PluginEnvironment.FromProcess(
                sp.GetRequiredService<ProfileContext>().Snapshot,
                sp.GetRequiredService<UserHome>()));

        services.AddSingleton(_ => new CapacitorServer(baseUrl, config, profiles));
        services.AddCapacitorHttp();

        return services;
    }

    /// <summary>
    /// Registers every command the dispatch switch resolves. Commands are transient: a run
    /// dispatches one, and holding them would keep a command's own state alive past its verb.
    /// </summary>
    public static IServiceCollection AddCapacitorCommands(this IServiceCollection services) {
        services.AddTransient<AgentCommand>();
        services.AddTransient<CleanupCommand>();
        services.AddTransient<ConfigCommand>();
        services.AddTransient<CurateCommand>();
        services.AddTransient<DaemonCommands>();
        services.AddTransient<ErrorsCommand>();
        services.AddTransient<EvalCommand>();
        services.AddTransient<FeedbackCommand>();
        services.AddTransient<HarnessCommand>();
        services.AddTransient<IgnoreCommand>();
        services.AddTransient<ImportCommand>();
        services.AddTransient<LoginCommand>();
        services.AddTransient<MachineCommand>();
        services.AddTransient<PluginCommand>();
        services.AddTransient<ProfileCommand>();
        services.AddTransient<ProjectsCommand>();
        services.AddTransient<RecapCommand>();
        services.AddTransient<RemapCommand>();
        services.AddTransient<ReportVersionCommand>();
        services.AddTransient<ReposCommand>();
        services.AddTransient<ReviewCommand>();
        services.AddTransient<SessionsCommand>();
        services.AddTransient<SetupCommand>();
        services.AddTransient<SkillsCommand>();
        services.AddTransient<StatusCommand>();
        services.AddTransient<McpFlowResultServer>();
        services.AddTransient<McpFlowsServer>();
        services.AddTransient<McpMemoryServer>();
        services.AddTransient<McpSessionsServer>();
        services.AddTransient<McpWorkItemsServer>();
        services.AddTransient<McpAnalyticsServer>();
        services.AddTransient<McpReviewServer>();
        services.AddTransient<McpJudgeServer>();
        services.AddTransient<UninstallCommand>();
        services.AddTransient<UpdateCommand>();
        services.AddTransient<UseCommand>();
        services.AddTransient<ValidatePlanCommand>();
        services.AddTransient<WatchCommand>();
        services.AddTransient<WhatsDoneCommand>();
        services.AddTransient<WhoamiCommand>();

        services.AddTransient<AntigravityHookCommand>();
        services.AddTransient<ClaudeHookCommand>();
        services.AddTransient<CodexHookCommand>();
        services.AddTransient<CopilotFinalizeDrainCommand>();
        services.AddTransient<CopilotHookCommand>();
        services.AddTransient<CursorHookCommand>();
        services.AddTransient<GeminiHookCommand>();
        services.AddTransient<KiroHookCommand>();
        services.AddTransient<OpenCodeHookCommand>();
        services.AddTransient<PiHookCommand>();

        return services;
    }
}
