using Capacitor.Cli.Core;
using Capacitor.Cli.Daemon.Services;
using Capacitor.Cli.Daemon.Tests.Unit.Pty;
using Microsoft.Extensions.Time.Testing;

namespace Capacitor.Cli.Daemon.Tests.Unit.Services;

/// <summary>
/// Task 12 (unified reviewer reaping, liveness-supervision spec §0/§1): the NO-SERVER-BOUND legacy
/// backstop — <see cref="AgentOrchestrator.FindReviewersToReap"/> reaps a Running ReviewFlow agent
/// past its lifetime/idle bound and never an interactive agent. As of Task 12 both bounds are read off
/// <see cref="AgentActivityClock"/>'s monotonic accessors (<c>AgeMs</c>/<c>IdleForMs</c>), not the
/// wall-clock <c>CreatedAt</c>/<c>LastOutputAt</c> pair this test used pre-Task-12 — so each scenario
/// below builds its own <see cref="FakeTimeProvider"/>-backed clock and passes it to
/// <c>SeedAgentForTest</c>'s new <c>activityClock</c> parameter, rather than setting independent
/// (and, under the new model, physically impossible — idle can never exceed age) wall-clock fields.
/// Partial of <see cref="AgentOrchestratorVendorTests"/> to reuse its test doubles.
/// </summary>
public class ReviewerTtlTests {
    [Test]
    public async Task FindReviewersToReap_flags_lifetime_and_idle_but_not_interactive() {
        await using var orch = AgentOrchestratorHarness.BuildOrchestrator(
            new CaptureServerConnection(), new SpyPtyProcessFactory(), new Dictionary<string, IHostedAgentLauncher>());
        // defaults: 6h lifetime / 2h idle, no server-sent bound on any of these agents.

        // Old AND still active: age past the 6h lifetime, but its most recent Advance() was just now
        // (IdleForMs ~ 0) — proves the absolute cap survives even for an agent that is not idle.
        var oldTime = new FakeTimeProvider();
        var oldClock = new AgentActivityClock(oldTime);
        oldTime.Advance(TimeSpan.FromHours(6) + TimeSpan.FromMinutes(50));
        oldClock.Advance();
        orch.SeedAgentForTest("rev-old", LaunchKind.ReviewFlow, status: "Running", activityClock: oldClock);

        // Young enough to be under the 6h TTL, but 3h with zero Advance() calls since spawn — both
        // AgeMs and IdleForMs read ~3h (age and idle can never diverge with no activity at all), which
        // is past the 2h idle bound but under the 6h lifetime.
        var idleTime = new FakeTimeProvider();
        var idleClock = new AgentActivityClock(idleTime);
        idleTime.Advance(TimeSpan.FromHours(3));
        orch.SeedAgentForTest("rev-idle", LaunchKind.ReviewFlow, status: "Running", activityClock: idleClock);

        // Interactive agent of the same age as rev-old → never reaped by this backstop regardless of
        // its clock (Kind gates it out first).
        var interactiveTime = new FakeTimeProvider();
        var interactiveClock = new AgentActivityClock(interactiveTime);
        interactiveTime.Advance(TimeSpan.FromHours(7));
        orch.SeedAgentForTest("interactive", LaunchKind.Default, status: "Running", activityClock: interactiveClock);

        // Healthy reviewer (fresh real-time clock) → left alone.
        orch.SeedAgentForTest("rev-fresh", LaunchKind.ReviewFlow, status: "Running");

        var reap = orch.FindReviewersToReap();

        await Assert.That(AgentOrchestratorHarness.Verdicts(reap)).Contains(("rev-old", "reviewer_ttl_expired"));
        await Assert.That(AgentOrchestratorHarness.Verdicts(reap)).Contains(("rev-idle", "reviewer_idle_expired"));
        await Assert.That(reap.Select(r => r.Id)).DoesNotContain("interactive");
        await Assert.That(reap.Select(r => r.Id)).DoesNotContain("rev-fresh");
    }

    /// <summary>A held turn defers the absolute lifetime cap: past the 6h lifetime but under the
    /// 6h+60m hard ceiling with a turn in flight (an actively-working reviewer mid-round) must NOT
    /// be reaped — killing it here burns the dispatched round and the reviewer's accumulated
    /// context. The wedge rule still owns a frozen turn.</summary>
    [Test]
    public async Task Held_turn_defers_the_lifetime_cap_under_the_hard_ceiling() {
        await using var orch = AgentOrchestratorHarness.BuildOrchestrator(
            new CaptureServerConnection(), new SpyPtyProcessFactory(), new Dictionary<string, IHostedAgentLauncher>());
        // defaults: 6h lifetime, 60m wedge ceiling → hard ceiling 7h.

        var time  = new FakeTimeProvider();
        var clock = new AgentActivityClock(time);
        time.Advance(TimeSpan.FromHours(6) + TimeSpan.FromMinutes(10));
        clock.SetTurnInFlight(true);
        clock.Advance(); // round input just delivered; the turn is genuinely active (idle ~0)

        orch.SeedAgentForTest("rev-mid-turn", LaunchKind.ReviewFlow, status: "Running", activityClock: clock);

        await Assert.That(orch.FindReviewersToReap().Select(r => r.Id)).DoesNotContain("rev-mid-turn");
    }

    /// <summary>The deferral is bounded: a held turn past the 6h+60m hard ceiling is reaped
    /// unfenced (<c>FencedOnActivity: false</c>) however active it is — a runaway turn that keeps
    /// advancing the seq forever must stay mortal.</summary>
    [Test]
    public async Task Held_turn_past_the_hard_ceiling_is_reaped_unfenced() {
        await using var orch = AgentOrchestratorHarness.BuildOrchestrator(
            new CaptureServerConnection(), new SpyPtyProcessFactory(), new Dictionary<string, IHostedAgentLauncher>());

        var time  = new FakeTimeProvider();
        var clock = new AgentActivityClock(time);
        time.Advance(TimeSpan.FromHours(7) + TimeSpan.FromMinutes(1));
        clock.SetTurnInFlight(true);
        clock.Advance(); // still active — activity must not defer the hard ceiling

        orch.SeedAgentForTest("rev-runaway", LaunchKind.ReviewFlow, status: "Running", activityClock: clock);

        var reap = orch.FindReviewersToReap();

        await Assert.That(AgentOrchestratorHarness.Verdicts(reap)).Contains(("rev-runaway", "reviewer_ttl_expired"));
        await Assert.That(reap.Single(r => r.Id == "rev-runaway").FencedOnActivity).IsFalse();
    }

    /// <summary>With no turn held, the lifetime candidate under the hard ceiling is fenced on
    /// activity — a delivery or output racing the sweep (a round just started) aborts the claim,
    /// and the reviewer is reaped at the first genuinely quiet sweep instead. Past the hard
    /// ceiling the candidate reverts to unfenced (the absolute backstop).</summary>
    [Test]
    public async Task Lifetime_candidate_without_a_turn_is_fenced_until_the_hard_ceiling() {
        await using var orch = AgentOrchestratorHarness.BuildOrchestrator(
            new CaptureServerConnection(), new SpyPtyProcessFactory(), new Dictionary<string, IHostedAgentLauncher>());

        var midBandTime  = new FakeTimeProvider();
        var midBandClock = new AgentActivityClock(midBandTime);
        midBandTime.Advance(TimeSpan.FromHours(6) + TimeSpan.FromMinutes(10));
        orch.SeedAgentForTest("rev-quiet", LaunchKind.ReviewFlow, status: "Running", activityClock: midBandClock);

        var pastCapTime  = new FakeTimeProvider();
        var pastCapClock = new AgentActivityClock(pastCapTime);
        pastCapTime.Advance(TimeSpan.FromHours(7) + TimeSpan.FromMinutes(1));
        orch.SeedAgentForTest("rev-past-cap", LaunchKind.ReviewFlow, status: "Running", activityClock: pastCapClock);

        var reap = orch.FindReviewersToReap();

        await Assert.That(AgentOrchestratorHarness.Verdicts(reap)).Contains(("rev-quiet", "reviewer_ttl_expired"));
        await Assert.That(reap.Single(r => r.Id == "rev-quiet").FencedOnActivity).IsTrue();
        await Assert.That(AgentOrchestratorHarness.Verdicts(reap)).Contains(("rev-past-cap", "reviewer_ttl_expired"));
        await Assert.That(reap.Single(r => r.Id == "rev-past-cap").FencedOnActivity).IsFalse();
    }

    /// <summary>A disabled wedge ceiling (<c>Zero</c>) removes the held-turn deferral rather than
    /// making it unbounded: with no frozen-turn backstop the lifetime cap keeps its original
    /// absolute, unfenced bite even mid-turn.</summary>
    [Test]
    public async Task Disabled_wedge_ceiling_restores_the_absolute_unfenced_cap() {
        await using var orch = AgentOrchestratorHarness.BuildOrchestrator(
            new CaptureServerConnection(), new SpyPtyProcessFactory(), new Dictionary<string, IHostedAgentLauncher>(),
            configure: c => c.ReviewerTurnWedgeCeiling = TimeSpan.Zero);

        var time  = new FakeTimeProvider();
        var clock = new AgentActivityClock(time);
        time.Advance(TimeSpan.FromHours(6) + TimeSpan.FromMinutes(1));
        clock.SetTurnInFlight(true);
        clock.Advance();

        orch.SeedAgentForTest("rev-no-wedge", LaunchKind.ReviewFlow, status: "Running", activityClock: clock);

        var reap = orch.FindReviewersToReap();

        await Assert.That(AgentOrchestratorHarness.Verdicts(reap)).Contains(("rev-no-wedge", "reviewer_ttl_expired"));
        await Assert.That(reap.Single(r => r.Id == "rev-no-wedge").FencedOnActivity).IsFalse();
    }

    [Test]
    public async Task FindReviewersToReap_disabled_when_bounds_are_zero() {
        await using var orch = AgentOrchestratorHarness.BuildOrchestrator(
            new CaptureServerConnection(), new SpyPtyProcessFactory(), new Dictionary<string, IHostedAgentLauncher>(),
            configure: c => { c.ReviewerMaxLifetime = TimeSpan.Zero; c.ReviewerIdleTimeout = TimeSpan.Zero; });

        var time  = new FakeTimeProvider();
        var clock = new AgentActivityClock(time);
        time.Advance(TimeSpan.FromHours(99));
        orch.SeedAgentForTest("rev", LaunchKind.ReviewFlow, status: "Running", activityClock: clock);

        await Assert.That(orch.FindReviewersToReap()).IsEmpty();
    }
}
