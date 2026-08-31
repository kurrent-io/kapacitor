using System.Runtime.CompilerServices;
using Capacitor.Cli.Core;
using Capacitor.Cli.Daemon.Pty;
using Capacitor.Cli.Daemon.Services;
using Capacitor.Cli.Daemon.Tests.Unit.Pty;

namespace Capacitor.Cli.Daemon.Tests.Unit.Services;

/// <summary>
/// Phase B (D1 + D4 §6.4(2a)): the single-flight teardown and the kill-quarantine.
/// <list type="bullet">
/// <item>A post-insert launch failure (e.g. a throwing RegisterAgentAsync) routes through the same
/// <c>CleanupAgentAsync</c> teardown, so it can never strand an agent whose child is still live.</item>
/// <item>Concurrent teardowns of one agent run exactly once (TryRemove + CleanupStarted gate).</item>
/// <item>A child that survives teardown is QUARANTINED — retained, counted against admission via
/// <c>EffectiveCount</c>, and reported in <c>QuarantineSnapshot()</c> — so a stuck-kill mode fails
/// closed rather than minting unbounded processes.</item>
/// </list>
/// Uses <see cref="AgentOrchestratorHarness"/> for its BuildOrchestrator/CreateGitRepo/
/// CaptureServerConnection/SpyPtyProcessFactory harness.
/// </summary>
public class LaunchCleanupTests {
    [Test]
    public async Task Post_insert_launch_failure_tears_down_via_single_flight_and_unregisters() {
        using var repoPath = GitRepo.CreateWithCommit();

        // AgentRegisteredAsync throws on the launch's RegisterAgentAsync call, AFTER the agent was
        // inserted into _agents — the exact post-insert failure the D1 routing must catch.
        var server     = new CaptureServerConnection { AgentRegisteredFailTimes = 1 };
        var ptyFactory = new SpyPtyProcessFactory();

        await using var orch = AgentOrchestratorHarness.BuildOrchestrator(server, ptyFactory, AgentOrchestratorHarness.Launcher("claude"), allowedRepoPath: repoPath);

        await orch.HandleLaunchAgentForTest(new LaunchAgentCommand(
            AgentId: "a1", Prompt: "hi", Model: "opus", Effort: null,
            RepoPath: repoPath, Tools: null, AttachmentIds: null, Vendor: "claude"));

        // Routed through CleanupAgentAsync: the registry entry is gone, the launch failed on the
        // server, and — the discriminator vs the pre-insert path — AgentUnregistered was sent.
        await Assert.That(orch.GetAgentForTest("a1")).IsNull();
        await Assert.That(server.LaunchFailedCalls.Any(c => c.AgentId == "a1")).IsTrue();
        await Assert.That(server.AgentUnregisteredCalls).Contains("a1");

    }

    [Test]
    public async Task Concurrent_teardown_of_one_agent_unregisters_exactly_once() {
        var server = new CaptureServerConnection();

        await using var orch = AgentOrchestratorHarness.BuildOrchestrator(
            server, new SpyPtyProcessFactory(), new Dictionary<string, IHostedAgentLauncher>());

        orch.SeedAgentForTest("s1", LaunchKind.Default, status: "Running");

        // Two racing teardowns (the launch catch and the read-loop finally, in production) must run
        // the teardown exactly once.
        await Task.WhenAll(orch.CleanupAgentForTest("s1"), orch.CleanupAgentForTest("s1"));

        await Assert.That(orch.GetAgentForTest("s1")).IsNull();
        await Assert.That(server.AgentUnregisteredCalls.Count(x => x == "s1")).IsEqualTo(1);
    }

    [Test]
    public async Task Teardown_of_a_child_that_survives_disposal_quarantines_and_counts_it() {
        var server = new CaptureServerConnection();

        await using var orch = AgentOrchestratorHarness.BuildOrchestrator(
            server, new SpyPtyProcessFactory(), new Dictionary<string, IHostedAgentLauncher>());

        // A real, live child whose runtime Dispose/Terminate are no-ops — so it is STILL ALIVE after
        // CleanupAgentAsync's disposal step, exactly like a stuck child that ignored SIGTERM. Its stored
        // start-identity is what proves "still ours" at teardown (recycled-pid-safe).
        using var dummy = DummyProcess.StartSleep(
            30, new Dictionary<string, string> { ["KCAP_AGENT_ID"] = "stuck" });
        orch.SeedAgentForTest("stuck", LaunchKind.ReviewFlow, status: "Running",
            flowRunId: "flow-1", flowRole: "reviewer", pty: new LiveNoKillPtyProcess(dummy.Pid),
            startIdentity: ProcessIdentity.Capture(dummy.Pid));

        await orch.CleanupAgentForTest("stuck");

        // Dropped from the live registry but quarantined: it counts against admission (EffectiveCount)
        // and is surfaced with its flow identity, so the daemon fails closed until the kill confirms.
        await Assert.That(orch.GetAgentForTest("stuck")).IsNull();
        await Assert.That(orch.EffectiveCount).IsEqualTo(1);

        var snap = orch.QuarantineSnapshot();
        await Assert.That(snap.Any(q => q.Id == "stuck" && q.FlowRunId == "flow-1")).IsTrue();

        // dummy is disposed by the using; also drop the quarantine's hold explicitly for hygiene.
        dummy.Kill();
    }

    [Test]
    public async Task Launch_stamps_daemon_identity_env_markers_on_the_spawned_child() {
        using var repoPath = GitRepo.CreateWithCommit();

        var server     = new CaptureServerConnection();
        var ptyFactory = new SpyPtyProcessFactory();

        await using var orch = AgentOrchestratorHarness.BuildOrchestrator(server, ptyFactory, AgentOrchestratorHarness.Launcher("claude"), allowedRepoPath: repoPath);

        await orch.HandleLaunchAgentForTest(new LaunchAgentCommand(
            AgentId: "env-1", Prompt: "hi", Model: "opus", Effort: null,
            RepoPath: repoPath, Tools: null, AttachmentIds: null, Vendor: "claude"));

        // The OrphanReaper env-marker scan (D4 §6.4(3)) recognizes a recordless survivor by these
        // three markers on the LIVE child's own env — so the spawn must stamp all three.
        await Assert.That(ptyFactory.LastEnv).IsNotNull();
        await Assert.That(ptyFactory.LastEnv!["KCAP_AGENT_ID"]).IsEqualTo("env-1");
        await Assert.That(ptyFactory.LastEnv!["KCAP_DAEMON_ID"]).IsEqualTo(orch.DaemonIdForTest);
        await Assert.That(ptyFactory.LastEnv!["KCAP_DAEMON_EPOCH"]).IsEqualTo(orch.DaemonEpochForTest);

    }

    [Test]
    public async Task Teardown_does_not_quarantine_or_kill_a_recycled_pid() {
        var server = new CaptureServerConnection();

        await using var orch = AgentOrchestratorHarness.BuildOrchestrator(
            server, new SpyPtyProcessFactory(), new Dictionary<string, IHostedAgentLauncher>());

        // A live process occupies the pid, but the agent's STORED identity is a same-scheme token with a
        // CONCLUSIVELY different value — exactly what a recycled pid looks like (our child exited, the pid
        // was reused). Derive it from the occupant's OWN token + a suffix so it's guaranteed different yet
        // same-scheme on every OS. (Deriving it from a second process is not safe: on Linux the start
        // token is boot-relative clock ticks — two rapidly-started processes can collide on the same
        // value.) Teardown must treat our agent as gone — never quarantine, never kill the occupant.
        using var occupant = DummyProcess.StartSleep(30);
        var wrongIdentity  = ProcessIdentity.Capture(occupant.Pid)! + "9"; // same scheme, ≠ occupant's value

        orch.SeedAgentForTest("recycled", LaunchKind.ReviewFlow, status: "Running",
            pty: new LiveNoKillPtyProcess(occupant.Pid), startIdentity: wrongIdentity);

        await orch.CleanupAgentForTest("recycled");

        await Assert.That(orch.GetAgentForTest("recycled")).IsNull();
        await Assert.That(orch.QuarantineSnapshot().Any(q => q.Id == "recycled")).IsFalse();
        await Assert.That(orch.EffectiveCount).IsEqualTo(0);
        await Assert.That(occupant.HasExited).IsFalse(); // the occupant was never signalled

        occupant.Kill();
    }

    [Test]
    public async Task Teardown_quarantines_when_the_stored_identity_is_uncomparable() {
        var server = new CaptureServerConnection();

        await using var orch = AgentOrchestratorHarness.BuildOrchestrator(
            server, new SpyPtyProcessFactory(), new Dictionary<string, IHostedAgentLauncher>());

        // A live child whose stored identity uses a FOREIGN token scheme ("zz:") — uncomparable on every
        // OS (tri-state null), so ownership can neither be proven nor disproven. Fail closed: retain +
        // quarantine (count against admission) rather than dropping a possibly-live child's record.
        using var dummy = DummyProcess.StartSleep(30);
        orch.SeedAgentForTest("ambiguous", LaunchKind.ReviewFlow, status: "Running",
            pty: new LiveNoKillPtyProcess(dummy.Pid), startIdentity: "zz:unknown-scheme-token");

        await orch.CleanupAgentForTest("ambiguous");

        await Assert.That(orch.GetAgentForTest("ambiguous")).IsNull();
        await Assert.That(orch.QuarantineSnapshot().Any(q => q.Id == "ambiguous")).IsTrue();
        await Assert.That(orch.EffectiveCount).IsEqualTo(1);

        dummy.Kill();
    }

    [Test]
    public async Task Quarantine_drain_deletes_the_retained_pid_record() {
        var server = new CaptureServerConnection();

        await using var orch = AgentOrchestratorHarness.BuildOrchestrator(
            server, new SpyPtyProcessFactory(), new Dictionary<string, IHostedAgentLauncher>());

        // A live child with a durable PID record. Teardown quarantines it (still alive) and RETAINS the
        // record. Once the child is gone, the quarantine retry must both drain the entry AND delete the
        // record — otherwise the current-epoch record leaks (the orphan sweep skips it until restart).
        using var dummy = DummyProcess.StartSleep(30, new Dictionary<string, string> { ["KCAP_AGENT_ID"] = "qr" });
        var identity = ProcessIdentity.Capture(dummy.Pid)!;
        orch.WritePidRecordForTest(new AgentPidRecord(
            "qr", dummy.Pid, identity, PidIdentityKind.Present, "ReviewFlow", "codex", "f1", "reviewer",
            orch.DaemonIdForTest, orch.DaemonEpochForTest, DateTimeOffset.UtcNow));
        orch.SeedAgentForTest("qr", LaunchKind.ReviewFlow, status: "Running",
            pty: new LiveNoKillPtyProcess(dummy.Pid), startIdentity: identity);

        await orch.CleanupAgentForTest("qr");
        await Assert.That(orch.QuarantineSnapshot().Any(q => q.Id == "qr")).IsTrue();
        await Assert.That(orch.PidRecordsForTest().Any(r => r.AgentId == "qr")).IsTrue(); // retained while quarantined

        // Child is now gone (reaped) → the retry confirms death, drains the entry, and deletes the record.
        dummy.Kill();
        dummy.WaitForExit(TimeSpan.FromSeconds(8));
        await orch.RetryQuarantineForTest();

        await Assert.That(orch.QuarantineSnapshot().Any(q => q.Id == "qr")).IsFalse();
        await Assert.That(orch.PidRecordsForTest().Any(r => r.AgentId == "qr")).IsFalse();
    }

    /// <summary>An <see cref="IPtyProcess"/> that reports a real live child's pid but whose disposal
    /// and termination are deliberately no-ops — models a child that survives teardown, so
    /// CleanupAgentAsync's confirm-death step must quarantine it.</summary>
    sealed class LiveNoKillPtyProcess(int pid) : IPtyProcess {
        public int  Pid       => pid;
        public bool HasExited => false;
        public int? ExitCode  => null;

        public ValueTask DisposeAsync() => default;
        public Task WaitForExitAsync(TimeSpan? _) => Task.CompletedTask;
        public Task TerminateAsync(TimeSpan?   _) => Task.CompletedTask; // deliberately does NOT kill

#pragma warning disable CS1998
        public async IAsyncEnumerable<byte[]> ReadOutputAsync([EnumeratorCancellation] CancellationToken _ = default) {
            yield break;
        }
#pragma warning restore CS1998

        public Task WriteAsync(string _) => Task.CompletedTask;
        public Task WriteAsync(byte[] _) => Task.CompletedTask;
        public void Resize(ushort     _, ushort __) { }
        public void SendInterrupt() { }
    }
}
