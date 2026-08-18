using Capacitor.Cli.Core;
using Capacitor.Cli.Daemon.Services;
using Capacitor.Cli.Daemon.Tests.Unit.Pty;

namespace Capacitor.Cli.Daemon.Tests.Unit.Services;

// Phase B2-b (sequenced-settlement design): the orchestrator surfaces the durable coverage
// boot-chain verdict (DaemonConfig.RecordlessSurvivorsImpossible, folded in DaemonRunner before
// Connect) so the enriched DaemonConnect payload can advertise it. Reuses AgentOrchestratorHarness
// (BuildOrchestrator/CaptureServerConnection/SpyPtyProcessFactory).
public class StartupCompletenessTests {
    [Test]
    public async Task Status_report_advertises_recordless_survivors_impossible_from_config() {
        await using var orch = AgentOrchestratorHarness.BuildOrchestrator(
            new CaptureServerConnection(), new SpyPtyProcessFactory(),
            new Dictionary<string, IHostedAgentLauncher>(),
            configure: c => c.RecordlessSurvivorsImpossible = true);

        await Assert.That(orch.RecordlessSurvivorsImpossibleForTest).IsTrue();
    }

    // Phase B2-b (sequenced-settlement design): StartupDiscovery.MarkerScanState is NotApplicable off
    // Linux (Windows has no scan, macOS env is redacted) and — on Linux — flips to Complete after one
    // clean env-marker-scan pass. A clean boot with no candidates completes the pass on the first reap.
    [Test]
    public async Task Startup_discovery_is_not_applicable_off_linux_and_pending_before_a_scan() {
        await using var orch = AgentOrchestratorHarness.BuildOrchestrator(new CaptureServerConnection(), new SpyPtyProcessFactory(),
            new Dictionary<string, IHostedAgentLauncher>());
        // A clean boot with no candidates: after ReapOrphansOnceAsync the Linux scan is complete.
        await orch.ReapOrphansOnceAsync();
        await Assert.That(orch.BuildStatusReport().StartupDiscovery!.Value.MarkerScanState)
            .IsEqualTo(OperatingSystem.IsLinux() ? MarkerScanState.Complete : MarkerScanState.NotApplicable);
    }

    // Phase B2-b (sequenced-settlement design): a persisted marker-candidate source (a recordless
    // prior-epoch survivor with no matching live triple) surfaces as a PendingMarker blocked candidate
    // and keeps StartupReapComplete false. Linux-only — the blocked-candidate surface is asserted
    // directly via BuildStatusReport (no scan runs), so the seeded dead pid is never resolved away.
    [Test]
    public async Task Pending_marker_candidate_blocks_completion_and_surfaces_reason() {
        if (!OperatingSystem.IsLinux()) return;
        await using var orch = AgentOrchestratorHarness.BuildOrchestrator(new CaptureServerConnection(), new SpyPtyProcessFactory(),
            new Dictionary<string, IHostedAgentLauncher>());
        orch.SeedPendingMarkerCandidateForTest("blocked", "old"); // occupant with no matching triple
        var report = orch.BuildStatusReport();
        await Assert.That(report.StartupReapComplete).IsFalse();
        await Assert.That(report.UnresolvedStartupCandidates!.Single().Reason)
            .IsEqualTo(StartupCandidateUnresolvedReason.PendingMarker);
    }

    // Phase B2-b (sequenced-settlement design §5.5): a PRESENT prior-epoch record that the record pass
    // spared (a transient ambiguous identity read on Linux, or a record-pass fault) is still on disk and
    // therefore UNRESOLVED — confirmed-dead records are deleted. It must keep completion FALSE (surfaced as
    // identity_unresolvable), never be silently omitted (the paired server would treat omission as proof of
    // death and launch a duplicate). Deterministic: BlockedCandidates() just reads the store, so seed it
    // directly with a dead pid (so the macOS legacy-live arm can't catch it) — the else arm must.
    [Test]
    public async Task Spared_prior_epoch_present_record_blocks_completion_as_identity_unresolvable() {
        await using var orch = AgentOrchestratorHarness.BuildOrchestrator(new CaptureServerConnection(), new SpyPtyProcessFactory(),
            new Dictionary<string, IHostedAgentLauncher>());
        orch.WritePidRecordForTest(new AgentPidRecord(
            "spared", 999_999, "mac:boot:uid", PidIdentityKind.Present, "ReviewFlow", "codex",
            "flow-1", "reviewer", orch.DaemonIdForTest, "old-epoch", DateTimeOffset.UtcNow));

        var report = orch.BuildStatusReport();
        await Assert.That(report.StartupReapComplete).IsFalse();
        var blocked = report.UnresolvedStartupCandidates!.Single(c => c.AgentId == "spared");
        await Assert.That(blocked.Reason).IsEqualTo(StartupCandidateUnresolvedReason.IdentityUnresolvable);
        await Assert.That(blocked.FlowRunId).IsEqualTo("flow-1");   // from the TRUSTED record
        await Assert.That(blocked.FlowRole).IsEqualTo("reviewer");
        await Assert.That(orch.ComputeStartupReapComplete()).IsFalse();
    }

    // Phase B2-b (sequenced-settlement design §4.2.4): the ledger's un-acked snapshot is re-reported on
    // every status report until the server prunes it per-entry via AckResolvedCandidates. The seam and
    // the underlying ledger Ack are SYNCHRONOUS (void) — no await (would be CS4008).
    [Test]
    public async Task Status_report_carries_resolved_candidates_and_ack_prunes_them() {
        var server = new CaptureServerConnection();
        await using var orch = AgentOrchestratorHarness.BuildOrchestrator(server, new SpyPtyProcessFactory(),
            new Dictionary<string, IHostedAgentLauncher>());

        var g = orch.SeedResolvedCandidateForTest("a1", "old-epoch");   // test seam over the ledger
        await Assert.That(orch.BuildStatusReport().ResolvedStartupCandidates!.Single().AgentId).IsEqualTo("a1");

        // The seam + the underlying ledger Ack are SYNCHRONOUS (void) — no await (would be CS4008).
        orch.HandleAckResolvedCandidatesForTest(
            new AckResolvedCandidates([new ResolvedCandidateAck(g.Generation, "a1", "old-epoch")]));
        await Assert.That(orch.BuildStatusReport().ResolvedStartupCandidates).IsEmpty();
    }
}
