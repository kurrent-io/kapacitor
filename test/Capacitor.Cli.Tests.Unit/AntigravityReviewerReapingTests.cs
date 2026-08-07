// test/Capacitor.Cli.Tests.Unit/AntigravityReviewerReapingTests.cs
using Capacitor.Cli.Core;
using Capacitor.Cli.Daemon.Services;
using Capacitor.Cli.Tests.Unit.Services;
using Microsoft.Extensions.Time.Testing;
using static Capacitor.Cli.Tests.Unit.Services.AntigravityRuntimeFakes;

namespace Capacitor.Cli.Tests.Unit;

/// <summary>
/// The exec-per-turn runtime's clock, judged by the REAL reaper
/// (<see cref="AgentOrchestrator.FindReviewersToReap"/>) rather than by reading
/// <see cref="AgentActivityClock.TurnInFlight"/> back. The unit tests in
/// <see cref="AntigravityActivityClockTests"/> pin the flag; these pin what the flag DOES, which is
/// the thing that actually leaks a daemon slot or kills a working reviewer.
///
/// <para>Between turns this runtime has no process at all. That needs no special case and no
/// keepalive: <c>TurnInFlight</c> is false, so <c>ReviewerIdleTimeout</c> governs exactly as it does
/// for any ACP reviewer waiting on the next round.</para>
///
/// Partial of <see cref="AgentOrchestratorVendorTests"/> to reuse its <c>BuildOrchestrator</c> /
/// <c>CaptureServerConnection</c> / <c>SpyPtyProcessFactory</c> doubles — the same pattern
/// <c>ReviewerReapingTests.cs</c> follows.
/// </summary>
public partial class AgentOrchestratorVendorTests {
    static readonly TimeSpan AntigravityHangGuard = TimeSpan.FromSeconds(5);

    /// <summary>
    /// Between rounds the reviewer is a runtime with NO child process, and it must be idle-reapable
    /// on the ordinary 2h rule. A build that left <c>TurnInFlight</c> held after a settled turn would
    /// suppress that rule outright and the slot would never be reclaimed.
    /// </summary>
    [Test]
    public async Task Antigravity_reviewer_between_turns_is_idle_reaped_like_any_other() {
        await using var orch = BuildOrchestrator(
            new CaptureServerConnection(), new SpyPtyProcessFactory(), new Dictionary<string, IHostedAgentLauncher>());

        var time  = new FakeTimeProvider();
        var clock = new AgentActivityClock(time);

        await using var rt = FakeRuntime();
        rt.ActivityClock = clock;

        await rt.SendUserInputAsync("round 1").WaitAsync(AntigravityHangGuard);
        await rt.WaitForConversationIdAsync(CancellationToken.None).WaitAsync(AntigravityHangGuard);
        await rt.WaitForTurnIdleAsync(CancellationToken.None).WaitAsync(AntigravityHangGuard);

        // The turn genuinely ran, so "no turn in flight" is a settled fact rather than a turn that
        // never started.
        await Assert.That(rt.AcpSessionId).IsEqualTo(FixedConversationId);

        time.Advance(TimeSpan.FromHours(2) + TimeSpan.FromMinutes(1)); // past the 2h idle rule, under the 6h TTL

        orch.SeedAgentForTest("agy-between-turns", LaunchKind.ReviewFlow, status: "Running", activityClock: clock);

        // The REASON, not merely "it is in the list": at this elapsed time the TTL rule cannot fire,
        // so naming the idle rule pins that the plain idle path was reached at all.
        await Assert.That(orch.FindReviewersToReap()).Contains(("agy-between-turns", "reviewer_idle_expired"));
    }

    /// <summary>
    /// The opposite direction, and the reason the flag cannot simply be pinned false: a turn that is
    /// genuinely running SUPPRESSES the idle rule, so a long review round is not reaped out from under
    /// itself. The settled twin is the control — identical elapsed time, opposite verdict, so neither
    /// outcome can come from the elapsed time alone.
    ///
    /// <para>The wedge ceiling is disabled for this orchestrator deliberately. It is the one rule a
    /// held turn leaves armed, so with it in force the mid-turn agent at 2h1m would be reaped as
    /// <c>turn_wedged</c> — a different rule reaching the same verdict, which would prove nothing
    /// about idle suppression. An earlier revision instead advanced the running clock only 30m, and a
    /// mutation check showed that made the assertion vacuous: 30m is under the idle bound anyway, so
    /// it passed with the flag never set at all. The wedge itself is covered by
    /// <c>ReviewerReapingTests</c>.</para>
    /// </summary>
    [Test]
    public async Task Antigravity_reviewer_mid_turn_is_not_idle_reaped_while_a_settled_one_is() {
        await using var orch = BuildOrchestrator(
            new CaptureServerConnection(), new SpyPtyProcessFactory(), new Dictionary<string, IHostedAgentLauncher>(),
            configure: c => c.ReviewerTurnWedgeCeiling = TimeSpan.Zero);

        var runningTime  = new FakeTimeProvider();
        var runningClock = new AgentActivityClock(runningTime);

        await using var running = FakeRuntime(FakeTurn.NeverEnds);
        running.ActivityClock = runningClock;

        _ = running.SendUserInputAsync("a long round");
        await running.WaitForConversationIdAsync(CancellationToken.None).WaitAsync(AntigravityHangGuard);

        var settledTime  = new FakeTimeProvider();
        var settledClock = new AgentActivityClock(settledTime);

        await using var settled = FakeRuntime();
        settled.ActivityClock = settledClock;

        await settled.SendUserInputAsync("round 1").WaitAsync(AntigravityHangGuard);
        await settled.WaitForConversationIdAsync(CancellationToken.None).WaitAsync(AntigravityHangGuard);
        await settled.WaitForTurnIdleAsync(CancellationToken.None).WaitAsync(AntigravityHangGuard);

        // IDENTICAL elapsed time on both, past the 2h idle rule and under the 6h TTL. The only thing
        // that differs between the two agents is whether a turn is in flight.
        var elapsed = TimeSpan.FromHours(2) + TimeSpan.FromMinutes(1);
        settledTime.Advance(elapsed);
        runningTime.Advance(elapsed);

        orch.SeedAgentForTest("agy-mid-turn", LaunchKind.ReviewFlow, status: "Running", activityClock: runningClock);
        orch.SeedAgentForTest("agy-settled",  LaunchKind.ReviewFlow, status: "Running", activityClock: settledClock);

        var reap = orch.FindReviewersToReap();

        await Assert.That(reap.Select(r => r.Id)).DoesNotContain("agy-mid-turn");
        await Assert.That(reap).Contains(("agy-settled", "reviewer_idle_expired"));
    }
}
