using Capacitor.Cli.Core.LocalIpc;
using Capacitor.Cli.Daemon.Services;
using Capacitor.Cli.Daemon.Tests.Unit.Pty;
using Microsoft.Extensions.Time.Testing;

namespace Capacitor.Cli.Daemon.Tests.Unit.Services;

/// The orchestrator's half of the input-wait relay: the bridge's attributed verdict lands on the
/// live agent's own clock, and one for an agent it does not hold is dropped.
public class AgentOrchestratorInputWaitTests {
    [Test]
    public async Task A_relayed_verdict_moves_the_attributed_agents_flag() {
        await using var orch  = AgentOrchestratorHarness.BuildOrchestrator(new CaptureServerConnection(), new SpyPtyProcessFactory(), new Dictionary<string, IHostedAgentLauncher>());
        var             agent = orch.SeedAgentForTest("relay-1", pty: new RecordingPtyProcess());
        var             relay = orch.PermissionBridgeForTest.InputWaitHandler!;

        relay(agent.Id, true);
        await Assert.That(agent.ActivityClock.AwaitingInput).IsTrue();

        relay(agent.Id, false);
        await Assert.That(agent.ActivityClock.AwaitingInput).IsFalse();
    }

    /// A locally attached client — the desktop composer and terminal alike — reaches the PTY as
    /// raw bytes on the attach socket, never through the server-origin send path, so the submit
    /// key is the clearing edge there.
    [Test]
    public async Task A_submit_typed_at_the_attached_terminal_clears_the_wait() {
        var clock = new AgentActivityClock(new FakeTimeProvider());
        clock.SetAwaitingInput(true);
        await using var orch  = AgentOrchestratorHarness.BuildOrchestrator(new CaptureServerConnection(), new SpyPtyProcessFactory(), new Dictionary<string, IHostedAgentLauncher>());
        var             agent = orch.SeedAgentForTest("attach-1", pty: new RecordingPtyProcess(), activityClock: clock);

        await orch.AttachClientLoopAsync(agent, await ScriptedClientAsync(LocalFrame.Stdin("hello\r"u8.ToArray())), CancellationToken.None);

        await Assert.That(agent.ActivityClock.AwaitingInput).IsFalse();
    }

    [Test]
    public async Task A_keystroke_that_submits_nothing_leaves_the_wait() {
        var clock = new AgentActivityClock(new FakeTimeProvider());
        clock.SetAwaitingInput(true);
        await using var orch  = AgentOrchestratorHarness.BuildOrchestrator(new CaptureServerConnection(), new SpyPtyProcessFactory(), new Dictionary<string, IHostedAgentLauncher>());
        var             agent = orch.SeedAgentForTest("attach-2", pty: new RecordingPtyProcess(), activityClock: clock);

        await orch.AttachClientLoopAsync(agent, await ScriptedClientAsync(LocalFrame.Stdin("\x1b[A"u8.ToArray())), CancellationToken.None);

        await Assert.That(agent.ActivityClock.AwaitingInput).IsTrue();
    }

    /// The client's side of an attach: the given frames, then a detach, with every daemon write
    /// discarded.
    static async Task<Stream> ScriptedClientAsync(params LocalFrame[] frames) {
        var script = new MemoryStream();
        foreach (var frame in frames) await FrameCodec.WriteAsync(script, frame, CancellationToken.None);
        await FrameCodec.WriteAsync(script, LocalFrame.Detach(), CancellationToken.None);
        script.Position = 0;
        return new ScriptedClientStream(script);
    }

    sealed class ScriptedClientStream(MemoryStream script) : Stream {
        public override bool CanRead  => true;
        public override bool CanSeek  => false;
        public override bool CanWrite => true;
        public override long Length   => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

        public override int  Read(byte[] buffer, int offset, int count) => script.Read(buffer, offset, count);
        public override void Write(byte[] buffer, int offset, int count) { }
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value)                => throw new NotSupportedException();
    }

    [Test]
    public async Task A_verdict_for_an_agent_the_daemon_does_not_hold_is_dropped() {
        await using var orch  = AgentOrchestratorHarness.BuildOrchestrator(new CaptureServerConnection(), new SpyPtyProcessFactory(), new Dictionary<string, IHostedAgentLauncher>());
        var             agent = orch.SeedAgentForTest("relay-2", pty: new RecordingPtyProcess());

        orch.PermissionBridgeForTest.InputWaitHandler!("somebody-else", true);

        await Assert.That(agent.ActivityClock.AwaitingInput).IsFalse();
    }
}
