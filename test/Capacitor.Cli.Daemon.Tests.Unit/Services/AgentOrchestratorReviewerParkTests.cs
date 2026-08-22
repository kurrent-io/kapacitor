using System.Runtime.CompilerServices;
using System.Threading.Channels;
using Capacitor.Cli.Core;
using Capacitor.Cli.Core.LocalIpc;
using Capacitor.Cli.Daemon.Harness.Codex;
using Capacitor.Cli.Daemon.Services;
using Capacitor.Cli.Daemon.Tests.Unit.Pty;
using Microsoft.Extensions.Time.Testing;

namespace Capacitor.Cli.Daemon.Tests.Unit.Services;

/// <summary>
/// §2.7 B6 arm-A: the RESUMABLE-PARK path in <see cref="AgentOrchestrator"/>. A settled,
/// app-server hosted Codex reviewer idle past the SHORT resumable bound
/// (<see cref="DaemonConfig.ReviewerResumableIdleTimeout"/>, 10m) is PARKED — its daemon slot freed
/// like a reap, but its Codex thread kept alive (the hosted session-end suppressed) for a later
/// <c>thread/resume</c> — rather than reaped at the 2h arm-B idle bound.
///
/// <para>Split, like the task brief, into (a) SELECTION — what
/// <see cref="AgentOrchestrator.FindReviewersToReap"/> flags as a <see cref="AgentOrchestrator.ReapCandidate.Park"/>
/// candidate — and (b) the PARK STATE MACHINE (<c>ParkReviewerAsync</c>) against a fake
/// <see cref="ServerConnection"/> whose <c>ReportParticipantParkedAsync</c> returns a chosen
/// <see cref="ParkAck"/>. Follows the <see cref="ReviewerReapingTests"/> harness patterns
/// (<c>BuildOrchestrator</c> / <see cref="CaptureServerConnection"/> / a controllable
/// <see cref="AgentActivityClock"/>).</para>
///
/// <para>The resume-capability signal is the runtime TRANSPORT
/// (<see cref="CodexTransportDecision.AppServer"/>) — the only value the app-server Codex runtime
/// reports; the canonical session id is that runtime's <see cref="IAcpTranscriptSource.AcpSessionId"/>
/// (its Codex thread id). <see cref="FakeAppServerRuntime"/> stands in for the real
/// <c>CodexAppServerHostedAgentRuntime</c> (internal/sealed, heavy ctor) by reporting the same
/// transport + thread id through those same two facets.</para>
/// </summary>
public class AgentOrchestratorReviewerParkTests {
    static readonly TimeSpan HangGuard = TimeSpan.FromSeconds(5);

    // ── (a) SELECTION ─────────────────────────────────────────────────────────────────────────

    /// <summary>The positive case: a resumable (app-server) reviewer that has settled its last round
    /// and gone idle past the 10m resumable bound — no turn in flight — is selected as a PARK candidate
    /// with the exact park reason and the activity fence armed (a delivery after selection must abort
    /// it), NOT as a reap.</summary>
    [Test]
    public async Task Arm_A_parks_a_resumable_idle_reviewer() {
        await using var orch = AgentOrchestratorHarness.BuildOrchestrator(
            new CaptureServerConnection(), new SpyPtyProcessFactory(), new Dictionary<string, IHostedAgentLauncher>());
        // defaults: 10m resumable / 2h idle / 6h TTL

        var time  = new FakeTimeProvider();
        var clock = new AgentActivityClock(time);
        time.Advance(TimeSpan.FromMinutes(11)); // past the 10m resumable bound, far under 2h idle / 6h TTL

        var agent = SeedResumableReviewer(orch, "park-me", clock);

        var park = orch.FindReviewersToReap().Single(c => c.Park);

        await Assert.That(park.Id).IsEqualTo("park-me");
        await Assert.That(park.Reason).IsEqualTo(AgentOrchestrator.ReviewerParkedResumableReason);
        await Assert.That(park.FencedOnActivity).IsTrue();
        await Assert.That(ReferenceEquals(park.Agent, agent)).IsTrue();
    }

    /// <summary>The transport IS the discriminator: under an IDENTICAL clock idle past the 2h arm-B
    /// bound, a resumable reviewer PARKS (arm-A pre-empts arm-B) while a non-resumable (PTY) reviewer
    /// falls through to the unchanged arm-B idle reap. Neither verdict can come from the elapsed time
    /// alone — only the runtime transport differs.</summary>
    [Test]
    public async Task Arm_A_ignores_a_non_resumable_reviewer_leaving_it_to_arm_B() {
        await using var orch = AgentOrchestratorHarness.BuildOrchestrator(
            new CaptureServerConnection(), new SpyPtyProcessFactory(), new Dictionary<string, IHostedAgentLauncher>());

        var elapsed = TimeSpan.FromHours(2) + TimeSpan.FromMinutes(1); // past BOTH the 10m and 2h bounds, under 6h TTL

        var resumableTime  = new FakeTimeProvider();
        var resumableClock = new AgentActivityClock(resumableTime);
        resumableTime.Advance(elapsed);
        SeedResumableReviewer(orch, "resumable", resumableClock);

        var ptyTime  = new FakeTimeProvider();
        var ptyClock = new AgentActivityClock(ptyTime);
        ptyTime.Advance(elapsed);
        // A PTY reviewer reports the "pty" transport — never resume-capable.
        orch.SeedAgentForTest("pty-reviewer", LaunchKind.ReviewFlow, status: "Running", activityClock: ptyClock);

        var reap = orch.FindReviewersToReap();

        // The resumable one is PARKED (arm-A fires before the 2h idle rule)...
        var park = reap.Single(c => c.Park);
        await Assert.That(park.Id).IsEqualTo("resumable");
        await Assert.That(park.Reason).IsEqualTo(AgentOrchestrator.ReviewerParkedResumableReason);

        // ...the PTY one is NOT parked and falls through to the unchanged arm-B idle reap.
        var pty = reap.Single(c => c.Id == "pty-reviewer");
        await Assert.That(pty.Park).IsFalse();
        await Assert.That(pty.Reason).IsEqualTo("reviewer_idle_expired");
    }

    /// <summary>A held turn suppresses arm-A exactly as it suppresses the plain idle rule: a resumable
    /// reviewer with <c>TurnInFlight</c> true, idle well past the resumable bound but nowhere near the
    /// wedge ceiling, is not parked mid-round.</summary>
    [Test]
    public async Task Arm_A_does_not_park_a_reviewer_with_a_turn_in_flight() {
        await using var orch = AgentOrchestratorHarness.BuildOrchestrator(
            new CaptureServerConnection(), new SpyPtyProcessFactory(), new Dictionary<string, IHostedAgentLauncher>());

        var time  = new FakeTimeProvider();
        var clock = new AgentActivityClock(time);
        clock.SetTurnInFlight(true);
        time.Advance(TimeSpan.FromMinutes(20)); // past the 10m resumable bound, under the 60m wedge ceiling

        SeedResumableReviewer(orch, "mid-turn", clock);

        await Assert.That(orch.FindReviewersToReap().Any(c => c.Park)).IsFalse();
    }

    /// <summary>A reviewer already mid-park (<see cref="AgentInstance.ParkAttemptInFlight"/> true) is
    /// skipped by the sweep — one park attempt at a time. A control twin under the identical clock
    /// WITHOUT the guard IS parked, so the skip is attributable to the guard and nothing else.</summary>
    [Test]
    public async Task Arm_A_skips_a_reviewer_already_parking() {
        await using var orch = AgentOrchestratorHarness.BuildOrchestrator(
            new CaptureServerConnection(), new SpyPtyProcessFactory(), new Dictionary<string, IHostedAgentLauncher>());

        var elapsed = TimeSpan.FromMinutes(15); // past the 10m resumable bound, under every reap bound

        var parkingTime  = new FakeTimeProvider();
        var parkingClock = new AgentActivityClock(parkingTime);
        parkingTime.Advance(elapsed);
        SeedResumableReviewer(orch, "already-parking", parkingClock, parkInFlight: true);

        var freeTime  = new FakeTimeProvider();
        var freeClock = new AgentActivityClock(freeTime);
        freeTime.Advance(elapsed);
        SeedResumableReviewer(orch, "free", freeClock);

        var reap = orch.FindReviewersToReap();

        await Assert.That(reap.Any(c => c.Id == "already-parking")).IsFalse(); // guard skipped it entirely
        await Assert.That(reap.Single(c => c.Park).Id).IsEqualTo("free");      // the control still parks
    }

    /// <summary>Below the resumable bound there is no park: a resumable reviewer idle only 5m (under the
    /// 10m bound) is not selected at all.</summary>
    [Test]
    public async Task Arm_A_does_not_park_before_the_resumable_idle_bound() {
        await using var orch = AgentOrchestratorHarness.BuildOrchestrator(
            new CaptureServerConnection(), new SpyPtyProcessFactory(), new Dictionary<string, IHostedAgentLauncher>());

        var time  = new FakeTimeProvider();
        var clock = new AgentActivityClock(time);
        time.Advance(TimeSpan.FromMinutes(5)); // under the 10m resumable bound

        SeedResumableReviewer(orch, "too-fresh", clock);

        await Assert.That(orch.FindReviewersToReap().Any(c => c.Id == "too-fresh")).IsFalse();
    }

    // ── (b) THE PARK STATE MACHINE ──────────────────────────────────────────────────────────────

    /// <summary>A durable <see cref="ParkAck.Parked"/> ack: the reviewer's canonical thread id + the
    /// park reason are reported to the server, the claim is won (the agent is on its way down), the
    /// shared stop path runs — and then the read-loop finalizer SUPPRESSES the hosted session-end while
    /// still freeing the slot (unregister + removal from the live map). The thread survives for a later
    /// resume precisely because no <c>EndAgentSession</c> was emitted.</summary>
    [Test]
    public async Task Park_on_Parked_ack_suppresses_session_end_and_frees_the_slot() {
        var server = new CaptureServerConnection { ParkOutcome = ParkAck.Parked };
        await using var orch = AgentOrchestratorHarness.BuildOrchestrator(
            server, new SpyPtyProcessFactory(), new Dictionary<string, IHostedAgentLauncher>());

        var time  = new FakeTimeProvider();
        var clock = new AgentActivityClock(time);
        time.Advance(TimeSpan.FromMinutes(11));

        var agent = SeedResumableReviewer(orch, "parked", clock, threadId: "thread-xyz");

        var candidate = orch.FindReviewersToReap().Single(c => c.Park);
        await orch.ParkReviewerForTest(candidate).WaitAsync(HangGuard);

        // Reported to the server with the canonical thread id and the exact park reason.
        await Assert.That(server.ParkReports).Contains(("parked", "thread-xyz", AgentOrchestrator.ReviewerParkedResumableReason));
        // Claim won (going down), and the park reason stamped so the finalizer suppresses session-end.
        await Assert.That(agent.IsReapClaimed).IsTrue();
        await Assert.That(agent.PendingEndReason).IsEqualTo(AgentOrchestrator.ReviewerParkedResumableReason);
        // The shared stop path ran (StopAgentCoreAsync emitted Completed); no session-end from the stop.
        await Assert.That(server.StatusChangedCalls).Contains(("parked", "Completed"));
        await Assert.That(server.EndSessionReasons).IsEmpty();

        // Now the read loop unwinds (its finally runs the finalizer). Session-end stays SUPPRESSED, but
        // the rest of the local teardown still frees the slot.
        await orch.FinalizeAgentRunForTest(agent).WaitAsync(HangGuard);

        await Assert.That(server.EndSessionReasons).IsEmpty();          // hosted session-end suppressed
        await Assert.That(orch.GetAgentForTest("parked")).IsNull();     // slot freed / removed from the live map
        await Assert.That(server.AgentUnregisteredCalls).Contains("parked");
    }

    /// <summary>P1-b regression (codex pre-merge review): the reviewer's app-server child exits DURING
    /// the park-ack await — before any definite reply. The hosted session-end MUST fire (an unconfirmed
    /// park is not a park), NOT be suppressed. This holds only because the claim leaves a NEUTRAL end
    /// reason and <c>ParkReviewerAsync</c> stamps <see cref="AgentOrchestrator.ReviewerParkedResumableReason"/>
    /// itself, only after a definite <see cref="ParkAck.Parked"/>. Were the suppress reason pre-stamped
    /// at claim time (the bug), the finalizer running in this window would suppress the session-end for a
    /// park that never committed, orphaning the ledger row — neither durably parked nor cleanly closed.
    /// Releasing the ack afterwards must NOT resurrect or double-end the already-gone agent.</summary>
    [Test]
    public async Task Child_exit_during_the_park_ack_await_does_not_suppress_the_session_end() {
        var parkEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var parkGate    = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var server = new CaptureServerConnection {
            ParkOutcome = ParkAck.Parked, ParkEntered = parkEntered, ParkGate = parkGate
        };
        await using var orch = AgentOrchestratorHarness.BuildOrchestrator(
            server, new SpyPtyProcessFactory(), new Dictionary<string, IHostedAgentLauncher>());

        var time  = new FakeTimeProvider();
        var clock = new AgentActivityClock(time);
        time.Advance(TimeSpan.FromMinutes(11));

        var agent = SeedResumableReviewer(orch, "raced-exit", clock);

        var candidate = orch.FindReviewersToReap().Single(c => c.Park);

        // Start the park: it wins the claim, then BLOCKS awaiting the park ack (gate held open).
        var park = orch.ParkReviewerForTest(candidate);
        await parkEntered.Task.WaitAsync(HangGuard);

        // Claim won, but the suppress reason must NOT be stamped yet — a mid-await exit must end normally.
        await Assert.That(agent.IsReapClaimed).IsTrue();
        await Assert.That(agent.PendingEndReason).IsNotEqualTo(AgentOrchestrator.ReviewerParkedResumableReason);

        // The child exits on its own NOW (read loop unwinds → finalizer) while the ack is still in flight.
        await orch.FinalizeAgentRunForTest(agent).WaitAsync(HangGuard);

        // Session-end FIRED with the neutral reason (park unconfirmed → not suppressed); the slot is freed.
        await Assert.That(server.EndSessionReasons).Contains("agent_exited");
        await Assert.That(orch.GetAgentForTest("raced-exit")).IsNull();

        // Release the ack; the park state machine finishes without throwing. The agent is already gone,
        // so StopClaimedReapAsync no-ops (agent_gone) — no second session-end, no resurrection.
        parkGate.SetResult();
        await park.WaitAsync(HangGuard);

        await Assert.That(server.EndSessionReasons.Count).IsEqualTo(1);
    }

    /// <summary>An <see cref="ParkAck.Ambiguous"/> ack (no definite reply): tear down NOTHING and
    /// restore the pre-attempt state — the agent stays Running, the in-flight guard AND the reap latch
    /// are released, no session-end fires — so a later sweep re-selects it for park and can actually
    /// re-claim and complete the park.</summary>
    [Test]
    public async Task Park_on_Ambiguous_ack_tears_down_nothing_and_allows_retry() {
        var server = new CaptureServerConnection { ParkOutcome = ParkAck.Ambiguous };
        await using var orch = AgentOrchestratorHarness.BuildOrchestrator(
            server, new SpyPtyProcessFactory(), new Dictionary<string, IHostedAgentLauncher>());

        var time  = new FakeTimeProvider();
        var clock = new AgentActivityClock(time);
        time.Advance(TimeSpan.FromMinutes(11));

        var agent = SeedResumableReviewer(orch, "ambig", clock);

        var candidate = orch.FindReviewersToReap().Single(c => c.Park);
        await orch.ParkReviewerForTest(candidate).WaitAsync(HangGuard);

        // Nothing torn down; pre-attempt state restored.
        await Assert.That(orch.GetAgentForTest("ambig")).IsNotNull();
        await Assert.That(agent.Status).IsEqualTo("Running");
        await Assert.That(agent.ParkAttemptInFlight).IsFalse();        // guard released
        await Assert.That(agent.IsReapClaimed).IsFalse();             // reap latch released so a retry can re-claim
        await Assert.That(agent.PendingEndReason).IsEqualTo("agent_exited"); // no reap-reason stamp left behind
        await Assert.That(server.EndSessionReasons).IsEmpty();
        await Assert.That(server.StatusChangedCalls.Any(c => c.AgentId == "ambig")).IsFalse();

        // The next sweep re-selects it for park...
        var again = orch.FindReviewersToReap().Single(c => c.Park);
        await Assert.That(again.Id).IsEqualTo("ambig");

        // ...and with a definite reply this time, the retry actually parks (proves the latch reset was
        // load-bearing — a still-latched ReapClaimed would have made this re-claim CAS-fail).
        server.ParkOutcome = ParkAck.Parked;
        await orch.ParkReviewerForTest(again).WaitAsync(HangGuard);

        await Assert.That(agent.IsReapClaimed).IsTrue();
        await Assert.That(agent.PendingEndReason).IsEqualTo(AgentOrchestrator.ReviewerParkedResumableReason);
        await Assert.That(server.ParkReports.Count).IsEqualTo(2);
    }

    /// <summary>A definite <see cref="ParkAck.Rejected"/> ack: the daemon does NOT park — it falls back
    /// to the normal end path so the reviewer is cleaned up, not left dangling. The won claim is kept
    /// (re-claiming could abort on activity and strand it), a NORMAL end reason replaces the park reason
    /// so the finalizer's session-end FIRES, and the slot is freed.</summary>
    [Test]
    public async Task Park_on_Rejected_ack_falls_back_to_the_normal_end_path() {
        var server = new CaptureServerConnection { ParkOutcome = ParkAck.Rejected };
        await using var orch = AgentOrchestratorHarness.BuildOrchestrator(
            server, new SpyPtyProcessFactory(), new Dictionary<string, IHostedAgentLauncher>());

        var time  = new FakeTimeProvider();
        var clock = new AgentActivityClock(time);
        time.Advance(TimeSpan.FromMinutes(11));

        var agent = SeedResumableReviewer(orch, "rejected", clock);

        var candidate = orch.FindReviewersToReap().Single(c => c.Park);
        await orch.ParkReviewerForTest(candidate).WaitAsync(HangGuard);

        // Ended, not parked: claim kept, a NORMAL end reason (not the park reason), the stop path ran.
        await Assert.That(agent.IsReapClaimed).IsTrue();
        await Assert.That(agent.PendingEndReason).IsNotEqualTo(AgentOrchestrator.ReviewerParkedResumableReason);
        await Assert.That(server.StatusChangedCalls).Contains(("rejected", "Completed"));

        // The read loop unwinds: the session-end FIRES (a rejected park must not be suppressed) and the
        // slot is freed.
        await orch.FinalizeAgentRunForTest(agent).WaitAsync(HangGuard);

        await Assert.That(server.EndSessionReasons).Contains(agent.PendingEndReason); // session-end fired
        await Assert.That(orch.GetAgentForTest("rejected")).IsNull();                 // slot freed
    }

    /// <summary>The activity fence: a delivery that lands between selection and the claim advances the
    /// activity generation the candidate was selected against, so the park ABORTS at the claim — before
    /// the server is ever told. Nothing is reported, nothing is torn down, the guard is released.
    /// (The park twin of <see cref="ReviewerReapingTests.Stale_reap_selection_aborts_after_delivery"/>.)</summary>
    [Test]
    public async Task Park_aborts_if_activity_advances_after_selection() {
        var server = new CaptureServerConnection { ParkOutcome = ParkAck.Parked }; // would park if the fence let it
        await using var orch = AgentOrchestratorHarness.BuildOrchestrator(
            server, new SpyPtyProcessFactory(), new Dictionary<string, IHostedAgentLauncher>());

        var time  = new FakeTimeProvider();
        var clock = new AgentActivityClock(time);
        time.Advance(TimeSpan.FromMinutes(11));

        var agent = SeedResumableReviewer(orch, "raced", clock);

        var candidate = orch.FindReviewersToReap().Single(c => c.Park);

        // A round lands (or a transcript envelope arrives) between selection and the claim.
        clock.Advance();
        await Assert.That(agent.ActivityClock.ActivitySeq).IsEqualTo(candidate.ActivityGeneration + 1);

        await orch.ParkReviewerForTest(candidate).WaitAsync(HangGuard);

        await Assert.That(agent.IsReapClaimed).IsFalse();      // claim aborted on the activity advance
        await Assert.That(agent.Status).IsEqualTo("Running");
        await Assert.That(agent.ParkAttemptInFlight).IsFalse(); // guard released after the lost claim
        await Assert.That(agent.PendingEndReason).IsEqualTo("agent_exited");
        await Assert.That(server.ParkReports).IsEmpty();        // the server was NEVER told — the fence ran first
    }

    // ── helpers ─────────────────────────────────────────────────────────────────────────────────

    /// <summary>Seeds a review-flow reviewer whose runtime reports the app-server transport (so it is
    /// resume-capable) and exposes a Codex thread id as its canonical session id — the shape arm-A
    /// keys on. <c>SeedAgentForTest</c> only builds PTY runtimes, so this constructs the
    /// <see cref="AgentInstance"/> directly (borrowed cwd, so teardown touches no filesystem) and
    /// registers it exactly as a real launch would.</summary>
    static AgentInstance SeedResumableReviewer(
            AgentOrchestrator orch, string id, AgentActivityClock clock,
            string threadId = "thread-abc", bool parkInFlight = false) {
        var agent = new AgentInstance(
            id, "review this", "default", null, "/repo", "codex",
            new FakeAppServerRuntime(threadId),
            new WorktreeInfo("/repo", "b", "/repo"),
            new CancellationTokenSource()) {
            Kind                = LaunchKind.ReviewFlow,
            Status              = "Running",
            ActivityClock       = clock,
            Work                = WorkLocation.BorrowedCwd, // never remove a checkout in the park-teardown tests
            ParkAttemptInFlight = parkInFlight
        };

        orch.RegisterAgentForTest(agent);

        return agent;
    }

    /// <summary>Stand-in for <c>CodexAppServerHostedAgentRuntime</c> (internal/sealed, heavy ctor): an
    /// <see cref="IHostedAgentRuntime"/> that reports the app-server transport and, via
    /// <see cref="IAcpTranscriptSource"/>, a fixed Codex thread id as its canonical session id — the two
    /// facets arm-A reads. Everything else is a harmless no-op; <see cref="TerminateAsync"/> flips
    /// <see cref="HasExited"/> so the shared stop path completes.</summary>
    sealed class FakeAppServerRuntime(string threadId) : IHostedAgentRuntime, IAcpTranscriptSource {
        readonly TaskCompletionSource _exit = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public string Vendor              => "codex";
        public string RuntimeTransport    => CodexTransportDecision.AppServer;
        public int    Pid                 => 4242;
        public bool   HasExited           => _exit.Task.IsCompleted;
        public int?   ExitCode            => 0;
        public bool   EmitsTerminalOutput => false;

        public async IAsyncEnumerable<byte[]> ReadOutputAsync([EnumeratorCancellation] CancellationToken ct = default) {
            var             ctTcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            await using var reg   = ct.Register(() => ctTcs.TrySetResult());
            await Task.WhenAny(_exit.Task, ctTcs.Task).ConfigureAwait(false);

            yield break;
        }

        public Task SendUserInputAsync(string  text) => Task.CompletedTask;
        public Task SendSpecialKeyAsync(string key)  => Task.CompletedTask;
        public Task SendRawInputAsync(byte[]   data) => Task.CompletedTask;
        public void Resize(ushort              cols, ushort rows) { }
        public Task RequestGracefulStopAsync()       => Task.CompletedTask;
        public Task WaitForExitAsync(TimeSpan? timeout = null) => Task.CompletedTask;

        public Task TerminateAsync(TimeSpan? timeout = null) {
            _exit.TrySetResult();

            return Task.CompletedTask;
        }

        public ValueTask DisposeAsync() => default;

        // IAcpTranscriptSource — the canonical session id accessor arm-A reads for the park report.
        public string                          AcpSessionId  => threadId;
        public string                          Cwd           => "/repo";
        public string?                         ResolvedModel => null;
        public ChannelReader<AcpEventEnvelope> Envelopes     { get; } = Channel.CreateUnbounded<AcpEventEnvelope>().Reader;
    }
}
