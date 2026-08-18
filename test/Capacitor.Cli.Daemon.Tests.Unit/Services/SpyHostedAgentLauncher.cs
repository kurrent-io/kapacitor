using Capacitor.Cli.Daemon.Services;

namespace Capacitor.Cli.Daemon.Tests.Unit.Services;

sealed class SpyHostedAgentLauncher(string vendor, string cliPath) : IHostedAgentLauncher {
    public string Vendor             { get; }       = vendor;
    public string CliPath            { get; }       = cliPath;
    public bool   SupportsUnattended { get; init; } = true;

    public int        PrepareCalls   { get; private set; }
    public int        BuildArgsCalls { get; private set; }
    public int        CleanupCalls   { get; private set; }
    public Exception? PrepareThrow   { get; init; }

    public bool IsAvailable() => true;

    public void Prepare(LauncherContext ctx) {
        PrepareCalls++;

        if (PrepareThrow is not null) throw PrepareThrow;
    }

    public LaunchArgs BuildArgs(LauncherContext ctx) {
        BuildArgsCalls++;

        return new LaunchArgs(Args: [], McpConfigPath: null);
    }

    public LaunchArgs BuildPassthrough(LauncherContext ctx, IReadOnlyList<string> userArgs) {
        BuildArgsCalls++;

        return new LaunchArgs(Args: [.. userArgs], McpConfigPath: null);
    }

    public void Cleanup(AgentInstance agent) {
        CleanupCalls++;
    }
}
