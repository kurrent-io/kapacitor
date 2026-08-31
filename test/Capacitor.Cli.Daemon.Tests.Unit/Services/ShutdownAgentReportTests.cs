using Capacitor.Cli.Core;
using Capacitor.Cli.Daemon.Services;
using Capacitor.Cli.Daemon.Tests.Unit.Pty;

namespace Capacitor.Cli.Daemon.Tests.Unit.Services;

/// <summary>
/// What a daemon says about its agents on the way out. The transport dropping tells the server a
/// daemon went away; it says nothing about the agents it was hosting, and a successor reconnecting
/// under the same name re-binds those retained entries onto its own connection — leaving sessions
/// the surface still composes into and a Stop with nothing to act on.
/// </summary>
public class ShutdownAgentReportTests {
    [Test]
    public async Task Disposing_reports_every_live_agent_ended_with_a_shutdown_reason() {
        var server = new CaptureServerConnection();
        var orch   = AgentOrchestratorHarness.BuildOrchestrator(
            server, new SpyPtyProcessFactory(), new Dictionary<string, IHostedAgentLauncher>());

        orch.SeedAgentForTest("shutdown-1", status: "Running");
        orch.SeedAgentForTest("shutdown-2", status: "Running");

        await orch.DisposeAsync();

        foreach (var id in new[] { "shutdown-1", "shutdown-2" }) {
            await Assert.That(server.StatusChangedCalls).Contains((id, "Completed"));
            await Assert.That(server.AgentUnregisteredCalls).Contains(id);
            await Assert.That(server.RunEvents.Where(e => e.AgentId == id)
                .Select(e => e.Event).OfType<AgentRunStopped>().Select(e => e.Reason))
                .Contains(AgentOrchestrator.DaemonShutdownStopReason);
        }

        await Assert.That(server.EndSessionReasons)
            .Contains(AgentOrchestrator.DaemonShutdownStopReason);
    }

    /// <summary>A private agent was never registered, so there is nothing to end and nobody to tell.</summary>
    [Test]
    public async Task A_private_agent_is_not_reported() {
        var server = new CaptureServerConnection();
        var orch   = AgentOrchestratorHarness.BuildOrchestrator(
            server, new SpyPtyProcessFactory(), new Dictionary<string, IHostedAgentLauncher>());

        orch.SeedAgentForTest("private-1", status: "Running", isPrivate: true);

        await orch.DisposeAsync();

        await Assert.That(server.StatusChangedCalls.Any(c => c.AgentId == "private-1")).IsFalse();
        await Assert.That(server.AgentUnregisteredCalls).DoesNotContain("private-1");
    }

    /// <summary>A server that has stopped answering must not hold a shutdown open: the budget is a
    /// ceiling on the whole pass, and teardown continues past it either way.</summary>
    [Test]
    public async Task A_hung_server_does_not_hold_shutdown_past_the_budget() {
        using var blockForever = new CancellationTokenSource();
        var server = new CaptureServerConnection { EndSessionBlockUntil = blockForever };
        var orch   = AgentOrchestratorHarness.BuildOrchestrator(
            server, new SpyPtyProcessFactory(), new Dictionary<string, IHostedAgentLauncher>());

        orch.SeedAgentForTest("hung-1", status: "Running");

        var started = DateTime.UtcNow;
        await orch.DisposeAsync();
        var elapsed = DateTime.UtcNow - started;

        await Assert.That(elapsed).IsLessThan(AgentOrchestrator.ShutdownReportBudget + TimeSpan.FromSeconds(15));
    }
}
