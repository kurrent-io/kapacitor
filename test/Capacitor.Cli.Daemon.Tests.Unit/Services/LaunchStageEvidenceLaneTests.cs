using Capacitor.Cli.Core;
using Capacitor.Cli.Daemon.Services;
using Capacitor.Cli.Daemon.Tests.Unit.Pty;

namespace Capacitor.Cli.Daemon.Tests.Unit.Services;

/// <summary>
/// The launch-stage EVIDENCE lane, end to end: a stage stamped during the ACP handshake must reach
/// the server in a <c>DaemonStatusReport</c> that actually CARRIES the agent being staged.
///
/// <para>The defect these tests fence: <c>PublishAgent</c> runs AFTER
/// <c>runtimeFactory.StartAsync</c>, so for the entire handshake the agent was absent from
/// <c>_agents</c> and <c>BuildLiveAgents</c> omitted it — every stage-triggered out-of-cycle report
/// described a live-agent list without the agent it was reporting a stage for. By publish time an ACP
/// runtime is flipped straight to Running with <c>ClearLaunchStage</c>, and <c>BuildLiveAgents</c>
/// gates <c>LaunchStage</c> on <c>Status == "Starting"</c>. Net: <c>launch_stage</c> was NEVER non-null
/// on the wire, the server's <c>EvidenceGeneration</c> stayed 0 through registration, and its rolling
/// registration deadline could never extend — while the daemon's own 3x90s stage caps make a ~270s
/// legitimate handshake representable. The spec's §5 window is sound only if these reports really fire,
/// so "the four stamps produce reports carrying the agent" is the premise, not a nicety.</para>
///
/// <para>Vacuity note: asserting only the FINAL state (an agent that ends up Running with a null
/// stage) passes under the pre-fix code too — that is exactly the trap this plan hit repeatedly. Every
/// assertion below is therefore about reports observed DURING the handshake, or about the presence of
/// an entry rather than its absence.</para>
///
/// Uses <see cref="AgentOrchestratorHarness"/> for BuildOrchestrator/CreateGitRepo/
/// SeedAgentForTest/FakeAcpRuntime, same as AcpLaunchStageTests.cs and StatusReportActivityFieldsTests.cs.
/// </summary>
public class LaunchStageEvidenceLaneTests {
    /// <summary>Stamps the four real ACP handshake stages, in order, from inside
    /// <c>StartAsync</c> — i.e. exactly where <c>AcpHostedAgentRuntime</c> stamps them, and exactly
    /// where no <c>AgentInstance</c> exists yet. After each stamp it waits for that stamp's
    /// out-of-cycle report to land (the send is fire-and-forget), so the recorded report sequence is
    /// deterministic rather than racing. Optionally throws after the first stamp, to drive the
    /// failed-handshake cleanup path.</summary>
    sealed class FourStageAcpRuntimeFactory(
            CaptureServerConnection server, bool throwAfterFirstStage = false) : IHostedAgentRuntimeFactory {
        public static readonly string[] Stages = ["spawned", "initialized", "session_created", "model_set"];

        public string Vendor             => "cursor";
        public bool   SupportsUnattended => false;

        public bool IsAvailable() => true;

        public async Task<HostedRuntimeStart> StartAsync(RuntimeStartContext ctx, CancellationToken ct) {
            for (var i = 0; i < Stages.Length; i++) {
                var before = server.StatusReportCount;
                ctx.ActivityClock?.SetLaunchStage(Stages[i]);
                await WaitForReportAsync(before + 1);

                if (throwAfterFirstStage && i == 0)
                    throw new InvalidOperationException("simulated handshake failure");
            }

            var runtime = new FakeAcpRuntime();
            return new HostedRuntimeStart(runtime, McpConfigPath: null, Transcript: runtime);
        }

        async Task WaitForReportAsync(int atLeast) {
            // Bounded so a build where the stamp no longer fires a report FAILS FAST here (the report
            // count simply never reaches the target) instead of hanging the suite — a mutation that
            // hangs is a weak anchor. The assertions in the test then report the real shortfall.
            var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(5);
            while (server.StatusReportCount < atLeast && DateTime.UtcNow < deadline)
                await Task.Delay(5);
        }
    }

    /// <summary>Reduces a recorded report sequence to the launch stages this agent was seen carrying,
    /// consecutive duplicates collapsed. Reports that do not carry the agent at all contribute
    /// NOTHING — which is precisely why an empty result is the pre-fix signature.</summary>
    static List<string?> ObservedStages(IReadOnlyList<DaemonStatusReport> reports, string agentId) {
        var seen = new List<string?>();

        foreach (var r in reports) {
            var entry = r.LiveAgents.FirstOrDefault(a => a.Id == agentId);
            if (entry.Id != agentId) continue; // struct default => the report did not carry this agent

            if (seen.Count == 0 || seen[^1] != entry.LaunchStage) seen.Add(entry.LaunchStage);
        }

        return seen;
    }

    /// <summary>The headline proof: all four handshake stages reach the server, IN ORDER, on reports
    /// that carry the agent — then <c>launch_stage</c> goes null once it is Running. Pre-fix,
    /// <c>ObservedStages</c> comes back EMPTY (no report during the handshake carried the agent at
    /// all), so the first assertion fails outright rather than merely differing.</summary>
    [Test]
    public async Task Handshake_stage_stamps_reach_the_server_carrying_the_agent_then_clear_once_running() {
        using var repoPath = GitRepo.CreateWithCommit();

        var server  = new CaptureServerConnection();
        var factory = new FourStageAcpRuntimeFactory(server);

        await using var orch = AgentOrchestratorHarness.BuildOrchestrator(
            server, new SpyPtyProcessFactory(), new Dictionary<string, IHostedAgentLauncher>(),
            allowedRepoPath: repoPath, extraRuntimeFactories: [factory]);

        await orch.HandleLaunchAgentForTest(AgentOrchestratorHarness.NewCursorLaunch("stage-lane", repoPath));

        var duringHandshake = ObservedStages(server.StatusReports, "stage-lane");

        await Assert.That(duringHandshake)
            .IsEquivalentTo(FourStageAcpRuntimeFactory.Stages.Cast<string?>().ToList());

        // Now Running: a fresh report must still carry the agent (presence is the non-vacuity
        // half — "absent" would also read as a null stage) and its stage must be null.
        var agent = orch.GetAgentForTest("stage-lane");
        await Assert.That(agent).IsNotNull();
        await Assert.That(agent!.Status).IsEqualTo("Running");

        await orch.SendStatusReportNowAsync();

        var final = server.StatusReports[^1].LiveAgents.Where(a => a.Id == "stage-lane").ToList();
        await Assert.That(final.Count).IsEqualTo(1);
        await Assert.That(final[0].LaunchStage).IsNull();

        await orch.HandleStopAgentForTest("stage-lane");

    }

    /// <summary>Leak proof for the failure path: a handshake that stamps a stage and then throws must
    /// leave NO pending entry behind. The first assertion (the stage really was reported while the
    /// launch was in flight) is what stops the second from being vacuous — without it, "not reported
    /// afterwards" would also hold for a build that never tracked the launch in the first
    /// place.</summary>
    [Test]
    public async Task Failed_handshake_reports_its_stage_then_leaves_nothing_behind() {
        using var repoPath = GitRepo.CreateWithCommit();

        var server  = new CaptureServerConnection();
        var factory = new FourStageAcpRuntimeFactory(server, throwAfterFirstStage: true);

        await using var orch = AgentOrchestratorHarness.BuildOrchestrator(
            server, new SpyPtyProcessFactory(), new Dictionary<string, IHostedAgentLauncher>(),
            allowedRepoPath: repoPath, extraRuntimeFactories: [factory]);

        await orch.HandleLaunchAgentForTest(AgentOrchestratorHarness.NewCursorLaunch("stage-fail", repoPath));

        await Assert.That(ObservedStages(server.StatusReports, "stage-fail")).IsEquivalentTo(new List<string?> { "spawned" });

        // The launch failed, so no AgentInstance exists — and the pending entry must be gone too,
        // or the daemon would report a phantom live agent forever (occupying a server-side slot
        // and drawing the untracked-reviewer sweep).
        await Assert.That(orch.GetAgentForTest("stage-fail")).IsNull();
        await Assert.That(orch.BuildLiveAgents().Any(a => a.Id == "stage-fail")).IsFalse();

    }

    /// <summary>The double-publish guard, driven at the exact overlap it protects: the pending entry is
    /// STILL registered while the published agent exists (the real window between
    /// <c>PublishAgent</c> and the launch method's scope exit). The agent must appear EXACTLY ONCE —
    /// two entries with one id would double-count it in the server's capacity tally. Mutation-checked
    /// by deleting <c>BuildLiveAgents</c>' <c>_agents.ContainsKey</c> skip, which makes the middle
    /// assertion read 2.</summary>
    [Test]
    public async Task Pending_and_published_entries_never_both_describe_one_agent() {
        await using var orch = AgentOrchestratorHarness.BuildOrchestrator(
            new CaptureServerConnection(), new SpyPtyProcessFactory(), new Dictionary<string, IHostedAgentLauncher>());

        var clock   = new AgentActivityClock(TimeProvider.System);
        var pending = orch.TrackPendingLaunch("overlap-1", LaunchKind.ReviewFlow, "flow-1", "reviewer", clock);
        clock.SetLaunchStage("initialized");

        // Pending only: this is the handshake window, and the entry carries the stage.
        var beforePublish = orch.BuildLiveAgents().Where(a => a.Id == "overlap-1").ToList();
        await Assert.That(beforePublish.Count).IsEqualTo(1);
        await Assert.That(beforePublish[0].LaunchStage).IsEqualTo("initialized");
        await Assert.That(beforePublish[0].Kind).IsEqualTo("ReviewFlow");
        await Assert.That(beforePublish[0].FlowRunId).IsEqualTo("flow-1");
        await Assert.That(beforePublish[0].FlowRole).IsEqualTo("reviewer");

        // Overlap: published while the pending registration is deliberately still held.
        orch.SeedAgentForTest("overlap-1", LaunchKind.ReviewFlow, status: "Running", activityClock: clock);
        await Assert.That(orch.BuildLiveAgents().Count(a => a.Id == "overlap-1")).IsEqualTo(1);

        pending.Dispose();
        await Assert.That(orch.BuildLiveAgents().Count(a => a.Id == "overlap-1")).IsEqualTo(1);
    }
}
