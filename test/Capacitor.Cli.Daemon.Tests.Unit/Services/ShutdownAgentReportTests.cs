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

        foreach (var id in new[] { "shutdown-1", "shutdown-2" })
            await Assert.That(server.ShutdownReports)
                .Contains((id, "Failed", AgentOrchestrator.DaemonShutdownStopReason, false));
    }

    /// <summary>The token is the whole point: every other method on the real connection bakes in
    /// ApplicationStopping, which is cancelled before this teardown runs, so a report made on it
    /// would send nothing and report success for having done so.</summary>
    [Test]
    public async Task The_report_is_made_on_a_token_that_is_still_live() {
        var server = new CaptureServerConnection();
        var orch   = AgentOrchestratorHarness.BuildOrchestrator(
            server, new SpyPtyProcessFactory(), new Dictionary<string, IHostedAgentLauncher>());

        orch.SeedAgentForTest("live-token-1", status: "Running");

        await orch.DisposeAsync();

        await Assert.That(server.ShutdownReports).IsNotEmpty();
        await Assert.That(server.ShutdownReports.All(r => !r.TokenAlreadyCancelled)).IsTrue();
    }

    /// <summary>An agent whose own finalizer already ran has reported its real ending; a second stop
    /// event over the top of it would be less true, and its unregister races that finalizer's.</summary>
    [Test]
    public async Task An_already_finalized_agent_is_not_reported_again() {
        var server = new CaptureServerConnection();
        var orch   = AgentOrchestratorHarness.BuildOrchestrator(
            server, new SpyPtyProcessFactory(), new Dictionary<string, IHostedAgentLauncher>());

        orch.SeedAgentForTest("done-1", status: "Completed");

        await orch.DisposeAsync();

        await Assert.That(server.ShutdownReports.Any(r => r.AgentId == "done-1")).IsFalse();
    }

    /// <summary>A private agent was never registered, so there is nothing to end and nobody to tell.</summary>
    [Test]
    public async Task A_private_agent_is_not_reported() {
        var server = new CaptureServerConnection();
        var orch   = AgentOrchestratorHarness.BuildOrchestrator(
            server, new SpyPtyProcessFactory(), new Dictionary<string, IHostedAgentLauncher>());

        orch.SeedAgentForTest("private-1", status: "Running", isPrivate: true);

        await orch.DisposeAsync();

        await Assert.That(server.ShutdownReports.Any(r => r.AgentId == "private-1")).IsFalse();
    }

    /// <summary>A server that has stopped answering must not hold a shutdown open: the budget is a
    /// ceiling on the whole pass, and teardown continues past it either way.</summary>
    [Test]
    public async Task A_hung_server_does_not_hold_shutdown_past_the_budget() {
        var server = new CaptureServerConnection { ShutdownReportHangs = true };
        var orch   = AgentOrchestratorHarness.BuildOrchestrator(
            server, new SpyPtyProcessFactory(), new Dictionary<string, IHostedAgentLauncher>());

        orch.SeedAgentForTest("hung-1", status: "Running");

        var started = DateTime.UtcNow;
        await orch.DisposeAsync();
        var elapsed = DateTime.UtcNow - started;

        await Assert.That(elapsed).IsLessThan(AgentOrchestrator.ShutdownReportBudget + TimeSpan.FromSeconds(15));
    }
}
