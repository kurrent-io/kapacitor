using Capacitor.Cli.Core;
using Capacitor.Cli.Daemon.Services;
using Capacitor.Cli.Daemon.Tests.Unit.Pty;
using Microsoft.Extensions.Time.Testing;

namespace Capacitor.Cli.Daemon.Tests.Unit.Services;

/// <summary>
/// Round-dispatch grace (server-side design doc
/// docs/superpowers/specs/2026-08-10-ai1842-round-dispatch-grace-design.md in kcap-server):
/// delivered <c>SendInput</c> must advance the same per-agent <see cref="AgentActivityClock"/> that
/// PTY output, ACP envelopes, and turn transitions already advance (see
/// <c>AgentActivityClockTests.PTY_output_chunk_advances_the_agents_activity_clock</c> for the
/// output-side precedent). Before this, an agent that only ever RECEIVED input — never producing
/// output before the next reap sweep — looked idle to the reaper even while a human/driver was
/// actively working with it.
///
/// <c>AgentOrchestrator.HandleSendInput</c> calls <c>Advance()</c> only once delivery is
/// KNOWN to have succeeded — the runtime write returned without throwing. A failed/cancelled
/// delivery must leave the clock untouched: a false advance would mask a genuinely wedged/dead
/// agent from the reaper, which is exactly the failure mode this whole clock exists to catch.
/// </summary>
public class SendInputActivityClockTests {
    [Test]
    public async Task Delivered_input_advances_the_activity_seq_and_resets_the_idle_clock() {
        var server = new CaptureServerConnection();
        var time   = new FakeTimeProvider();
        var clock  = new AgentActivityClock(time);

        await using var orch  = AgentOrchestratorHarness.BuildOrchestrator(server, new SpyPtyProcessFactory(), new Dictionary<string, IHostedAgentLauncher>());
        var             agent = orch.SeedAgentForTest("send-input-ok", pty: new RecordingPtyProcess(), activityClock: clock);

        time.Advance(TimeSpan.FromSeconds(10));
        await Assert.That(agent.ActivityClock.IdleForMs).IsGreaterThanOrEqualTo(9_900UL);

        await orch.HandleSendInputForTest(new SendInputCommand(agent.Id, "hello", null));

        // Spawn (1) + the delivered input (2) — nothing else in this test touches the clock.
        await Assert.That(agent.ActivityClock.ActivitySeq).IsEqualTo(2UL);
        await Assert.That(agent.ActivityClock.IdleForMs).IsEqualTo(0UL);
    }

    /// <summary>Pins the mechanism the design's residual note is about: ACP's default (non-borrowed)
    /// <c>SendUserInputAsync</c> is fire-and-forget — its non-throwing return means "enqueued", not
    /// "the agent read it" (<c>AcpHostedAgentRuntime.EnqueueTurn</c>'s full-queue branch drops
    /// silently, no throw) — yet <c>AgentOrchestrator.HandleSendInput</c> still advances the
    /// clock on it, by design (a false advance only delays a reap/silence verdict, never manufactures
    /// one). Uses <see cref="FakeAcpRuntime"/> (an <see cref="IHostedAgentRuntime"/> test double
    /// from <see cref="AgentOrchestratorHarness"/>) via <c>SeedAcpAgent</c> — its
    /// <c>SendUserInputAsync</c> is itself a non-throwing no-op, matching the enqueue-accepted shape
    /// this test pins, without needing the full duplex <c>AcpHostedAgentRuntime</c>/<c>FakeAcpAgent</c>
    /// harness the finalizer-verdict tests use.</summary>
    [Test]
    public async Task Acp_enqueue_accepted_input_advances_the_activity_seq_and_resets_the_idle_clock() {
        var time  = new FakeTimeProvider();
        var clock = new AgentActivityClock(time);

        await using var orch  = AgentOrchestratorHarness.BuildOrchestrator(new CaptureServerConnection(), new SpyPtyProcessFactory(), new Dictionary<string, IHostedAgentLauncher>());
        var             agent = AgentOrchestratorHarness.SeedAcpAgent(orch, "send-input-acp-ok", new FakeAcpRuntime(), activityClock: clock);

        time.Advance(TimeSpan.FromSeconds(10));
        await Assert.That(agent.ActivityClock.IdleForMs).IsGreaterThanOrEqualTo(9_900UL);

        await orch.HandleSendInputForTest(new SendInputCommand(agent.Id, "hello", null));

        // Spawn (1) + the enqueue-accepted delivery (2).
        await Assert.That(agent.ActivityClock.ActivitySeq).IsEqualTo(2UL);
        await Assert.That(agent.ActivityClock.IdleForMs).IsEqualTo(0UL);
    }

    /// Handing the agent a message ends its wait, whichever source raised the flag.
    [Test]
    public async Task Delivered_input_clears_awaiting_input() {
        var clock = new AgentActivityClock(new FakeTimeProvider());
        clock.SetAwaitingInput(true);

        await using var orch  = AgentOrchestratorHarness.BuildOrchestrator(new CaptureServerConnection(), new SpyPtyProcessFactory(), new Dictionary<string, IHostedAgentLauncher>());
        var             agent = orch.SeedAgentForTest("send-input-wait", pty: new RecordingPtyProcess(), activityClock: clock);

        await orch.HandleSendInputForTest(new SendInputCommand(agent.Id, "hello", null));

        await Assert.That(agent.ActivityClock.AwaitingInput).IsFalse();
    }

    /// Codex can report a turn complete before the send that started it returns, and a PTY submit
    /// with approvals disabled sprays carriage returns long enough for a short turn's Stop to land:
    /// the wait that began during the delivery is the newer verdict and must survive the clear.
    [Test]
    public async Task A_turn_that_ends_during_delivery_keeps_its_wait() {
        var clock = new AgentActivityClock(new FakeTimeProvider());
        var pty   = new MidWritePtyProcess(() => { clock.SetTurnInFlight(true); clock.SetTurnInFlight(false); });

        await using var orch  = AgentOrchestratorHarness.BuildOrchestrator(new CaptureServerConnection(), new SpyPtyProcessFactory(), new Dictionary<string, IHostedAgentLauncher>());
        var             agent = orch.SeedAgentForTest("send-input-race", pty: pty, activityClock: clock);

        await orch.HandleSendInputForTest(new SendInputCommand(agent.Id, "hello", null));

        await Assert.That(agent.ActivityClock.AwaitingInput).IsTrue();
    }

    [Test]
    public async Task Failed_delivery_advances_nothing() {
        var server = new CaptureServerConnection();
        var time   = new FakeTimeProvider();
        var clock  = new AgentActivityClock(time);

        await using var orch  = AgentOrchestratorHarness.BuildOrchestrator(server, new SpyPtyProcessFactory(), new Dictionary<string, IHostedAgentLauncher>());
        var             agent = orch.SeedAgentForTest("send-input-fail", pty: new AlwaysThrowsPtyProcess(), activityClock: clock);

        time.Advance(TimeSpan.FromSeconds(10));

        await Assert.ThrowsAsync<IOException>(
            async () => await orch.HandleSendInputForTest(new SendInputCommand(agent.Id, "hello", null)));

        // The write failed before "delivered" — seq and idle are exactly where spawn left them.
        await Assert.That(agent.ActivityClock.ActivitySeq).IsEqualTo(1UL);
        await Assert.That(agent.ActivityClock.IdleForMs).IsGreaterThanOrEqualTo(9_900UL);
    }

}
