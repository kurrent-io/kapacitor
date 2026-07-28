using Capacitor.Cli.Daemon.Services;
using Microsoft.Extensions.Logging.Abstractions;

namespace Capacitor.Cli.Tests.Unit.Daemon;

/// <summary>
/// Phase B (D4 §6.4(2a)): <see cref="AgentKillQuarantine"/> — the in-memory holding pen for
/// agents whose death could not be confirmed at teardown. A quarantined entry counts against admission
/// (via the orchestrator's <c>EffectiveCount</c>) and the heartbeat retries the kill by EXACT identity
/// until confirmed gone, then drains it. Acts only on a test-owned <see cref="DummyProcess"/>.
/// </summary>
public class AgentKillQuarantineTests {
    [Test]
    public async Task Add_counts_and_snapshot_carries_flow_identity() {
        var q = new AgentKillQuarantine(NullLogger.Instance);
        var created = DateTimeOffset.UtcNow;

        q.Add(new AgentKillQuarantine.Entry("q1", 4242, "ident", "ReviewFlow", created, "flow-9", "reviewer"));

        await Assert.That(q.Count).IsEqualTo(1);

        var snap = q.Snapshot();
        await Assert.That(snap.Count).IsEqualTo(1);
        await Assert.That(snap[0].Id).IsEqualTo("q1");
        await Assert.That(snap[0].Kind).IsEqualTo("ReviewFlow");
        await Assert.That(snap[0].FlowRunId).IsEqualTo("flow-9");
        await Assert.That(snap[0].FlowRole).IsEqualTo("reviewer");
    }

    // ReadLiveness consults this on every settled command's ack, so it asks by id rather than projecting
    // a Snapshot and scanning it.
    [Test]
    public async Task IsQuarantined_answers_by_id() {
        var q = new AgentKillQuarantine(NullLogger.Instance);

        q.Add(new AgentKillQuarantine.Entry("q1", 1, "ident", "ReviewFlow", DateTimeOffset.UtcNow, null, null));

        await Assert.That(q.IsQuarantined("q1")).IsTrue();
        await Assert.That(q.IsQuarantined("q2")).IsFalse();
        await Assert.That(q.IsQuarantined("Q1")).IsFalse();   // ordinal, like the backing dictionary
    }

    [Test]
    public async Task Add_is_idempotent_per_agent_id() {
        var q = new AgentKillQuarantine(NullLogger.Instance);

        q.Add(new AgentKillQuarantine.Entry("dup", 1, "a", "ReviewFlow", DateTimeOffset.UtcNow, null, null));
        q.Add(new AgentKillQuarantine.Entry("dup", 2, "b", "ReviewFlow", DateTimeOffset.UtcNow, null, null));

        await Assert.That(q.Count).IsEqualTo(1);
    }

    [Test]
    public async Task RetryAll_kills_a_live_child_by_identity_then_drains_it() {
        using var dummy = DummyProcess.StartSleep(
            30, new Dictionary<string, string> { ["KCAP_AGENT_ID"] = "q1" });

        var identity = ProcessIdentity.Capture(dummy.Pid);
        await Assert.That(identity).IsNotNull();

        var q = new AgentKillQuarantine(NullLogger.Instance);
        q.Add(new AgentKillQuarantine.Entry(
            "q1", dummy.Pid, identity!, "ReviewFlow", DateTimeOffset.UtcNow, null, null));

        await Assert.That(q.Count).IsEqualTo(1);

        // First retry kills the child by exact identity (no env check needed — we own it). The child
        // dies but, as a direct child of this test process, may linger as an unreaped zombie whose
        // liveness is momentarily ambiguous — so it is retained (fail closed), exactly as a stuck child
        // is across heartbeat ticks in production.
        await q.RetryAllAsync(CancellationToken.None);
        dummy.WaitForExit(TimeSpan.FromSeconds(8)); // reap it → liveness is now unambiguous
        await Assert.That(dummy.HasExited).IsTrue();

        // Second retry observes CONFIRMED death and drains the entry so it stops counting against admission.
        await q.RetryAllAsync(CancellationToken.None);
        await Assert.That(q.Count).IsEqualTo(0);
    }

    [Test]
    public async Task RetryAll_drains_an_already_gone_process_without_a_kill() {
        // A process that is already gone (PID reused / exited) is confirmed-gone immediately and
        // drained — the retry never needs to signal anything.
        using var dummy = DummyProcess.StartSleep(30);
        var identity = ProcessIdentity.Capture(dummy.Pid)!;
        dummy.Kill();
        dummy.WaitForExit(TimeSpan.FromSeconds(5));

        var q = new AgentKillQuarantine(NullLogger.Instance);
        q.Add(new AgentKillQuarantine.Entry("gone", dummy.Pid, identity, "ReviewFlow", DateTimeOffset.UtcNow, null, null));

        await q.RetryAllAsync(CancellationToken.None);

        await Assert.That(q.Count).IsEqualTo(0);
    }
}
