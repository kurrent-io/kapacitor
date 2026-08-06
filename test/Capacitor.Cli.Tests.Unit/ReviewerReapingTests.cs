using Capacitor.Cli.Core;
using Capacitor.Cli.Daemon.Services;
using Microsoft.Extensions.Time.Testing;

namespace Capacitor.Cli.Tests.Unit;

/// <summary>
/// Task 12 (unified reviewer reaping, liveness-supervision spec §0/§1):
/// <see cref="AgentOrchestrator.FindReviewersToReap"/>'s full decision table.
///
/// <para>Three coexisting rule sets fire from this one method — the server-sent-bound inactivity
/// rule, the server-sent-bound <c>turn_wedged</c> rule, and the no-bound dual legacy fallback — so
/// every test here pins WHICH rule fired (the reason string), never merely "the agent is gone",
/// per the two-guards-one-input trap: an agent absent from <c>FindReviewersToReap</c>'s result could
/// be healthy under every rule, and an agent present in it could have been flagged by the wrong one.
/// </para>
///
/// Partial of <see cref="AgentOrchestratorVendorTests"/> to reuse its <c>BuildOrchestrator</c>/
/// <c>SeedAgentForTest</c>/<c>CaptureServerConnection</c>/<c>SpyPtyProcessFactory</c> test doubles —
/// same pattern as <c>ReviewerTtlTests.cs</c>/<c>OneExecutionDomainTests.cs</c>.
/// </summary>
public partial class AgentOrchestratorVendorTests {
    /// <summary>
    /// A server-sent bound must be used INSTEAD OF the daemon's env-configured legacy knobs, not
    /// alongside them. Proven by setting the legacy knobs to values that would NEVER reap (10h, far
    /// past any test duration) while the server-sent bound is a tight 60s and the agent has been idle
    /// 5 minutes — only a build that actually branches on <see cref="AgentInstance.InactivityBoundSeconds"/>
    /// reaps this agent; a build that fell through to the legacy rules (ignoring the bound) would leave
    /// it alive under the 10h knobs.
    /// </summary>
    [Test]
    public async Task Server_sent_bound_is_honored_over_the_env_configured_legacy_values() {
        await using var orch = BuildOrchestrator(
            new CaptureServerConnection(), new SpyPtyProcessFactory(), new Dictionary<string, IHostedAgentLauncher>(),
            configure: c => {
                c.ReviewerMaxLifetime = TimeSpan.FromHours(10);
                c.ReviewerIdleTimeout = TimeSpan.FromHours(10);
            });

        var time  = new FakeTimeProvider();
        var clock = new AgentActivityClock(time);
        time.Advance(TimeSpan.FromMinutes(5)); // idle 5m — past a 60s bound, nowhere near the 10h knobs

        orch.SeedAgentForTest("bound-reviewer", LaunchKind.ReviewFlow, status: "Running",
            activityClock: clock, inactivityBoundSeconds: 60);

        var reap = orch.FindReviewersToReap();

        await Assert.That(reap).Contains(("bound-reviewer", "reviewer_inactivity_bound_exceeded"));
    }

    /// <summary>A held turn suppresses the plain inactivity rule outright — idle well past the bound,
    /// but <c>TurnInFlight</c> true and nowhere near the wedge ceiling, must not reap.</summary>
    [Test]
    public async Task TurnInFlight_defers_the_inactivity_reap() {
        await using var orch = BuildOrchestrator(
            new CaptureServerConnection(), new SpyPtyProcessFactory(), new Dictionary<string, IHostedAgentLauncher>());

        var time  = new FakeTimeProvider();
        var clock = new AgentActivityClock(time);
        clock.SetTurnInFlight(true);
        time.Advance(TimeSpan.FromMinutes(5)); // well past the 60s bound, nowhere near the 60m wedge ceiling

        orch.SeedAgentForTest("wedge-safe", LaunchKind.ReviewFlow, status: "Running",
            activityClock: clock, inactivityBoundSeconds: 60);

        await Assert.That(orch.FindReviewersToReap().Select(r => r.Id)).DoesNotContain("wedge-safe");
    }

    /// <summary>A turn held with the seq genuinely frozen (no Advance() at all since it started) past
    /// the daemon-local wedge ceiling is reaped as <c>turn_wedged</c> — independent of the server-sent
    /// bound, which a held turn always suppresses.</summary>
    [Test]
    public async Task Turn_wedged_fires_when_the_seq_stays_frozen_past_the_ceiling() {
        await using var orch = BuildOrchestrator(
            new CaptureServerConnection(), new SpyPtyProcessFactory(), new Dictionary<string, IHostedAgentLauncher>());
        // default ReviewerTurnWedgeCeiling: 60m

        var time  = new FakeTimeProvider();
        var clock = new AgentActivityClock(time);
        clock.SetTurnInFlight(true);
        time.Advance(TimeSpan.FromMinutes(61)); // past the 60m ceiling; nothing has advanced the seq since

        orch.SeedAgentForTest("wedged", LaunchKind.ReviewFlow, status: "Running",
            activityClock: clock, inactivityBoundSeconds: 300);

        var reap = orch.FindReviewersToReap();

        await Assert.That(reap).Contains(("wedged", "turn_wedged"));
    }

    /// <summary>Positive control for the wedge rule: an envelope arriving mid-turn (a genuine seq
    /// Advance()) resets the idle window, so even once MORE time has elapsed than the ceiling since
    /// the turn started, the agent is NOT wedge-reaped — only a truly frozen seq is.</summary>
    [Test]
    public async Task Seq_advance_under_a_held_turn_rearms_and_prevents_the_wedge_reap() {
        await using var orch = BuildOrchestrator(
            new CaptureServerConnection(), new SpyPtyProcessFactory(), new Dictionary<string, IHostedAgentLauncher>());

        var time  = new FakeTimeProvider();
        var clock = new AgentActivityClock(time);
        clock.SetTurnInFlight(true);
        time.Advance(TimeSpan.FromMinutes(30));
        clock.Advance(); // an envelope arrives mid-turn — genuine progress, idle resets to ~0 here
        time.Advance(TimeSpan.FromMinutes(40)); // 70m since the turn started, but only 40m since the advance

        orch.SeedAgentForTest("progressing", LaunchKind.ReviewFlow, status: "Running",
            activityClock: clock, inactivityBoundSeconds: 300);

        await Assert.That(orch.FindReviewersToReap().Select(r => r.Id)).DoesNotContain("progressing");
    }

    /// <summary>No server-sent bound (old server, or a launch predating the field): BOTH legacy rules
    /// apply, not just one — an ACTIVE agent (idle ~0, i.e. genuinely working) still dies at the 6h
    /// absolute TTL, and a separate agent under the TTL but idle past 2h dies on the idle rule. Each
    /// reason is asserted individually so the two rules are pinned as independently load-bearing.</summary>
    [Test]
    public async Task No_bound_launch_retains_both_legacy_rules_ttl_and_idle() {
        await using var orch = BuildOrchestrator(
            new CaptureServerConnection(), new SpyPtyProcessFactory(), new Dictionary<string, IHostedAgentLauncher>());
        // defaults: 6h lifetime / 2h idle

        var activeTime  = new FakeTimeProvider();
        var activeClock = new AgentActivityClock(activeTime);
        activeTime.Advance(TimeSpan.FromHours(6) + TimeSpan.FromMinutes(1));
        activeClock.Advance(); // genuinely active right up to the moment of the check — idle ~0
        orch.SeedAgentForTest("active-old", LaunchKind.ReviewFlow, status: "Running", activityClock: activeClock);

        var idleTime  = new FakeTimeProvider();
        var idleClock = new AgentActivityClock(idleTime);
        idleTime.Advance(TimeSpan.FromHours(2) + TimeSpan.FromMinutes(1)); // under 6h TTL, past 2h idle
        orch.SeedAgentForTest("idle-young", LaunchKind.ReviewFlow, status: "Running", activityClock: idleClock);

        var reap = orch.FindReviewersToReap();

        await Assert.That(reap).Contains(("active-old", "reviewer_ttl_expired"));
        await Assert.That(reap).Contains(("idle-young", "reviewer_idle_expired"));
    }

    /// <summary>
    /// The defect this task fixes: the pre-Task-12 idle rule read <see cref="AgentInstance.LastOutputAt"/>,
    /// which only PTY output chunks ever advance — for an ACP reviewer (cursor, copilot, gemini, kiro,
    /// ACP-codex) it never moves past launch, so "2h idle" silently became a hard 2h cap from birth
    /// regardless of real activity. Here an ACP-shaped agent has real activity (an envelope, standing in
    /// for transcript/turn traffic) at the 1h59m mark — under the 2h idle bound and the 6h TTL — while
    /// <c>LastOutputAt</c> is deliberately left frozen 5 REAL hours in the past (never stamped, exactly
    /// the ACP PTY-only defect). It must NOT be reaped. Mutating the idle check back to
    /// <c>now - a.LastOutputAt</c> (the old comparison) makes this fail — see the task report.
    /// </summary>
    [Test]
    public async Task ACP_agent_with_envelope_activity_at_1h59m_is_not_reaped() {
        await using var orch = BuildOrchestrator(
            new CaptureServerConnection(), new SpyPtyProcessFactory(), new Dictionary<string, IHostedAgentLauncher>());

        var time  = new FakeTimeProvider();
        var clock = new AgentActivityClock(time);
        time.Advance(TimeSpan.FromHours(1) + TimeSpan.FromMinutes(59));
        clock.Advance(); // an ACP envelope/turn transition lands right at the 1h59m mark — real evidence

        orch.SeedAgentForTest("acp-reviewer", LaunchKind.ReviewFlow, status: "Running",
            activityClock: clock,
            // LastOutputAt (PTY-only) frozen at birth, 5 real hours ago — the ACP defect this proves fixed.
            lastOutputAt: DateTime.UtcNow.AddHours(-5));

        await Assert.That(orch.FindReviewersToReap().Select(r => r.Id)).DoesNotContain("acp-reviewer");
    }

    /// <summary>Non-flow hosted agents are never touched by any of the three rule sets, however
    /// extreme their clock — the <c>LaunchKind.ReviewFlow</c> gate excludes them before any bound/TTL/
    /// idle/wedge comparison runs. Mutation-checked: removing the Kind guard makes this fail (the
    /// interactive agent, seeded with a clock that would trip EVERY rule, gets reaped).</summary>
    [Test]
    public async Task Non_flow_hosted_agents_are_unaffected_regardless_of_clock_state() {
        await using var orch = BuildOrchestrator(
            new CaptureServerConnection(), new SpyPtyProcessFactory(), new Dictionary<string, IHostedAgentLauncher>());

        var time  = new FakeTimeProvider();
        var clock = new AgentActivityClock(time);
        clock.SetTurnInFlight(true);
        time.Advance(TimeSpan.FromHours(99)); // would trip the bound, the wedge, AND both legacy rules

        orch.SeedAgentForTest("interactive-extreme", LaunchKind.Default, status: "Running",
            activityClock: clock, inactivityBoundSeconds: 60);

        // A genuine ReviewFlow agent under the identical extreme clock IS reaped, proving the guard
        // discriminates on Kind rather than the whole method being a no-op in this test.
        var time2  = new FakeTimeProvider();
        var clock2 = new AgentActivityClock(time2);
        clock2.SetTurnInFlight(true);
        time2.Advance(TimeSpan.FromHours(99));
        orch.SeedAgentForTest("reviewer-extreme", LaunchKind.ReviewFlow, status: "Running",
            activityClock: clock2, inactivityBoundSeconds: 60);

        var reap = orch.FindReviewersToReap();

        await Assert.That(reap.Select(r => r.Id)).DoesNotContain("interactive-extreme");
        await Assert.That(reap).Contains(("reviewer-extreme", "turn_wedged"));
    }

    /// <summary>End-to-end launch-path proof (as opposed to every test above, which injects the field
    /// via <c>SeedAgentForTest</c> directly): a real <see cref="LaunchAgentCommand.InactivityBoundSeconds"/>
    /// on the wire actually lands on the resulting <see cref="AgentInstance.InactivityBoundSeconds"/>
    /// through <c>HandleLaunchAgentCore</c>'s <c>AgentInstance</c> construction — closing the gap the
    /// rest of this file's <c>activityClock</c>/<c>inactivityBoundSeconds</c> seam bypasses.</summary>
    [Test, NotInParallel("LocalPermissionBridgeTests")]
    public async Task Launch_command_inactivity_bound_lands_on_the_agent_instance() {
        var (repoPath, cleanup) = CreateGitRepo();

        try {
            var server     = new CaptureServerConnection();
            var ptyFactory = new FixedPtyProcessFactory(new OneChunkThenBlockPtyProcess());

            await using var orch = BuildOrchestrator(server, ptyFactory, Launcher("codex"), allowedRepoPath: repoPath);
            var bridge = orch.PermissionBridgeForTest;
            await bridge.StartAsync(CancellationToken.None);

            try {
                await orch.HandleLaunchAgentForTest(new LaunchAgentCommand(
                    AgentId: "bound-launch", Prompt: "review", Model: "default", Effort: null,
                    RepoPath: repoPath, Tools: null, AttachmentIds: null, Vendor: "codex",
                    Kind: LaunchKind.ReviewFlow, McpAllowlist: ["kcap-review"],
                    InactivityBoundSeconds: 180));

                var agent = orch.GetAgentForTest("bound-launch");
                await Assert.That(agent).IsNotNull();
                await Assert.That(agent!.InactivityBoundSeconds).IsEqualTo(180);
            } finally {
                await bridge.DisposeAsync();
            }
        } finally {
            cleanup();
        }
    }

    /// <summary>A wall-clock jump (NTP correction, DST, a debugger-paused process resuming) must never
    /// reap a healthy reviewer — <see cref="AgentOrchestrator.FindReviewersToReap"/> must not consult
    /// <see cref="AgentOrchestrator.ClockUtc"/> (wall clock) for any of its three rule sets, only the
    /// monotonic <see cref="AgentActivityClock"/>. Mutation-checked: reintroducing a
    /// <c>ClockUtc() - AgentInstance.CreatedAt/LastOutputAt</c> comparison anywhere in the method makes
    /// this fail, since the jumped clock reads 10 years ahead of the agent's real construction time.</summary>
    [Test]
    public async Task Wall_clock_jump_does_not_reap_a_healthy_reviewer() {
        await using var orch = BuildOrchestrator(
            new CaptureServerConnection(), new SpyPtyProcessFactory(), new Dictionary<string, IHostedAgentLauncher>());

        // Healthy no-bound reviewer: fresh real-time clock, nowhere near either legacy bound.
        orch.SeedAgentForTest("healthy", LaunchKind.ReviewFlow, status: "Running");

        orch.ClockUtc = () => DateTime.UtcNow.AddYears(10);

        await Assert.That(orch.FindReviewersToReap().Select(r => r.Id)).DoesNotContain("healthy");
    }
}
