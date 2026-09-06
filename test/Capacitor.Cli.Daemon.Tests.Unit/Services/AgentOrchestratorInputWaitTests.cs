using Capacitor.Cli.Daemon.Services;
using Capacitor.Cli.Daemon.Tests.Unit.Pty;

namespace Capacitor.Cli.Daemon.Tests.Unit.Services;

/// The orchestrator's half of the input-wait relay: the bridge's attributed verdict lands on the
/// live agent's own clock, and one for an agent it does not hold is dropped.
public class AgentOrchestratorInputWaitTests {
    [Test]
    public async Task A_relayed_verdict_moves_the_attributed_agents_flag() {
        await using var orch  = AgentOrchestratorHarness.BuildOrchestrator(new CaptureServerConnection(), new SpyPtyProcessFactory(), new Dictionary<string, IHostedAgentLauncher>());
        var             agent = orch.SeedAgentForTest("relay-1", pty: new RecordingPtyProcess());
        var             relay = orch.PermissionBridgeForTest.InputWaitHandler!;

        relay(agent.Id, true);
        await Assert.That(agent.ActivityClock.AwaitingInput).IsTrue();

        relay(agent.Id, false);
        await Assert.That(agent.ActivityClock.AwaitingInput).IsFalse();
    }

    [Test]
    public async Task A_verdict_for_an_agent_the_daemon_does_not_hold_is_dropped() {
        await using var orch  = AgentOrchestratorHarness.BuildOrchestrator(new CaptureServerConnection(), new SpyPtyProcessFactory(), new Dictionary<string, IHostedAgentLauncher>());
        var             agent = orch.SeedAgentForTest("relay-2", pty: new RecordingPtyProcess());

        orch.PermissionBridgeForTest.InputWaitHandler!("somebody-else", true);

        await Assert.That(agent.ActivityClock.AwaitingInput).IsFalse();
    }
}
