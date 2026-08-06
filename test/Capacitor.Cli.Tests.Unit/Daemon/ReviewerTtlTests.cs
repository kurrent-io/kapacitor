using Capacitor.Cli.Core;
using Capacitor.Cli.Daemon.Services;
using Microsoft.Extensions.Time.Testing;

namespace Capacitor.Cli.Tests.Unit;

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
public partial class AgentOrchestratorVendorTests {
    [Test]
    public async Task FindReviewersToReap_flags_lifetime_and_idle_but_not_interactive() {
        await using var orch = BuildOrchestrator(
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

        await Assert.That(reap).Contains(("rev-old", "reviewer_ttl_expired"));
        await Assert.That(reap).Contains(("rev-idle", "reviewer_idle_expired"));
        await Assert.That(reap.Select(r => r.Id)).DoesNotContain("interactive");
        await Assert.That(reap.Select(r => r.Id)).DoesNotContain("rev-fresh");
    }

    [Test]
    public async Task FindReviewersToReap_disabled_when_bounds_are_zero() {
        await using var orch = BuildOrchestrator(
            new CaptureServerConnection(), new SpyPtyProcessFactory(), new Dictionary<string, IHostedAgentLauncher>(),
            configure: c => { c.ReviewerMaxLifetime = TimeSpan.Zero; c.ReviewerIdleTimeout = TimeSpan.Zero; });

        var time  = new FakeTimeProvider();
        var clock = new AgentActivityClock(time);
        time.Advance(TimeSpan.FromHours(99));
        orch.SeedAgentForTest("rev", LaunchKind.ReviewFlow, status: "Running", activityClock: clock);

        await Assert.That(orch.FindReviewersToReap()).IsEmpty();
    }
}
