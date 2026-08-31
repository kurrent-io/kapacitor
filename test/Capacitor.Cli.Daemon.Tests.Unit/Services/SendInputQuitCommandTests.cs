using Capacitor.Cli.Core;
using Capacitor.Cli.Daemon.Services;
using Capacitor.Cli.Daemon.Tests.Unit.Pty;
using Microsoft.Extensions.Time.Testing;

namespace Capacitor.Cli.Daemon.Tests.Unit.Services;

/// <summary>
/// Chat input that IS a quit command must stop a runtime that has no TUI to interpret it —
/// forwarding it would hand the text to the model as an ordinary prompt, so the "command" at best
/// gets role-played ("Quitting") while the agent keeps running. PTY runtimes keep receiving the
/// text verbatim: their TUI owns the command's meaning, and intercepting there would shadow it.
/// </summary>
public class SendInputQuitCommandTests {
    /// <summary>A seeded agent has no read loop, so runtime disposal (a read-loop finalizer effect)
    /// never runs here; the stop funnel's own observable is TerminateAsync flipping HasExited.</summary>
    static async Task EventuallyTerminated(FakeAcpRuntime runtime) {
        for (var i = 0; i < 300; i++) {
            if (runtime.HasExited) return;

            await Task.Delay(10);
        }

        throw new TimeoutException("Timed out waiting for the quit command to stop the agent.");
    }

    [Test]
    [Arguments("/quit")]
    [Arguments("/exit")]
    [Arguments("  /quit  \n")]
    [Arguments("/QUIT")]
    public async Task Quit_command_to_a_non_pty_agent_stops_it_instead_of_forwarding(string text) {
        var time  = new FakeTimeProvider();
        var clock = new AgentActivityClock(time);

        await using var orch = AgentOrchestratorHarness.BuildOrchestrator(
            new CaptureServerConnection(), new SpyPtyProcessFactory(), new Dictionary<string, IHostedAgentLauncher>());
        orch.GracefulExitWait = TimeSpan.FromMilliseconds(50);
        var runtime = new FakeAcpRuntime();
        var agent   = AgentOrchestratorHarness.SeedAcpAgent(orch, $"quit-acp-{Guid.NewGuid():N}", runtime, activityClock: clock);

        await orch.HandleSendInputForTest(new SendInputCommand(agent.Id, text, null));

        await EventuallyTerminated(runtime);

        // Never delivered as input: the clock still shows only the spawn advance.
        await Assert.That(agent.ActivityClock.ActivitySeq).IsEqualTo(1UL);
    }

    [Test]
    public async Task Quit_like_text_that_is_not_exactly_the_command_is_forwarded() {
        var time  = new FakeTimeProvider();
        var clock = new AgentActivityClock(time);

        await using var orch = AgentOrchestratorHarness.BuildOrchestrator(
            new CaptureServerConnection(), new SpyPtyProcessFactory(), new Dictionary<string, IHostedAgentLauncher>());
        var runtime = new FakeAcpRuntime();
        var agent   = AgentOrchestratorHarness.SeedAcpAgent(orch, "quit-acp-prose", runtime, activityClock: clock);

        await orch.HandleSendInputForTest(new SendInputCommand(agent.Id, "/quit the loop and try plan B", null));

        // Delivered normally: spawn (1) + the delivery (2), and nothing stopped the runtime.
        await Assert.That(agent.ActivityClock.ActivitySeq).IsEqualTo(2UL);
        await Assert.That(runtime.HasExited).IsFalse();
    }

    [Test]
    public async Task Pty_agent_receives_the_quit_text_verbatim() {
        var pty = new RecordingPtyProcess();

        await using var orch = AgentOrchestratorHarness.BuildOrchestrator(
            new CaptureServerConnection(), new SpyPtyProcessFactory(), new Dictionary<string, IHostedAgentLauncher>());
        var agent = orch.SeedAgentForTest("quit-pty", pty: pty);

        await orch.HandleSendInputForTest(new SendInputCommand(agent.Id, "/quit", null));

        await Assert.That(string.Join("", pty.Writes)).Contains("/quit");
    }
}
