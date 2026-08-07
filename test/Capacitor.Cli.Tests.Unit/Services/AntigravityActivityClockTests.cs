// test/Capacitor.Cli.Tests.Unit/Services/AntigravityActivityClockTests.cs
using Capacitor.Cli.Daemon.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using static Capacitor.Cli.Tests.Unit.Services.AntigravityRuntimeFakes;

namespace Capacitor.Cli.Tests.Unit.Services;

/// <summary>
/// <see cref="AntigravityHostedAgentRuntime"/>'s wiring into <see cref="AgentActivityClock"/> — the
/// evidence <c>AgentOrchestrator.FindReviewersToReap</c> makes every reaping decision from.
///
/// <para>Both directions of <c>TurnInFlight</c> are load-bearing and neither is a style preference.
/// A flag stuck <see langword="true"/> suppresses the plain idle rule OUTRIGHT, so an idle reviewer
/// is never idle-reaped — a slot leak. A flag stuck <see langword="false"/> during a long turn lets
/// the idle rule reap a reviewer that is genuinely working. The exec-per-turn shape makes the first
/// direction the easier one to get wrong: between turns there is no process at all, so nothing
/// external ever contradicts a stale <see langword="true"/>.</para>
/// </summary>
public class AntigravityActivityClockTests {
    static readonly TimeSpan HangGuard = TimeSpan.FromSeconds(5);

    static (AntigravityHostedAgentRuntime Runtime, AgentActivityClock Clock, FakeTimeProvider Time) Wired(
            FakeTurn turn = FakeTurn.Normal) {
        var time    = new FakeTimeProvider();
        var clock   = new AgentActivityClock(time);
        var runtime = FakeRuntime(turn);
        runtime.ActivityClock = clock;

        return (runtime, clock, time);
    }

    /// <summary>Between turns there is genuinely no child, and that is exactly when the plain idle
    /// rule must be allowed to apply — no keepalive, no special case.</summary>
    [Test]
    public async Task A_settled_turn_leaves_no_turn_in_flight() {
        var (rt, clock, _) = Wired();
        await using var _r = rt;

        await rt.SendUserInputAsync("hello").WaitAsync(HangGuard);
        await rt.WaitForConversationIdAsync(CancellationToken.None).WaitAsync(HangGuard);
        await rt.WaitForTurnIdleAsync(CancellationToken.None).WaitAsync(HangGuard);

        // Positive check the turn genuinely ran, so "not in flight" cannot pass vacuously on a turn
        // that never started.
        await Assert.That(rt.AcpSessionId).IsEqualTo(FixedConversationId);
        await Assert.That(clock.TurnInFlight).IsFalse();
    }

    /// <summary>The other direction: while a turn's child is genuinely running the gate is held, so
    /// the idle rule stays suppressed and the wedge ceiling is what governs.</summary>
    [Test]
    public async Task A_running_turn_reports_the_turn_gate_as_held() {
        var (rt, clock, _) = Wired(FakeTurn.NeverEnds);
        await using var _r = rt;

        _ = rt.SendUserInputAsync("hello");

        // Resolves only once the worker has read a spawned turn's first line — i.e. the turn is
        // genuinely in flight, not merely enqueued.
        await rt.WaitForConversationIdAsync(CancellationToken.None).WaitAsync(HangGuard);

        await Assert.That(clock.TurnInFlight).IsTrue();
    }

    /// <summary>
    /// A turn that dies mid-flight (EOF with no terminal <c>result</c>) drives the runtime terminal —
    /// and must still clear the flag. Leaving it held would make the resulting agent permanently
    /// exempt from the idle rule, which is the leak shape this direction exists to prevent.
    /// </summary>
    [Test]
    public async Task A_turn_that_dies_mid_flight_still_releases_the_turn_gate_flag() {
        var (rt, clock, _) = Wired(FakeTurn.EofWithoutResult);
        await using var _r = rt;

        await rt.SendUserInputAsync("hello").WaitAsync(HangGuard);
        await rt.WaitForExitAsync(HangGuard);

        await Assert.That(rt.HasExited).IsTrue();
        await Assert.That(clock.TurnInFlight).IsFalse();
    }

    /// <summary>A stop landing while a turn is genuinely in flight must clear it too — this is the
    /// path where the turn worker may still be unwinding when the caller observes the clock.</summary>
    [Test]
    public async Task Terminating_a_running_turn_releases_the_turn_gate_flag() {
        var (rt, clock, _) = Wired(FakeTurn.NeverEnds);
        await using var _r = rt;

        _ = rt.SendUserInputAsync("hello");
        await rt.WaitForConversationIdAsync(CancellationToken.None).WaitAsync(HangGuard);
        await Assert.That(clock.TurnInFlight).IsTrue();

        await rt.TerminateAsync(TimeSpan.FromSeconds(2)).WaitAsync(HangGuard);

        await Assert.That(clock.TurnInFlight).IsFalse();
    }

    /// <summary>Every forwarded envelope is activity. Without this a reviewer streaming a long answer
    /// looks silent, and only the turn-start/turn-end pair would ever re-arm the wedge ceiling.</summary>
    [Test]
    public async Task Each_forwarded_envelope_advances_the_clock() {
        var (rt, clock, _t) = Wired();
        await using var _r = rt;

        await rt.SendUserInputAsync("hello").WaitAsync(HangGuard);
        await rt.WaitForConversationIdAsync(CancellationToken.None).WaitAsync(HangGuard);
        await rt.WaitForTurnIdleAsync(CancellationToken.None).WaitAsync(HangGuard);

        var envelopes = 0;
        while (rt.Envelopes.TryRead(out _)) envelopes++;

        await Assert.That(envelopes).IsGreaterThan(0);

        // Exact, not "greater than": a clean turn's contributions are enumerable, so an equality is
        // the only form that goes red when the per-envelope Advance() is removed — the four stamps
        // alone would still satisfy any inequality this turn could express.
        //
        //   1 — the clock's own construction value (a freshly-launched agent is never "already idle")
        //   4 — turn start, `spawned`, `session_created`, turn end
        //   + one per forwarded envelope
        await Assert.That(clock.ActivitySeq).IsEqualTo(1UL + 4UL + (ulong) envelopes);
    }

    /// <summary>
    /// The launch handshake stamps: <c>spawned</c> when turn 1's child exists, then
    /// <c>session_created</c> when its <c>init</c> resolves the conversation id. These are the
    /// evidence the out-of-cycle status report extends the server's registration wait on, and they
    /// are silent no-ops against a clock attached after the launch — which is why the factory must
    /// wire the clock before the first turn.
    /// </summary>
    [Test]
    public async Task The_first_turn_stamps_spawned_then_session_created() {
        var time    = new FakeTimeProvider();
        var clock   = new AgentActivityClock(time);
        var stages  = new List<string?>();
        clock.OnLaunchStageChanged = () => stages.Add(clock.LaunchStage);

        await using var rt = FakeRuntime();
        rt.ActivityClock = clock;

        await rt.SendUserInputAsync("hello").WaitAsync(HangGuard);
        await rt.WaitForConversationIdAsync(CancellationToken.None).WaitAsync(HangGuard);
        await rt.WaitForTurnIdleAsync(CancellationToken.None).WaitAsync(HangGuard);

        await Assert.That(string.Join(",", stages)).IsEqualTo("spawned,session_created");
    }

    /// <summary>A SECOND turn must not re-stamp a launch stage: <c>LaunchStage</c> is
    /// <c>Starting</c>-only, and the orchestrator clears it the instant the agent reaches Running, so
    /// a later stamp would resurrect a stage on a running agent.</summary>
    [Test]
    public async Task A_later_turn_does_not_re_stamp_a_launch_stage() {
        var time   = new FakeTimeProvider();
        var clock  = new AgentActivityClock(time);
        var stages = new List<string?>();

        await using var rt = FakeRuntime();
        rt.ActivityClock = clock;

        await rt.SendUserInputAsync("one").WaitAsync(HangGuard);
        await rt.WaitForConversationIdAsync(CancellationToken.None).WaitAsync(HangGuard);
        await rt.WaitForTurnIdleAsync(CancellationToken.None).WaitAsync(HangGuard);

        // Subscribe only now: everything recorded below belongs to turn 2.
        clock.OnLaunchStageChanged = () => stages.Add(clock.LaunchStage);

        // SendUserInputAndWaitForWrite, NOT WaitForTurnIdleAsync: the ack resolves once turn 2's
        // process has genuinely spawned, which is strictly after the point a `spawned` stamp would
        // fire. WaitForTurnIdleAsync's acquire-then-release can complete against a momentarily-free
        // gate before the worker has even dequeued the turn — a mutation check caught that letting
        // this assertion pass with the guard removed entirely.
        await rt.SendUserInputAndWaitForWriteAsync("two").WaitAsync(HangGuard);

        await Assert.That(stages).IsEmpty();
    }

    /// <summary>A runtime with no clock at all (every direct construction, including the lifecycle
    /// suite's) must keep working — every stamp site is a no-op guard, never a throw.</summary>
    [Test]
    public async Task A_runtime_without_a_clock_still_runs_a_turn() {
        await using var rt = FakeRuntime();

        await rt.SendUserInputAsync("hello").WaitAsync(HangGuard);
        await rt.WaitForConversationIdAsync(CancellationToken.None).WaitAsync(HangGuard);

        await Assert.That(rt.AcpSessionId).IsEqualTo(FixedConversationId);
    }
}
