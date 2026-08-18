using Capacitor.Cli.Core;
using Capacitor.Cli.Daemon.Services;
using Capacitor.Cli.Daemon.Tests.Unit.Pty;

namespace Capacitor.Cli.Daemon.Tests.Unit.Services;

// Phase B2-b (sequenced-settlement design §4.2.4): orchestrator-level proof that the quarantine-drain
// hook emits positive per-id death evidence into the real ResolvedCandidatesLedger owned by the
// orchestrator (append BEFORE the durable PID-record delete). Reuses the vendor test class's
// BuildOrchestrator/CaptureServerConnection/SpyPtyProcessFactory harness + the shared test seams.
public class LedgerHookOrchestratorTests {
    [Test]
    public async Task Quarantine_drain_via_orchestrator_emits_resolved_evidence_before_deleting_the_record() {
        await using var orch = AgentOrchestratorHarness.BuildOrchestrator(
            new CaptureServerConnection(), new SpyPtyProcessFactory(),
            new Dictionary<string, IHostedAgentLauncher>());

        using var dummy = DummyProcess.StartSleep(30);
        var pid      = dummy.Pid;
        var identity = ProcessIdentity.Capture(pid)!;
        dummy.Kill(); dummy.WaitForExit(TimeSpan.FromSeconds(5)); // confirmed dead -> the drain confirms + emits

        // Teardown retains the PID record (current epoch) of a quarantined survivor, so the drain deletes
        // it. Seed both the record and the quarantine entry to mirror that post-teardown state exactly.
        orch.WritePidRecordForTest(new AgentPidRecord(
            "q1", pid, identity, PidIdentityKind.Present, "ReviewFlow", "codex",
            "flow-1", "reviewer", orch.DaemonIdForTest, orch.DaemonEpochForTest, DateTimeOffset.UtcNow));
        orch.QuarantineForTest(new AgentKillQuarantine.Entry(
            "q1", pid, identity, "ReviewFlow", DateTimeOffset.UtcNow, "flow-1", "reviewer"));

        await orch.RetryQuarantineForTest();

        var entry = orch.ResolvedLedgerSnapshotForTest.Single();
        await Assert.That(entry.AgentId).IsEqualTo("q1");
        await Assert.That(entry.OldEpoch).IsEqualTo(orch.DaemonEpochForTest); // drain emits the current epoch
        await Assert.That(entry.FlowRole).IsEqualTo("reviewer");
        await Assert.That(orch.PidRecordsForTest()).IsEmpty(); // record deleted after the append
    }
}
