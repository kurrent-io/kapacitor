using System.Net.Http.Json;
using System.Runtime.CompilerServices;
using System.Text;
using Capacitor.Cli.Core;
using Capacitor.Cli.Daemon.Acp;
using Capacitor.Cli.Daemon.Pty;
using Capacitor.Cli.Daemon.Services;
using Capacitor.Cli.Daemon.Tests.Unit.Acp;
using Capacitor.Cli.Daemon.Tests.Unit.Pty;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using TUnit.Assertions.Enums;

namespace Capacitor.Cli.Daemon.Tests.Unit.Services;

/// <summary>
/// Liveness-supervision spec §0/§1: <see cref="AgentActivityClock"/>'s own semantics (this class),
/// plus proof that each of the four daemon-local sources — PTY output chunk, ACP transcript envelope,
/// ACP turn transition, and <see cref="LocalPermissionBridge"/> reviewer tool-call hit — independently
/// advances a launch's clock (the <see cref="AgentActivityClockOrchestratorTests"/> below,
/// and <see cref="ActivityClockTurnAndEnvelopeWiringTests"/>, and the standalone
/// <see cref="LocalPermissionBridgeActivityWiringTests"/>).
///
/// Each wiring test is built so the OTHER three sources genuinely never fire in that test's run
/// (never merely "wasn't asserted on") — see each test's own remarks for why — and each is proven to
/// fail when its one production wiring line is removed (verified by hand during development; see the
/// task report).
/// </summary>
public class AgentActivityClockTests {
    // ── Pure clock semantics ─────────────────────────────────────────────────────────────────────

    [Test]
    public async Task ActivitySeq_starts_at_1_so_a_freshly_spawned_agent_is_never_already_idle() {
        var clock = new AgentActivityClock(new FakeTimeProvider());

        await Assert.That(clock.ActivitySeq).IsEqualTo(1UL);
    }

    [Test]
    public async Task Advance_bumps_the_seq_and_resets_idle_to_zero() {
        var time  = new FakeTimeProvider();
        var clock = new AgentActivityClock(time);

        time.Advance(TimeSpan.FromSeconds(10));
        await Assert.That(clock.IdleForMs).IsGreaterThanOrEqualTo(9_900UL);

        clock.Advance();

        await Assert.That(clock.ActivitySeq).IsEqualTo(2UL);
        await Assert.That(clock.IdleForMs).IsEqualTo(0UL);
    }

    /// <summary>
    /// The §0 invariant the whole feature leans on: <see cref="AgentActivityClock.IdleForMs"/> is
    /// read from the MONOTONIC clock only, never wall-clock. <see cref="FakeTimeProvider"/> ties its
    /// monotonic timestamp and its <c>UtcNow</c> to the same simulated instant, so it cannot exercise
    /// this — <see cref="DecoupledTimeProvider"/> deliberately keeps the two axes independent. Mutate
    /// <see cref="AgentActivityClock.IdleForMs"/> to compute from <c>GetUtcNow()</c> deltas instead of
    /// <c>GetElapsedTime</c>/<c>GetTimestamp</c> and this test fails on the second assertion.
    /// </summary>
    [Test]
    public async Task IdleForMs_is_monotonic_based_and_unaffected_by_a_wall_clock_jump() {
        var time  = new DecoupledTimeProvider();
        var clock = new AgentActivityClock(time);

        time.AdvanceMonotonic(TimeSpan.FromSeconds(5));
        var idleAfterMonotonicAdvance = clock.IdleForMs;
        await Assert.That(idleAfterMonotonicAdvance).IsGreaterThanOrEqualTo(4_900UL);

        // A wall-clock jump with NO monotonic advance must leave IdleForMs exactly where it was.
        time.JumpWallClock(TimeSpan.FromDays(400));
        await Assert.That(clock.IdleForMs).IsEqualTo(idleAfterMonotonicAdvance);
    }

    [Test]
    public async Task Turn_transitions_set_and_clear_TurnInFlight_and_advance_the_seq() {
        var clock = new AgentActivityClock(new FakeTimeProvider());

        clock.SetTurnInFlight(true);
        await Assert.That(clock.TurnInFlight).IsTrue();
        await Assert.That(clock.ActivitySeq).IsEqualTo(2UL);

        clock.SetTurnInFlight(false);
        await Assert.That(clock.TurnInFlight).IsFalse();
        await Assert.That(clock.ActivitySeq).IsEqualTo(3UL);
    }

    [Test]
    public async Task LaunchStage_transitions_advance_the_seq_and_the_stage_clears_on_Running() {
        var clock = new AgentActivityClock(new FakeTimeProvider());

        clock.SetLaunchStage("spawned");
        await Assert.That(clock.LaunchStage).IsEqualTo("spawned");
        await Assert.That(clock.ActivitySeq).IsEqualTo(2UL);

        clock.SetLaunchStage("initialized");
        await Assert.That(clock.LaunchStage).IsEqualTo("initialized");
        await Assert.That(clock.ActivitySeq).IsEqualTo(3UL);

        clock.ClearLaunchStage(); // the agent reached Running
        await Assert.That(clock.LaunchStage).IsNull();
        await Assert.That(clock.ActivitySeq).IsEqualTo(4UL);
    }

    [Test]
    public async Task A_turns_falling_edge_marks_awaiting_input_and_the_next_rising_edge_clears_it() {
        var clock = new AgentActivityClock(new FakeTimeProvider());
        var seen  = new List<bool>();
        clock.OnAwaitingInputChanged = v => seen.Add(v);

        await Assert.That(clock.AwaitingInput).IsFalse();
        clock.SetTurnInFlight(true);
        await Assert.That(clock.AwaitingInput).IsFalse();
        clock.SetTurnInFlight(false);
        await Assert.That(clock.AwaitingInput).IsTrue();
        clock.SetTurnInFlight(true);
        await Assert.That(clock.AwaitingInput).IsFalse();

        await Assert.That(seen).IsEquivalentTo([true, false], CollectionOrdering.Matching);
    }

    /// A gate cleared without ever being held (a runtime going terminal) is not a turn ending.
    [Test]
    public async Task Clearing_a_gate_that_was_never_held_does_not_mark_awaiting_input() {
        var clock = new AgentActivityClock(new FakeTimeProvider());

        clock.SetTurnInFlight(false);

        await Assert.That(clock.AwaitingInput).IsFalse();
    }

    /// The explicit setter is the hook-relay and input-delivery path: it moves the flag alone,
    /// never the activity evidence the reaper reads, and notifies only on a real change.
    [Test]
    public async Task SetAwaitingInput_moves_only_the_flag_and_notifies_once_per_change() {
        var clock    = new AgentActivityClock(new FakeTimeProvider());
        var notified = 0;
        clock.OnAwaitingInputChanged = _ => notified++;

        clock.SetAwaitingInput(true);
        clock.SetAwaitingInput(true);
        await Assert.That(clock.AwaitingInput).IsTrue();
        await Assert.That(notified).IsEqualTo(1);
        await Assert.That(clock.ActivitySeq).IsEqualTo(1UL);

        clock.SetAwaitingInput(false);
        await Assert.That(clock.AwaitingInput).IsFalse();
        await Assert.That(notified).IsEqualTo(2);
    }

    /// A delivery clears the wait it was answering, never one that began while the delivery was
    /// still in flight.
    [Test]
    public async Task Clearing_since_a_sample_yields_to_a_wait_that_began_afterwards() {
        var clock = new AgentActivityClock(new FakeTimeProvider());
        clock.SetAwaitingInput(true);

        var sampled = clock.WaitGeneration;
        clock.ClearAwaitingInputSince(sampled);
        await Assert.That(clock.AwaitingInput).IsFalse();

        var stale = clock.WaitGeneration;
        clock.SetTurnInFlight(true);
        clock.SetTurnInFlight(false);
        clock.ClearAwaitingInputSince(stale);
        await Assert.That(clock.AwaitingInput).IsTrue();
    }

    /// <summary>
    /// A minimal <see cref="TimeProvider"/> whose monotonic timestamp and wall clock are deliberately
    /// independent axes — every real implementation (including <see cref="FakeTimeProvider"/>) ties
    /// the two to one simulated instant, which would make it structurally impossible to prove the §0
    /// isolation <see cref="AgentActivityClock.IdleForMs"/> depends on.
    /// </summary>
    sealed class DecoupledTimeProvider : TimeProvider {
        long           _timestamp;
        DateTimeOffset _utcNow = DateTimeOffset.FromUnixTimeSeconds(0);

        public override long           GetTimestamp() => _timestamp;
        public override DateTimeOffset GetUtcNow()     => _utcNow;

        public void AdvanceMonotonic(TimeSpan by) => _timestamp += (long) (by.TotalSeconds * TimestampFrequency);
        public void JumpWallClock(TimeSpan    by) => _utcNow    += by;
    }
}

/// <summary>
/// PTY-source wiring (liveness-supervision spec §1): uses <see cref="AgentOrchestratorHarness"/>
/// for its <c>BuildOrchestrator</c>/<c>SeedAgentForTest</c>/<c>ReadAgentOutputForTest</c> harness —
/// same pattern as <c>AgentOrchestratorConsentDialogTests</c>/<c>AgentOrchestratorBracketedPasteTests</c>.
/// </summary>
public class AgentActivityClockOrchestratorTests {
    /// <summary>Emits exactly one output chunk then completes — no other agent activity (no ACP
    /// runtime, no turn, no permission-bridge hit) exists anywhere in this test, so a seq bump can
    /// only have come from the PTY chunk site.</summary>
    sealed class OneShotChunkPtyProcess(byte[] chunk) : IPtyProcess {
        public int  Pid       => 4141;
        public bool HasExited => true;
        public int? ExitCode  => 0;

        public ValueTask DisposeAsync()               => default;
        public Task      WaitForExitAsync(TimeSpan? _) => Task.CompletedTask;
        public Task      TerminateAsync(TimeSpan?   _) => Task.CompletedTask;

#pragma warning disable CS1998
        public async IAsyncEnumerable<byte[]> ReadOutputAsync([EnumeratorCancellation] CancellationToken ct = default) {
            yield return chunk;
        }
#pragma warning restore CS1998

        public Task WriteAsync(string _) => Task.CompletedTask;
        public Task WriteAsync(byte[] _) => Task.CompletedTask;
        public void Resize(ushort     _, ushort __) { }
        public void SendInterrupt() { }
    }

    [Test]
    public async Task PTY_output_chunk_advances_the_agents_activity_clock() {
        var server = new CaptureServerConnection();

        await using var orch = AgentOrchestratorHarness.BuildOrchestrator(server, new SpyPtyProcessFactory(), new Dictionary<string, IHostedAgentLauncher>());

        var agent = orch.SeedAgentForTest(
            "pty-activity", isPrivate: true, pty: new OneShotChunkPtyProcess(Encoding.UTF8.GetBytes("hello")));

        await orch.ReadAgentOutputForTest(agent).WaitAsync(TimeSpan.FromSeconds(10));

        // Spawn (1) + the one PTY chunk (2) — nothing else in this test can have touched the clock.
        await Assert.That(agent.ActivityClock.ActivitySeq).IsEqualTo(2UL);
    }
}

/// <summary>
/// ACP-source wiring (liveness-supervision spec §1): envelope ingest and turn start/end, exercised
/// against a real <see cref="AcpHostedAgentRuntime"/> and <see cref="FakeAcpAgent"/> — same harness
/// shape as <c>AcpTranscriptAggregationTests</c>, built fresh here so each test can independently
/// control whether a turn ever runs at all.
/// </summary>
public class ActivityClockTurnAndEnvelopeWiringTests {
    static readonly TimeSpan HangGuard = TimeSpan.FromSeconds(5);

    sealed class FakeAcpProcess : IAcpProcess {
        public int  Pid       { get; init; } = 4242;
        public bool HasExited { get; private set; }
        public int? ExitCode  { get; private set; }

        public Task WaitForExitAsync(TimeSpan? timeout = null) =>
            timeout is { } t ? Task.Delay(t) : Task.Delay(Timeout.InfiniteTimeSpan);

        public Task TerminateAsync(TimeSpan? timeout = null) {
            HasExited = true;
            ExitCode  = 0;
            return Task.CompletedTask;
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    sealed class Harness : IAsyncDisposable {
        public FakeAcpAgent          Fake    { get; }
        public AcpConnection         Conn    { get; }
        public FakeAcpProcess        Process { get; }
        public AcpHostedAgentRuntime Runtime { get; }
        public CancellationTokenSource Cts   { get; } = new();

        Task _fakeRunTask = Task.CompletedTask;

        public Harness() {
            Fake    = new FakeAcpAgent();
            Conn    = new AcpConnection(Fake.ClientWriteStream, Fake.ClientReadStream, NullLogger.Instance);
            Process = new FakeAcpProcess();
            Runtime = new AcpHostedAgentRuntime(Conn, Process, NullLogger.Instance) {
                // Liveness-supervision spec §1: production wires this from AgentOrchestrator right
                // after the runtime is obtained; a bare test construction (like this one) must do the
                // same assignment itself.
                ActivityClock = new AgentActivityClock(TimeProvider.System)
            };
        }

        public void StartFakeAgentLoop() => _fakeRunTask = Fake.RunAsync(Cts.Token);

        public async ValueTask DisposeAsync() {
            Cts.Cancel();
            try {
                await _fakeRunTask.WaitAsync(HangGuard);
            } catch (OperationCanceledException) {
                // expected shutdown path
            }
            await Runtime.DisposeAsync();
            await Fake.DisposeAsync();
            Cts.Dispose();
        }
    }

    /// <summary>
    /// Proves the envelope-ingest source (EmitEnvelope) in total isolation from the turn-transition
    /// source: <c>StartAsync</c> is given a NULL initial prompt, so <c>StartAsync</c> never enqueues a
    /// turn (see its own doc comment — "If initialPrompt is non-empty") and this test never calls
    /// <c>SendUserInputAsync</c> either, so NO turn is EVER admitted here — <c>TurnInFlight</c> can
    /// never have moved. The <c>session_info_update</c> kind is translated and emitted standalone,
    /// with no aggregation/turn involvement at all (see <c>AggregateUpdate</c>'s
    /// <c>SessionInfo</c>/<c>UsageUpdate</c> case) — the only source that can have advanced the clock
    /// here is the envelope-ingest call in <c>EmitEnvelope</c>.
    /// </summary>
    [Test]
    public async Task Session_update_envelope_advances_the_clock_with_no_turn_ever_admitted() {
        await using var h = new Harness();
        h.StartFakeAgentLoop();

        await h.Runtime.StartAsync("/abs/worktree", initialPrompt: null, h.Cts.Token).WaitAsync(HangGuard);

        var beforeSeq = h.Runtime.ActivityClock!.ActivitySeq;

        await h.Fake.WriteRawFrameAsync(
            FakeAcpAgent.BuildSessionUpdateNotification(FakeAcpAgent.FixedSessionId, FakeAcpAgent.BuildSessionInfoUpdate("t")),
            h.Cts.Token);

        // Wait for the envelope to actually land — the fact of reading it IS the proof EmitEnvelope ran.
        // AcpUpdateKind.SessionInfo translates to the wire kind AcpEventKind.SessionTitle
        // (AcpEventTranslator.Translate) — a plain string constant, not an enum member.
        var envelope = await h.Runtime.Envelopes.ReadAsync().AsTask().WaitAsync(HangGuard);
        await Assert.That(envelope.Kind).IsEqualTo(AcpEventKind.SessionTitle);

        await Assert.That(h.Runtime.ActivityClock!.ActivitySeq).IsEqualTo(beforeSeq + 1);
        await Assert.That(h.Runtime.ActivityClock!.TurnInFlight).IsFalse();
    }

    /// <summary>
    /// Proves the turn-transition source (<c>SetTurnInFlight</c>) specifically — not merely "the seq
    /// moved" (which admitting a turn ALSO does via its own UserMessage envelope, a DIFFERENT call
    /// site; asserting only on the seq here would be exactly the two-guards-one-input trap). The
    /// assertion is on <see cref="AgentActivityClock.TurnInFlight"/> itself, which no other call site
    /// in this codebase can flip — so this test fails if either <c>SetTurnInFlight(true)</c> (mid-turn
    /// assertion) or <c>SetTurnInFlight(false)</c> (post-settle assertion) is removed, regardless of
    /// the envelope traffic that legitimately accompanies the turn.
    /// </summary>
    [Test]
    public async Task Turn_entering_and_settling_sets_then_clears_TurnInFlight() {
        await using var h = new Harness();
        h.StartFakeAgentLoop();

        await h.Runtime.StartAsync("/abs/worktree", initialPrompt: null, h.Cts.Token).WaitAsync(HangGuard);
        await Assert.That(h.Runtime.ActivityClock!.TurnInFlight).IsFalse(); // precondition: no turn yet

        // Hold the prompt's RESPONSE (not its queued updates) so the turn stays "entered" long enough
        // to observe TurnInFlight==true before it settles.
        h.Fake.HoldPromptResponses = new TaskCompletionSource();

        await h.Runtime.SendUserInputAsync("hi");

        var deadline = DateTime.UtcNow + HangGuard;
        while (h.Fake.ReceivedCalls.All(c => c.Method != "session/prompt") && DateTime.UtcNow < deadline)
            await Task.Delay(10);

        await Assert.That(h.Runtime.ActivityClock!.TurnInFlight).IsTrue();

        h.Fake.HoldPromptResponses.SetResult();

        deadline = DateTime.UtcNow + HangGuard;
        while (h.Runtime.ActivityClock!.TurnInFlight && DateTime.UtcNow < deadline)
            await Task.Delay(10);

        await Assert.That(h.Runtime.ActivityClock!.TurnInFlight).IsFalse();
    }
}

/// <summary>
/// LocalPermissionBridge-source wiring (liveness-supervision spec §1): a reviewer tool-call POST must
/// advance the bound clock. Isolated by construction — nothing else in this test touches a PTY,
/// an ACP runtime, or a turn, so the clock can only have moved from the bridge's own handler.
/// </summary>
public class LocalPermissionBridgeActivityWiringTests {
    static HttpClient CreateClient() => new() { Timeout = TimeSpan.FromSeconds(5) };

    [Test]
    public async Task Reviewer_tool_call_advances_the_bound_activity_clock() {
        var server = new FakeServerConnection((_, _, _, _, _) => Task.FromResult(new PermissionDecision("deny", null, null)));
        var bridge = new LocalPermissionBridge(server, NullLogger<LocalPermissionBridge>.Instance);
        var clock  = new AgentActivityClock(TimeProvider.System);

        try {
            await bridge.StartAsync(CancellationToken.None);
            var reviewerUrl = bridge.RegisterReviewerToken(["kcap-review"], activityClock: clock);

            using var client  = CreateClient();
            var       payload = new { session_id = "abc", tool_name = "mcp__kcap-review__whatever" };
            using var response = await client.PostAsync($"{reviewerUrl}/codex/permission-request", JsonContent.Create(payload));

            await Assert.That((int) response.StatusCode).IsEqualTo(200);
            await Assert.That(clock.ActivitySeq).IsEqualTo(2UL);
        } finally {
            await bridge.DisposeAsync();
        }
    }
}
