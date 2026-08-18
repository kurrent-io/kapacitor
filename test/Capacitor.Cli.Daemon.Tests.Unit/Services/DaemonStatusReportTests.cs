using Capacitor.Cli.Core;
using Capacitor.Cli.Daemon.Services;
using Capacitor.Cli.Daemon.Tests.Unit.Pty;

namespace Capacitor.Cli.Daemon.Tests.Unit.Services;

/// <summary>
/// Phase B (D2): <see cref="AgentOrchestrator.BuildStatusReport"/> reports the daemon's
/// authoritative ActiveCount + live-agent metadata (quarantine wired in D4/Task 8).
/// Partial of <see cref="AgentOrchestratorVendorTests"/> to reuse its test doubles.
/// </summary>
public class DaemonStatusReportTests {
    [Test]
    public async Task BuildStatusReport_reports_active_count_and_live_agents() {
        await using var orch = AgentOrchestratorHarness.BuildOrchestrator(
            new CaptureServerConnection(), new SpyPtyProcessFactory(), new Dictionary<string, IHostedAgentLauncher>());

        orch.SeedAgentForTest("a1", LaunchKind.ReviewFlow, status: "Running", flowRunId: "f1", flowRole: "reviewer");
        orch.SeedAgentForTest("a2", LaunchKind.Default,    status: "Running");
        orch.SeedAgentForTest("a3", LaunchKind.Default,    status: "Stopped");

        var report = orch.BuildStatusReport();

        await Assert.That(report.ActiveCount).IsEqualTo(2); // a1 + a2 (a3 stopped)
        await Assert.That(report.LiveAgents.Select(x => x.Id)).IsEquivalentTo(new[] { "a1", "a2" });
        await Assert.That(report.Quarantined).IsEmpty(); // until D4/Task 8
    }
}
