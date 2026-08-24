using Capacitor.Cli.Core.LocalIpc;

namespace Capacitor.Cli.Core.Tests.Unit.LocalIpc;

public class AgentAttachClientTests {
    static readonly string AgentId = new('a', 32);

    static (ScriptedAttachServer Server, TempDir Tmp) NewServer() {
        var tmp = new TempDir("sock");
        var path = tmp.GetResolvedPath("s.sock");
        return (new ScriptedAttachServer(path), tmp);
    }

    sealed class Recorder {
        public readonly List<(byte[] Snapshot, string? Reason)> Attached = [];
        public readonly List<byte[]> Output = [];
        public Func<byte[], string?, CancellationToken, Task> OnAttached => (s, r, _) => { Attached.Add((s, r)); return Task.CompletedTask; };
        public Func<byte[], CancellationToken, Task> OnOutput => (b, _) => { Output.Add(b); return Task.CompletedTask; };
    }

    [Test]
    public async Task Read_write_attach_delivers_snapshot_then_output_then_exit_and_nudges_resize() {
        var (server, tmp) = NewServer();
        await using var _s = server; using var _t = tmp;
        var rec = new Recorder();
        await using var client = new AgentAttachClient(server.Path, AgentId, rec.OnAttached, rec.OnOutput);

        var run = client.RunAsync(120, 40, CancellationToken.None);
        await server.AcceptAndPumpInboundAsync();
        var first = await server.FirstFrame.Task;              // the opening Attach frame
        await server.SendAttachedAsync(AgentId, [1, 2, 3]);
        await server.SendStdoutAsync([4, 5]);
        await server.SendExitedAsync(7);

        var outcome = await run;

        await Assert.That(first.Type).IsEqualTo(FrameType.Attach);
        await Assert.That(first.Text).IsEqualTo(AgentId);
        await Assert.That(outcome).IsEqualTo(new AttachOutcome.Exited(7));
        await Assert.That(rec.Attached.Single().Snapshot).IsEquivalentTo(new byte[] { 1, 2, 3 });
        await Assert.That(rec.Attached.Single().Reason).IsNull();
        await Assert.That(rec.Output.Single()).IsEquivalentTo(new byte[] { 4, 5 });
        // resize nudge at the initial size, after the read-write Attached:
        var resize = server.Received.Single(f => f.Type == FrameType.Resize);
        await Assert.That(resize.Cols).IsEqualTo((ushort)120);
        await Assert.That(resize.Rows).IsEqualTo((ushort)40);
    }

    [Test]
    public async Task Read_only_attach_carries_the_reason_and_sends_no_resize_nudge() {
        var (server, tmp) = NewServer();
        await using var _s = server; using var _t = tmp;
        var rec = new Recorder();
        await using var client = new AgentAttachClient(server.Path, AgentId, rec.OnAttached, rec.OnOutput);

        var run = client.RunAsync(80, 24, CancellationToken.None);
        await server.AcceptAndPumpInboundAsync();
        await server.SendAttachedReadOnlyAsync(AgentId, "flow participant", [9]);
        await server.SendExitedAsync(0);
        await run;

        await Assert.That(rec.Attached.Single().Reason).IsEqualTo("flow participant");
        await Assert.That(server.Received.Any(f => f.Type == FrameType.Resize)).IsFalse();
    }

    [Test]
    public async Task Error_as_first_reply_settles_failed() {
        var (server, tmp) = NewServer();
        await using var _s = server; using var _t = tmp;
        var rec = new Recorder();
        await using var client = new AgentAttachClient(server.Path, AgentId, rec.OnAttached, rec.OnOutput);

        var run = client.RunAsync(80, 24, CancellationToken.None);
        await server.AcceptAndPumpInboundAsync();
        await server.SendErrorAsync("no such agent aaaa…");

        await Assert.That(await run).IsEqualTo(new AttachOutcome.Failed("no such agent aaaa…"));
        await Assert.That(rec.Attached).IsEmpty();
    }

    /// Serial awaited delivery: the pump must not read frame N+1 until the
    /// callback for frame N completed.
    [Test]
    public async Task Output_callbacks_are_awaited_serially() {
        var (server, tmp) = NewServer();
        await using var _s = server; using var _t = tmp;
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        int concurrent = 0, maxConcurrent = 0, count = 0;
        await using var client = new AgentAttachClient(server.Path, AgentId,
            (_, _, _) => Task.CompletedTask,
            async (_, _) => {
                var c = Interlocked.Increment(ref concurrent);
                maxConcurrent = Math.Max(maxConcurrent, c);
                if (Interlocked.Increment(ref count) == 1) await gate.Task;
                Interlocked.Decrement(ref concurrent);
            });

        var run = client.RunAsync(80, 24, CancellationToken.None);
        await server.AcceptAndPumpInboundAsync();
        await server.SendAttachedAsync(AgentId, []);
        await server.SendStdoutAsync([1]);
        await server.SendStdoutAsync([2]);   // must sit in the socket until the gate opens
        await Task.Delay(100);
        gate.SetResult();
        await server.SendExitedAsync(0);
        await run;

        await Assert.That(maxConcurrent).IsEqualTo(1);
        await Assert.That(count).IsEqualTo(2);
    }
}
