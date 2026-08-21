using Capacitor.Cli.Core;
using Capacitor.Cli.Daemon.Services;
using Capacitor.Cli.Daemon.Tests.Unit.Pty;

namespace Capacitor.Cli.Daemon.Tests.Unit.Services;

/// <summary>
/// Phase B (D2): <see cref="AgentOrchestrator.BuildLiveAgents"/> reflects each live agent's
/// kind + flow identity and mirrors the <c>GetLiveAgentIds</c> filter (Starting/Running, non-private).
/// Uses <see cref="AgentOrchestratorHarness"/> for its
/// <c>BuildOrchestrator</c> + <c>CaptureServerConnection</c> + <c>SpyPtyProcessFactory</c> doubles.
/// </summary>
public class LiveAgentsSnapshotTests {
    [Test]
    public async Task BuildLiveAgents_carries_reviewflow_identity_and_excludes_stopped_and_private() {
        await using var orch = AgentOrchestratorHarness.BuildOrchestrator(
            new CaptureServerConnection(), new SpyPtyProcessFactory(), new Dictionary<string, IHostedAgentLauncher>());

        orch.SeedAgentForTest("a1", LaunchKind.ReviewFlow, status: "Running", flowRunId: "flow-1", flowRole: "reviewer");
        orch.SeedAgentForTest("a2", LaunchKind.Default,    status: "Stopped");
        orch.SeedAgentForTest("a3", LaunchKind.Default,    status: "Running", isPrivate: true);

        var live = orch.BuildLiveAgents();

        await Assert.That(live.Select(x => x.Id)).IsEquivalentTo(new[] { "a1" });
        await Assert.That(live[0].Kind).IsEqualTo("ReviewFlow");
        await Assert.That(live[0].FlowRunId).IsEqualTo("flow-1");
        await Assert.That(live[0].FlowRole).IsEqualTo("reviewer");
    }
}
