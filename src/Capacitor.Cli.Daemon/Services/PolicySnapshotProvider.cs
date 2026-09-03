using Capacitor.Cli.Core;
using Capacitor.Cli.Core.Policy;

namespace Capacitor.Cli.Daemon.Services;

/// <summary>Binds the repo and user approval policies for one hosted launch. The repo scope reads
/// the worktree checkout the agent will actually run in, not the source repo.</summary>
internal sealed class PolicySnapshotProvider(ConfigRoot config) {
    public PolicySnapshot BuildFor(string repoPath) => PolicySnapshotBuilder.Build(repoPath, config);
}
