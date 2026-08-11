using System.Runtime.CompilerServices;
using Capacitor.Cli.Core;
using Capacitor.Cli.Daemon.Pty;
using Capacitor.Cli.Daemon.Services;
using Microsoft.Extensions.Time.Testing;

namespace Capacitor.Cli.Tests.Unit;

/// <summary>
/// <see cref="AgentOrchestrator.FindReviewersToReap"/>'s full decision table.
///
/// <para>Three rules coexist in this one method — the 6h absolute TTL, the 60m <c>turn_wedged</c>
/// ceiling, and the 2h idle backstop — so every test here pins WHICH rule fired (the reason string),
/// never merely "the agent is gone", per the two-guards-one-input trap: an agent absent from
/// <c>FindReviewersToReap</c>'s result could be healthy under every rule, and an agent present in it
/// could have been flagged by the wrong one.</para>
///
/// <para>The server-sent <see cref="AgentInstance.InactivityBoundSeconds"/> is deliberately NOT a
/// rule here (it is round-scoped and the server owns it). Several tests below therefore seed a bound
/// specifically to prove it does nothing — they are the regression fence for the
/// reviewer-reaped-between-rounds defect.</para>
///
/// Partial of <see cref="AgentOrchestratorVendorTests"/> to reuse its <c>BuildOrchestrator</c>/
/// <c>SeedAgentForTest</c>/<c>CaptureServerConnection</c>/<c>SpyPtyProcessFactory</c> test doubles —
/// same pattern as <c>ReviewerTtlTests.cs</c>/<c>OneExecutionDomainTests.cs</c>.
/// </summary>
public partial class AgentOrchestratorVendorTests {
    /// <summary>(id, reason) projection of a selection. The decision-table tests below assert on the
    /// RULE that fired; the rest of <see cref="AgentOrchestrator.ReapCandidate"/> is claim evidence
    /// (the captured activity generation and whether the rule is activity-fenced), which the claim
    /// tests at the bottom of this file pin instead.</summary>
    static IEnumerable<(string Id, string Reason)> Verdicts(
            IEnumerable<AgentOrchestrator.ReapCandidate> selection) =>
        selection.Select(c => (c.Id, c.Reason));

    /// <summary>
    /// THE regression this fix exists for: a reviewer that has finished round 1 and is waiting while
    /// the driver spends half an hour addressing its findings emits nothing, so <c>IdleForMs</c>
    /// climbs far past the server-sent (round-scoped) 10-minute bound — and must NOT be reaped, because
    /// the daemon's own backstop is 2h/6h and neither is close. Any build that applies
    /// <see cref="AgentInstance.InactivityBoundSeconds"/> as a lifetime idle rule kills this reviewer
    /// mid-flow and lands round 2 on the heal path.
    ///
    /// <para>Mutation anchor: reinstating <c>if (a.InactivityBoundSeconds is { } b &amp;&amp; b > 0 ...)</c>
    /// before the legacy rules fails exactly this test (and its sibling below), while every other test
    /// in the file still passes — so the anchor is specific to the removed rule.</para>
    /// </summary>
    [Test]
    public async Task Reviewer_idle_30m_between_rounds_is_not_reaped_despite_a_server_sent_bound() {
        await using var orch = BuildOrchestrator(
            new CaptureServerConnection(), new SpyPtyProcessFactory(), new Dictionary<string, IHostedAgentLauncher>());
        // defaults: 6h lifetime / 2h idle / 60m wedge ceiling

        var time  = new FakeTimeProvider();
        var clock = new AgentActivityClock(time);
        // No turn is held — between rounds the reviewer has settled its last turn. Idle 30m: 3x the
        // server's bound, but well under BOTH daemon backstops.
        time.Advance(TimeSpan.FromMinutes(30));

        orch.SeedAgentForTest("between-rounds", LaunchKind.ReviewFlow, status: "Running",
            activityClock: clock, inactivityBoundSeconds: 600);

        await Assert.That(orch.FindReviewersToReap().Select(r => r.Id)).DoesNotContain("between-rounds");
    }

    /// <summary>The other half of the ownership split: a bound being present must not DISABLE the legacy
    /// backstop either (the pre-fix code returned early on any bound, so a bound-carrying reviewer was
    /// never TTL- or idle-checked at all). Both legacy rules fire here on bound-carrying agents, and each
    /// reason is asserted individually so the two remain independently load-bearing.</summary>
    [Test]
    public async Task Legacy_ttl_and_idle_still_fire_for_a_bound_carrying_reviewer() {
        await using var orch = BuildOrchestrator(
            new CaptureServerConnection(), new SpyPtyProcessFactory(), new Dictionary<string, IHostedAgentLauncher>());
        // defaults: 6h lifetime / 2h idle

        var activeTime  = new FakeTimeProvider();
        var activeClock = new AgentActivityClock(activeTime);
        activeTime.Advance(TimeSpan.FromHours(6) + TimeSpan.FromMinutes(1));
        activeClock.Advance(); // genuinely active at the moment of the check — idle ~0, only the TTL can fire
        orch.SeedAgentForTest("bound-active-old", LaunchKind.ReviewFlow, status: "Running",
            activityClock: activeClock, inactivityBoundSeconds: 600);

        var idleTime  = new FakeTimeProvider();
        var idleClock = new AgentActivityClock(idleTime);
        idleTime.Advance(TimeSpan.FromHours(2) + TimeSpan.FromMinutes(1)); // under 6h TTL, past 2h idle
        orch.SeedAgentForTest("bound-idle-young", LaunchKind.ReviewFlow, status: "Running",
            activityClock: idleClock, inactivityBoundSeconds: 600);

        var reap = orch.FindReviewersToReap();

        await Assert.That(Verdicts(reap)).Contains(("bound-active-old", "reviewer_ttl_expired"));
        await Assert.That(Verdicts(reap)).Contains(("bound-idle-young", "reviewer_idle_expired"));
    }

    /// <summary>A held turn suppresses the plain idle rule outright — idle 5m with <c>TurnInFlight</c>
    /// true and nowhere near the wedge ceiling must not reap. (Also inert against the removed bound
    /// rule: 5m is past the seeded 60s bound.)</summary>
    [Test]
    public async Task TurnInFlight_defers_the_idle_reap() {
        await using var orch = BuildOrchestrator(
            new CaptureServerConnection(), new SpyPtyProcessFactory(), new Dictionary<string, IHostedAgentLauncher>());

        var time  = new FakeTimeProvider();
        var clock = new AgentActivityClock(time);
        clock.SetTurnInFlight(true);
        time.Advance(TimeSpan.FromMinutes(5)); // nowhere near the 60m wedge ceiling or the 2h idle rule

        orch.SeedAgentForTest("wedge-safe", LaunchKind.ReviewFlow, status: "Running",
            activityClock: clock, inactivityBoundSeconds: 60);

        await Assert.That(orch.FindReviewersToReap().Select(r => r.Id)).DoesNotContain("wedge-safe");
    }

    /// <summary>A turn held with the seq genuinely frozen (no Advance() at all since it started) past
    /// the daemon-local wedge ceiling is reaped as <c>turn_wedged</c>. 61m is past the 60m ceiling but
    /// well under the 6h TTL, so the reason pins the wedge rule specifically. The seeded bound is
    /// irrelevant to it — the wedge is a genuine daemon-local wedge detector, not a round-scoped rule.</summary>
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

        await Assert.That(Verdicts(reap)).Contains(("wedged", "turn_wedged"));
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
    /// reason is asserted individually so the two rules are pinned as independently load-bearing.
    /// The bound-carrying twin of this test is
    /// <see cref="Legacy_ttl_and_idle_still_fire_for_a_bound_carrying_reviewer"/> — together they pin
    /// that the presence or absence of a bound changes nothing.</summary>
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

        await Assert.That(Verdicts(reap)).Contains(("active-old", "reviewer_ttl_expired"));
        await Assert.That(Verdicts(reap)).Contains(("idle-young", "reviewer_idle_expired"));
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

    /// <summary>Non-flow hosted agents are never touched by any rule, however extreme their clock —
    /// the <c>LaunchKind.ReviewFlow</c> gate excludes them before any TTL/idle/wedge comparison runs.
    /// Mutation-checked: removing the Kind guard makes this fail (the interactive agent, seeded with a
    /// clock that would trip EVERY rule, gets reaped).</summary>
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
        // discriminates on Kind rather than the whole method being a no-op in this test. At 99h the
        // absolute TTL is the first rule that matches, so that — not the wedge — is the reason.
        var time2  = new FakeTimeProvider();
        var clock2 = new AgentActivityClock(time2);
        clock2.SetTurnInFlight(true);
        time2.Advance(TimeSpan.FromHours(99));
        orch.SeedAgentForTest("reviewer-extreme", LaunchKind.ReviewFlow, status: "Running",
            activityClock: clock2, inactivityBoundSeconds: 60);

        var reap = orch.FindReviewersToReap();

        await Assert.That(reap.Select(r => r.Id)).DoesNotContain("interactive-extreme");
        await Assert.That(Verdicts(reap)).Contains(("reviewer-extreme", "reviewer_ttl_expired"));
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

    // ── The atomic reap claim (round-dispatch grace §3) ──────────────────────────────────────
    //
    // Everything above judges SELECTION. These judge the CLAIM, which is the actual decision:
    // FindReviewersToReap reads a snapshot and the teardown happens later, so a reviewer that
    // receives a round in between must survive. The fence is the per-agent BorrowedSnapshotGate —
    // already held across every vendor's whole delivery body — so the delivery's clock advance and
    // the reaper's claim are mutually exclusive and exactly one side wins.
    //
    // Bounds are load-bearing in all three: idle past ReviewerIdleTimeout (2h) with age under
    // ReviewerMaxLifetime (6h), i.e. the daemon's OWN backstops. The server's round bound is not a
    // rule here at all (see the top of this file), so no test may lean on it.

    /// <summary>The stale-selection case, in its natural order: the sweep selects an idle-expired
    /// reviewer, THEN the driver's next round is delivered, THEN the pending reap runs. The reap must
    /// abort — the "nothing has happened for 2h" claim it was selected on is now false.
    ///
    /// <para>Mutation anchor: dropping the generation re-validation from <c>TryClaimReapAsync</c>
    /// fails exactly here (the reviewer is stopped seconds after being handed round 2), while every
    /// selection test above still passes — selection is not what changed.</para></summary>
    [Test]
    public async Task Stale_reap_selection_aborts_after_delivery() {
        var server = new CaptureServerConnection();
        var time   = new FakeTimeProvider();
        var clock  = new AgentActivityClock(time);

        await using var orch = BuildOrchestrator(
            server, new SpyPtyProcessFactory(), new Dictionary<string, IHostedAgentLauncher>());
        // defaults: 6h lifetime / 2h idle / 60m wedge ceiling

        var agent = orch.SeedAgentForTest("stale-reap", LaunchKind.ReviewFlow, status: "Running",
            pty: new RecordingPtyProcess(), activityClock: clock);

        time.Advance(TimeSpan.FromHours(2) + TimeSpan.FromMinutes(1)); // past 2h idle, far under 6h TTL

        var candidate = orch.FindReviewersToReap().Single(c => c.Id == "stale-reap");
        // WHICH rule selected it, not merely that it was selected: only an activity-fenced rule may
        // abort, so a test that accidentally selected on the TTL would prove the opposite of the point.
        await Assert.That(candidate.Reason).IsEqualTo("reviewer_idle_expired");
        await Assert.That(candidate.FencedOnActivity).IsTrue();

        // ...and now round 2 lands, between selection and teardown.
        await orch.HandleSendInputForTest(new SendInputCommand(agent.Id, "round 2", null));
        await Assert.That(agent.ActivityClock.ActivitySeq).IsEqualTo(candidate.ActivityGeneration + 1);

        await orch.ReapReviewerForTest(candidate);

        await Assert.That(agent.ReapClaimed).IsFalse();
        await Assert.That(agent.Status).IsEqualTo("Running");
        // The end-reason stamp belongs to a WON claim: an aborted reap must not leave a reap reason on
        // a live agent for whatever ends it later to report.
        await Assert.That(agent.PendingEndReason).IsEqualTo("agent_exited");
        await Assert.That(server.StatusChangedCalls).DoesNotContain(("stale-reap", "Completed"));
    }

    /// <summary>Contention AT the claim boundary — both orders, on two agents whose situations are
    /// otherwise identical, so neither outcome can come from anything but who reached the section
    /// first.
    ///
    /// <para><b>Delivery first:</b> the delivery is parked mid-write while holding the section. The
    /// reap cannot even begin to validate until the delivery releases it (asserted directly: the reap
    /// task is still incomplete while the write is parked — that is the mutual exclusion), and by then
    /// the clock has advanced, so it aborts.</para>
    ///
    /// <para><b>Reap first:</b> the claim wins the section, and the delivery that follows refuses —
    /// nothing is written to the runtime, the clock does not advance, and no out-of-cycle report is
    /// emitted for it. The round's dispatch fails there, which is the intended loss: the server heals
    /// it on resubmit, whereas a delivery quietly "succeeding" into an agent already being torn down
    /// would leave the round waiting on a corpse.</para></summary>
    [Test]
    public async Task Reap_claim_contention_has_exactly_one_winner() {
        var server = new CaptureServerConnection();

        await using var orch = BuildOrchestrator(
            server, new SpyPtyProcessFactory(), new Dictionary<string, IHostedAgentLauncher>());

        // ── delivery first ──────────────────────────────────────────────────────────────────
        var deliveryTime  = new FakeTimeProvider();
        var deliveryClock = new AgentActivityClock(deliveryTime);
        var parking       = new ParkingPtyProcess();

        var delivered = orch.SeedAgentForTest("race-delivery-wins", LaunchKind.ReviewFlow, status: "Running",
            pty: parking, activityClock: deliveryClock);

        deliveryTime.Advance(TimeSpan.FromHours(2) + TimeSpan.FromMinutes(1));

        var deliveryCandidate = orch.FindReviewersToReap().Single(c => c.Id == "race-delivery-wins");
        await Assert.That(deliveryCandidate.Reason).IsEqualTo("reviewer_idle_expired");

        var delivery = orch.HandleSendInputForTest(new SendInputCommand(delivered.Id, "round 2", null));
        await parking.FirstWriteEntered.WaitAsync(TimeSpan.FromSeconds(5)); // provably inside the section

        var racingReap = orch.ReapReviewerForTest(deliveryCandidate);
        await Task.Delay(100);

        // Mutual exclusion, asserted as such: a reaper that did not enter the section would have
        // validated and stopped this agent inside that window.
        await Assert.That(racingReap.IsCompleted).IsFalse();
        await Assert.That(delivered.Status).IsEqualTo("Running");

        parking.ReleaseFirstWrite();
        await delivery.WaitAsync(TimeSpan.FromSeconds(5));
        await racingReap.WaitAsync(TimeSpan.FromSeconds(5));

        await Assert.That(delivered.ActivityClock.ActivitySeq)
            .IsEqualTo(deliveryCandidate.ActivityGeneration + 1); // the delivery landed
        await Assert.That(delivered.ReapClaimed).IsFalse();       // ...so the reap lost
        await Assert.That(delivered.Status).IsEqualTo("Running");
        await Assert.That(server.StatusChangedCalls).DoesNotContain(("race-delivery-wins", "Completed"));

        // The winning delivery's own out-of-cycle report is fire-and-forget, so wait for it HERE —
        // otherwise it lands mid-arm-2 and pollutes that arm's "no report was emitted" baseline.
        await PollUntilAsync(() => server.StatusReportCount >= 1);

        // ── reap first ──────────────────────────────────────────────────────────────────────
        var reapTime  = new FakeTimeProvider();
        var reapClock = new AgentActivityClock(reapTime);
        var recording = new RecordingPtyProcess();

        var condemned = orch.SeedAgentForTest("race-reap-wins", LaunchKind.ReviewFlow, status: "Running",
            pty: recording, activityClock: reapClock);

        reapTime.Advance(TimeSpan.FromHours(2) + TimeSpan.FromMinutes(1));

        var reapCandidate = orch.FindReviewersToReap().Single(c => c.Id == "race-reap-wins");
        await Assert.That(reapCandidate.Reason).IsEqualTo("reviewer_idle_expired");

        // The claim alone — the teardown it gates is not what this arm is about.
        await Assert.That(await orch.TryClaimReapForTest(reapCandidate)).IsTrue();
        await Assert.That(condemned.ReapClaimed).IsTrue();

        var seqAtClaim   = condemned.ActivityClock.ActivitySeq;
        var reportsAtClaim = server.StatusReportCount;

        await orch.HandleSendInputForTest(new SendInputCommand(condemned.Id, "too late", null));

        await Assert.That(recording.Writes).IsEmpty();                          // nothing reached the runtime
        await Assert.That(condemned.ActivityClock.ActivitySeq).IsEqualTo(seqAtClaim);
        await Task.Delay(100); // the delivery report is fire-and-forget; give a wrong one time to land
        await Assert.That(server.StatusReportCount).IsEqualTo(reportsAtClaim);
    }

    /// <summary>The fence is scoped to the "nothing has happened" rules. The 6h absolute cap is not one
    /// of them: it reaps a reviewer that is demonstrably active — here one that took a round AFTER
    /// selection, advancing the very generation the idle/wedge rules would have aborted on — because
    /// "absolute" is exactly what it means. Without the scoping, the one rule that guarantees a leaked
    /// daemon slot is eventually reclaimed becomes indefinitely deferrable by traffic.</summary>
    [Test]
    public async Task Max_lifetime_reaps_regardless_of_delivery() {
        var server = new CaptureServerConnection();
        var time   = new FakeTimeProvider();
        var clock  = new AgentActivityClock(time);

        await using var orch = BuildOrchestrator(
            server, new SpyPtyProcessFactory(), new Dictionary<string, IHostedAgentLauncher>());

        var agent = orch.SeedAgentForTest("ttl-reap", LaunchKind.ReviewFlow, status: "Running",
            pty: new RecordingPtyProcess(), activityClock: clock);

        time.Advance(TimeSpan.FromHours(6) + TimeSpan.FromMinutes(1)); // past the 6h absolute cap

        var candidate = orch.FindReviewersToReap().Single(c => c.Id == "ttl-reap");
        await Assert.That(candidate.Reason).IsEqualTo("reviewer_ttl_expired");
        await Assert.That(candidate.FencedOnActivity).IsFalse();

        await orch.HandleSendInputForTest(new SendInputCommand(agent.Id, "round 7", null));
        await Assert.That(agent.ActivityClock.ActivitySeq)
            .IsEqualTo(candidate.ActivityGeneration + 1); // the exact advance that aborts an idle reap

        await orch.ReapReviewerForTest(candidate);

        await Assert.That(agent.ReapClaimed).IsTrue();
        await Assert.That(agent.PendingEndReason).IsEqualTo("reviewer_ttl_expired");
        await Assert.That(server.StatusChangedCalls).Contains(("ttl-reap", "Completed"));
    }

    /// <summary>PTY double that PARKS inside its first write, holding whatever section its caller is in
    /// until the test releases it. Later writes (the submit CR, and anything the stop path sends) pass
    /// straight through, so only the one window under test is controlled.</summary>
    sealed class ParkingPtyProcess : IPtyProcess {
        readonly TaskCompletionSource _entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        readonly TaskCompletionSource _release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        int                           _writes;

        /// <summary>Completes the instant the first write is entered — the test drives the racing reap
        /// from exactly that window rather than from a timing guess.</summary>
        public Task FirstWriteEntered => _entered.Task;

        public void ReleaseFirstWrite() => _release.TrySetResult();

        public int  Pid       => 5152;
        public bool HasExited => false;
        public int? ExitCode  => null;

        public ValueTask DisposeAsync() => default;
        public Task WaitForExitAsync(TimeSpan? _) => Task.CompletedTask;
        public Task TerminateAsync(TimeSpan?   _) => Task.CompletedTask;

#pragma warning disable CS1998
        public async IAsyncEnumerable<byte[]> ReadOutputAsync([EnumeratorCancellation] CancellationToken _ = default) {
            yield break;
        }
#pragma warning restore CS1998

        public async Task WriteAsync(string _) {
            if (Interlocked.Increment(ref _writes) != 1) return;

            _entered.TrySetResult();
            await _release.Task;
        }

        public Task WriteAsync(byte[] _) => Task.CompletedTask;

        public void Resize(ushort _, ushort __) { }
        public void SendInterrupt() { }
    }
}
