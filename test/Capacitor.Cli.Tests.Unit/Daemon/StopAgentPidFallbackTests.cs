using Capacitor.Cli.Core;
using Capacitor.Cli.Daemon.Services;
using Capacitor.Cli.Tests.Unit.Daemon;

namespace Capacitor.Cli.Tests.Unit;

/// <summary>
/// Phase B (D4 §6.4(3)): <c>HandleStopAgent</c> for an id not in the registry falls back to the
/// PID record and reaps a matching live process by exact identity. OS-aware: Linux/Windows can prove
/// ownership (Linux via the <c>KCAP_AGENT_ID</c> env) and reap; on macOS the outcome depends on the OS
/// version's start-identity readability (redacted → spared, readable → reaped), so the test asserts the
/// record is deleted iff the process was confirmed gone. Partial of
/// <see cref="AgentOrchestratorVendorTests"/> to call the private HandleStopAgent.
/// </summary>
public partial class AgentOrchestratorVendorTests {
    [Test]
    public async Task HandleStopAgent_unknown_id_reaps_by_pid_record_where_env_is_readable() {
        await using var orch = BuildOrchestrator(
            new CaptureServerConnection(), new SpyPtyProcessFactory(), new Dictionary<string, IHostedAgentLauncher>());

        using var dummy = DummyProcess.StartSleep(30, new Dictionary<string, string> { ["KCAP_AGENT_ID"] = "ghost" });
        var identity = ProcessIdentity.Capture(dummy.Pid);
        await Assert.That(identity).IsNotNull();

        orch.WritePidRecordForTest(new AgentPidRecord(
            "ghost", dummy.Pid, identity!, PidIdentityKind.Present, "ReviewFlow", "codex", "f1", "reviewer",
            orch.DaemonIdForTest, orch.DaemonEpochForTest, DateTimeOffset.UtcNow));

        await orch.HandleStopAgent("ghost");

        if (!OperatingSystem.IsMacOS()) {
            // Linux / Windows → ownership is provable (Linux confirms via the KCAP_AGENT_ID env,
            // Windows via an exact (pid, start-identity) match), so the record-pass reap kills by
            // identity and deletes the record on CONFIRMED death.
            dummy.WaitForExit(TimeSpan.FromSeconds(10));
            await Assert.That(dummy.HasExited).IsTrue();
            await Assert.That(orch.PidRecordsForTest().Any(r => r.AgentId == "ghost")).IsFalse();
        } else {
            // macOS → the outcome depends on this OS version's start-identity readability (older macOS
            // redacts it → spared/retained; macOS 26+ reads it → reaped/deleted), so accept either but
            // assert the real invariant: the record is deleted IFF the process was confirmed gone.
            dummy.WaitForExit(TimeSpan.FromSeconds(10));
            var retained = orch.PidRecordsForTest().Any(r => r.AgentId == "ghost");
            await Assert.That(retained).IsEqualTo(!dummy.HasExited);
            if (!dummy.HasExited) dummy.Kill();
        }
    }

    [Test]
    public async Task HandleStopAgent_unknown_id_with_no_record_is_a_noop() {
        await using var orch = BuildOrchestrator(
            new CaptureServerConnection(), new SpyPtyProcessFactory(), new Dictionary<string, IHostedAgentLauncher>());

        // No record, no in-memory agent → must not throw.
        await orch.HandleStopAgent("does-not-exist");
        await Assert.That(orch.PidRecordsForTest()).IsEmpty();
    }
}
