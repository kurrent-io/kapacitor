using Capacitor.Cli.Core;
using Capacitor.Cli.Daemon.Pty;
using Capacitor.Cli.Daemon.Services;
using Capacitor.Cli.Tests.Unit.Daemon;
using Microsoft.Extensions.Logging.Abstractions;

namespace Capacitor.Cli.Tests.Unit;

// Phase B2-b (sequenced-settlement design): the orchestrator now owns an epoch-scoped
// SequencedCommandProcessor, advertises its Epoch/HighestAcceptedSeq/LastProcessedSeq counters on the
// self-report + connect payload, and exposes the single confirmed-death-precedence liveness read
// (ReadLiveness) the processor uses to answer duplicate CommandAcks. This partial reuses the vendor
// test class's BuildOrchestrator/CaptureServerConnection/SpyPtyProcessFactory/SeedAgentForTest harness.
public partial class AgentOrchestratorVendorTests {
    [Test]
    public async Task Report_advertises_sequencing_capability_and_counters() {
        await using var orch = BuildOrchestrator(new CaptureServerConnection(), new SpyPtyProcessFactory(),
            new Dictionary<string, IHostedAgentLauncher>());
        var report = orch.BuildStatusReport();
        await Assert.That(report.Epoch).IsNotNull();
        await Assert.That(report.HighestAcceptedSeq).IsEqualTo(0L); // fresh epoch, nothing accepted yet
        await Assert.That(report.LastProcessedSeq).IsEqualTo(0L);
    }

    [Test]
    public async Task ReadLiveness_follows_confirmed_death_precedence() {
        await using var orch = BuildOrchestrator(new CaptureServerConnection(), new SpyPtyProcessFactory(),
            new Dictionary<string, IHostedAgentLauncher>());
        orch.SeedAgentForTest("live", LaunchKind.ReviewFlow, status: "Running");
        await Assert.That(orch.ReadLiveness("live")).IsEqualTo(AgentLiveness.Live);
        await Assert.That(orch.ReadLiveness("never")).IsEqualTo(AgentLiveness.Dead);
    }

    [Test]
    public async Task ReadLiveness_racing_transitions_never_yields_a_transient_false_dead() {
        // Parent §8: CommandAck.CurrentState racing live->quarantine / quarantine->dead transitions must
        // never surface a transient false Dead. Hammer ReadLiveness while the agent moves live -> quarantine
        // (teardown of a surviving child) -> dead (drain after the child exits). The shipped ordering
        // invariant (CleanupAgentAsync adds to _quarantine BEFORE removing from _agents) keeps the id
        // continuously in _agents ∪ _quarantine until the drain, so deadness is monotonic.
        var server = new CaptureServerConnection();
        await using var orch = BuildOrchestrator(server, new SpyPtyProcessFactory(),
            new Dictionary<string, IHostedAgentLauncher>());
        using var dummy = DummyProcess.StartSleep(30);
        orch.SeedAgentForTest("rev", LaunchKind.ReviewFlow, status: "Running", flowRunId: "f", flowRole: "reviewer",
            pty: new LivePtyDouble(dummy.Pid), startIdentity: ProcessIdentity.Capture(dummy.Pid));

        var observed = new System.Collections.Concurrent.ConcurrentQueue<AgentLiveness>();
        using var stop = new CancellationTokenSource();
        var hammer = Task.Run(() => { while (!stop.IsCancellationRequested) observed.Enqueue(orch.ReadLiveness("rev")); });

        await orch.CleanupAgentForTest("rev");                        // live -> quarantine (child still alive)
        dummy.Kill(); dummy.WaitForExit(TimeSpan.FromSeconds(5));
        await orch.RetryQuarantineForTest();                          // quarantine -> dead (drain confirms death)
        stop.Cancel(); await hammer;

        // No transient false Dead: once Dead is observed it is never followed by a non-Dead reading.
        var seq = observed.ToArray();
        var firstDead = Array.IndexOf(seq, AgentLiveness.Dead);
        if (firstDead >= 0)
            await Assert.That(seq.Skip(firstDead).All(s => s == AgentLiveness.Dead)).IsTrue();
        await Assert.That(orch.ReadLiveness("rev")).IsEqualTo(AgentLiveness.Dead); // genuinely dead at the end
    }

    // Phase B2-b (sequenced-settlement design §4.2.2/§5.5): a Seq'd LaunchAgentCommand the shipped launch
    // would reject at capacity flows through the processor as a terminal LaunchRejected(daemon_capacity)
    // CommandOutcome, so the sequenced lane emits a CommandRejected (in addition to the legacy
    // LaunchFailed) and the watermark still advances. MaxConcurrentAgents=0 makes the very first admission
    // check reject with no launcher/worktree side effects.
    [Test]
    public async Task Sequenced_launch_over_capacity_emits_daemon_capacity_rejection() {
        var server = new SeqCaptureServerConnection();
        await using var orch = BuildOrchestrator(
            server, new SpyPtyProcessFactory(),
            new Dictionary<string, IHostedAgentLauncher> { ["claude"] = new SpyHostedAgentLauncher("claude", cliPath: "spy-claude") },
            configure: c => c.MaxConcurrentAgents = 0);

        await orch.HandleLaunchAgentForTest(new LaunchAgentCommand(
            AgentId: "cap", Prompt: "hi", Model: "opus", Effort: null,
            RepoPath: "/tmp/does-not-matter", Tools: null, AttachmentIds: null, Vendor: "claude",
            Epoch: orch.DaemonEpochForTest, Seq: 1, CommandId: "cmd-1"));

        // §3.3: HandleLaunchAgentForTest drains the lane again since the fix round (it now awaits
        // DrainLaneForTest after submitting), so the terminal rejection is already settled by the
        // time this returns — the poll below is redundant but harmless, kept as defense-in-depth
        // against a future regression reopening the async gap between acceptance and execution.
        await SpinUntilAsync(() => server.Rejects.Count > 0, TimeSpan.FromSeconds(30));

        await Assert.That(server.Rejects.Count).IsEqualTo(1);
        await Assert.That(server.Rejects[0].Reason).IsEqualTo(CommandRejectedReason.DaemonCapacity);
        await Assert.That(server.Rejects[0].Seq).IsEqualTo(1L);
        // The terminal (daemon_capacity) processing advanced the watermark through the accepted prefix.
        await Assert.That(orch.BuildStatusReport().LastProcessedSeq).IsEqualTo(1L);
    }

    // Phase B2-b (sequenced-settlement design §4.2.6): the daemon-side heal-barrier contract. A
    // StopAgentV2 at Seq = M that terminally processes advances LastProcessedSeq to M; once the agent's
    // death is confirmed it is absent from BOTH LiveAgents and Quarantined, so a later report PROVES the
    // id's absence at watermark M. The seeded agent uses NoopPtyProcess (pid 0 / not-alive), so
    // HandleStopAgent -> CleanupAgentAsync confirms death immediately (no quarantine), removing it from
    // _agents. This is verification that Tasks 12–16 compose into the barrier obligation.
    [Test]
    public async Task Stop_via_v2_advances_watermark_and_a_later_report_omits_the_confirmed_dead_id() {
        var server = new CaptureServerConnection();
        await using var orch = BuildOrchestrator(server, new SpyPtyProcessFactory(),
            new Dictionary<string, IHostedAgentLauncher>());
        var agent = orch.SeedAgentForTest("rev", LaunchKind.ReviewFlow, status: "Running", flowRunId: "f", flowRole: "reviewer");
        var epoch = orch.DaemonEpochForTest;

        await orch.HandleStopAgentV2ForTest(new StopAgentV2("rev", epoch, 1, "cmd-1"));

        var report = orch.BuildStatusReport();
        await Assert.That(report.LastProcessedSeq).IsEqualTo(1L);                          // stop at M=1 terminally processed
        await Assert.That(report.LiveAgents.Select(x => x.Id)).DoesNotContain("rev");      // confirmed dead (Noop pty) -> absent
        await Assert.That(report.Quarantined.Select(x => x.Id)).DoesNotContain("rev");
    }

    // Phase B2-b (sequenced-settlement design §5): an un-Seq'd launch (old server) runs the legacy
    // unsequenced lane and must NEVER advance the sequenced watermark.
    [Test]
    public async Task Legacy_unsequenced_launch_never_advances_the_watermark() {
        var server = new CaptureServerConnection();
        await using var orch = BuildOrchestrator(server, new SpyPtyProcessFactory(),
            new Dictionary<string, IHostedAgentLauncher>());
        // No Epoch/Seq/CommandId -> legacy lane.
        await orch.HandleLaunchAgentForTest(new LaunchAgentCommand("x", "hi", "opus", null, "/tmp", null, null, "bogus"));
        await Assert.That(orch.BuildStatusReport().HighestAcceptedSeq).IsEqualTo(0L);
    }

    // Phase B2-b (sequenced-settlement design §5.5): a PARTIAL sequencing tuple (some but not all of
    // Epoch/Seq/CommandId present) is a malformed sequenced command and must FAIL CLOSED (LaunchFailed) —
    // never fall through to the unwatermarked legacy lane, whose retry could be re-accepted on the
    // sequenced lane and double-create the generation (at-most-once-per-generation). It must not spawn and
    // must not advance the watermark.
    [Test]
    public async Task Partial_sequencing_tuple_fails_closed_without_spawning_or_advancing_the_watermark() {
        var server = new SeqCaptureServerConnection();
        // A VALID vendor so the fail-closed reason can ONLY be the malformed-tuple route, not the legacy
        // lane's unknown-vendor rejection (which would mask a missing fix).
        await using var orch = BuildOrchestrator(server, new SpyPtyProcessFactory(),
            new Dictionary<string, IHostedAgentLauncher> { ["claude"] = new SpyHostedAgentLauncher("claude", cliPath: "spy-claude") });

        // Epoch + Seq present, CommandId ABSENT -> malformed.
        await orch.HandleLaunchAgentForTest(new LaunchAgentCommand(
            AgentId: "partial", Prompt: "hi", Model: "opus", Effort: null,
            RepoPath: "/tmp/does-not-matter", Tools: null, AttachmentIds: null, Vendor: "claude",
            Epoch: orch.DaemonEpochForTest, Seq: 1, CommandId: null));

        await Assert.That(server.LaunchFaileds.Count).IsEqualTo(1);
        await Assert.That(server.LaunchFaileds[0].AgentId).IsEqualTo("partial");
        await Assert.That(server.LaunchFaileds[0].Reason).Contains("Malformed sequenced launch"); // the fix's route
        await Assert.That(orch.GetAgentForTest("partial")).IsNull();                    // never spawned
        var report = orch.BuildStatusReport();
        await Assert.That(report.HighestAcceptedSeq).IsEqualTo(0L);                     // watermark untouched
        await Assert.That(report.LastProcessedSeq).IsEqualTo(0L);
    }

    /// <summary>A live-pid-backed pty double whose TerminateAsync deliberately does NOT kill (so teardown
    /// quarantines the "surviving" child). Mirrors LaunchCleanupTests' private LiveNoKillPtyProcess.</summary>
    sealed class LivePtyDouble(int pid) : IPtyProcess {
        public int  Pid       => pid;
        public bool HasExited => false;
        public int? ExitCode  => null;
        public ValueTask DisposeAsync() => default;
        public Task WaitForExitAsync(TimeSpan? _) => Task.CompletedTask;
        public Task TerminateAsync(TimeSpan?   _) => Task.CompletedTask; // deliberately does NOT kill
#pragma warning disable CS1998
        public async IAsyncEnumerable<byte[]> ReadOutputAsync([System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken _ = default) { yield break; }
#pragma warning restore CS1998
        public Task WriteAsync(string _) => Task.CompletedTask;
        public Task WriteAsync(byte[] _) => Task.CompletedTask;
        public void Resize(ushort     _, ushort __) { }
        public void SendInterrupt() { }
    }

    /// <summary>Captures the one-way sequenced CommandAck/CommandRejected sends (synchronously, so an
    /// awaited SubmitAsync has already recorded them) and no-ops everything else the capacity-reject path
    /// touches. IsReady is forced true so the base sends aren't short-circuited before our override.
    ///
    /// <para>§3.3: also overrides <see cref="AgentRegisteredAsync"/> so a genuinely SUCCESSFUL
    /// sequenced launch (one that reaches <c>RegisterAgentAsync</c>'s awaited, un-guarded
    /// <c>_server.AgentRegisteredAsync(...)</c> call) doesn't fault on the base implementation's
    /// <c>_hub.InvokeAsync</c> against a never-started HubConnection — no pre-Task-5 test ever drove a
    /// full success through THIS server double, since every existing capacity/malformed-tuple test is
    /// rejected before reaching registration.</para></summary>
    sealed class SeqCaptureServerConnection() : ServerConnection(
        new() { Name = "test", ServerUrl = "http://127.0.0.1:1" },
        NullLoggerFactory.Instance,
        NullLogger<ServerConnection>.Instance
    ) {
        internal override bool IsReady => true;
        public List<CommandRejected> Rejects       { get; } = [];
        public List<CommandAck>      Acks          { get; } = [];
        public List<(string AgentId, string Reason)> LaunchFaileds { get; } = [];

        public override Task LaunchFailedAsync(string agentId, string reason) { lock (LaunchFaileds) LaunchFaileds.Add((agentId, reason)); return Task.CompletedTask; }
        public override Task CommandRejectedAsync(CommandRejected rej) { lock (Rejects) Rejects.Add(rej); return Task.CompletedTask; }
        public override Task CommandAckAsync(CommandAck ack)           { lock (Acks)    Acks.Add(ack);    return Task.CompletedTask; }

        public override Task AgentRegisteredAsync(
                string agentId, string? prompt, string? model, string? effort, string? repoPath,
                string? sandboxPolicy = null, string? approvalPolicy = null, string? permissionPreset = null) => Task.CompletedTask;
    }
}
