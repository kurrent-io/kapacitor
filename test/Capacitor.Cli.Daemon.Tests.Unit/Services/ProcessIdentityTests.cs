using Capacitor.Cli.Daemon.Services;

namespace Capacitor.Cli.Daemon.Tests.Unit.Services;

/// <summary>
/// Phase B (D4): <see cref="ProcessIdentity"/> — exact start-identity match against a live
/// process (and NOT after it exits), plus reading a KCAP_* env marker from a live process. Acts only
/// on test-owned <see cref="DummyProcess"/> instances.
/// </summary>
public class ProcessIdentityTests {
    [Test]
    public async Task Capture_then_Matches_holds_for_live_process_and_fails_after_exit() {
        using var dummy = DummyProcess.StartSleep(30);

        var identity = ProcessIdentity.Capture(dummy.Pid);
        await Assert.That(identity).IsNotNull();
        await Assert.That(ProcessIdentity.Matches(dummy.Pid, identity!)).IsTrue();
        await Assert.That(ProcessIdentity.IsAlive(dummy.Pid)).IsTrue();

        dummy.Kill();
        dummy.WaitForExit(TimeSpan.FromSeconds(8));

        await Assert.That(ProcessIdentity.Matches(dummy.Pid, identity!)).IsFalse();
    }

    [Test]
    public async Task ReadAgentEnv_reads_a_kcap_marker_where_the_OS_allows_it() {
        using var dummy = DummyProcess.StartSleep(30, new Dictionary<string, string> {
            ["KCAP_AGENT_ID"]     = "agent-xyz",
            ["KCAP_DAEMON_EPOCH"] = "epoch-1",
        });

        var agentId = ProcessIdentity.ReadAgentEnv(dummy.Pid, "KCAP_AGENT_ID");

        if (OperatingSystem.IsLinux()) {
            // /proc/{pid}/environ is same-uid readable — the production (Linux) path reads the marker.
            await Assert.That(agentId).IsEqualTo("agent-xyz");
            await Assert.That(ProcessIdentity.ReadAgentEnv(dummy.Pid, "KCAP_DAEMON_EPOCH")).IsEqualTo("epoch-1");
            await Assert.That(ProcessIdentity.ReadAgentEnv(dummy.Pid, "KCAP_NOT_SET")).IsNull();
        } else {
            // macOS 26 redacts the env from KERN_PROCARGS2 for a same-user non-root reader (and ps -E),
            // so cross-process env reading is unavailable — the method degrades to null (callers then
            // SPARE the process; never a false read). Windows has no scan path. Either way: never a
            // wrong non-null value.
            await Assert.That(agentId is null or "agent-xyz").IsTrue();
        }

        dummy.Kill();
    }
}
