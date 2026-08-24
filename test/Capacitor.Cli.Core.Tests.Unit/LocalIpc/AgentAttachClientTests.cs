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

    [Test]
    public async Task Detach_intent_plus_eof_settles_detached() {
        var (server, tmp) = NewServer(); await using var _s = server; using var _t = tmp;
        var rec = new Recorder();
        await using var client = new AgentAttachClient(server.Path, AgentId, rec.OnAttached, rec.OnOutput);
        var run = client.RunAsync(80, 24, CancellationToken.None);
        await server.AcceptAndPumpInboundAsync();
        await server.SendAttachedAsync(AgentId, []);

        await client.DetachAsync();
        await server.WaitForReceivedAsync(FrameType.Detach);   // deterministic: pump has drained it
        server.CloseConnection();                       // daemon closes; no ack

        await Assert.That(await run).IsEqualTo(new AttachOutcome.Detached());
        await Assert.That(server.Received.Any(f => f.Type == FrameType.Detach)).IsTrue();
    }

    [Test]
    public async Task A_terminal_frame_read_after_detach_intent_still_wins() {
        var (server, tmp) = NewServer(); await using var _s = server; using var _t = tmp;
        var rec = new Recorder();
        await using var client = new AgentAttachClient(server.Path, AgentId, rec.OnAttached, rec.OnOutput);
        var run = client.RunAsync(80, 24, CancellationToken.None);
        await server.AcceptAndPumpInboundAsync();
        await server.SendAttachedAsync(AgentId, []);

        await client.DetachAsync();
        await server.SendExitedAsync(3);                // daemon raced: exit after Detach

        await Assert.That(await run).IsEqualTo(new AttachOutcome.Exited(3));
    }

    [Test]
    public async Task Uninitiated_eof_after_attach_is_connection_lost() {
        var (server, tmp) = NewServer(); await using var _s = server; using var _t = tmp;
        var rec = new Recorder();
        await using var client = new AgentAttachClient(server.Path, AgentId, rec.OnAttached, rec.OnOutput);
        var run = client.RunAsync(80, 24, CancellationToken.None);
        await server.AcceptAndPumpInboundAsync();
        await server.SendAttachedAsync(AgentId, []);
        server.CloseConnection();

        await Assert.That(await run).IsEqualTo(new AttachOutcome.ConnectionLost());
    }

    [Test]
    public async Task Mid_header_truncation_after_attach_is_connection_lost() {
        var (server, tmp) = NewServer(); await using var _s = server; using var _t = tmp;
        var rec = new Recorder();
        await using var client = new AgentAttachClient(server.Path, AgentId, rec.OnAttached, rec.OnOutput);
        var run = client.RunAsync(80, 24, CancellationToken.None);
        await server.AcceptAndPumpInboundAsync();
        await server.SendAttachedAsync(AgentId, []);
        await server.SendRawThenCloseAsync([0x41, 0x00]);          // stdout type byte + half a length

        await Assert.That(await run).IsEqualTo(new AttachOutcome.ConnectionLost());
    }

    [Test]
    public async Task Connect_refusal_without_intent_is_failed() {
        using var tmp = new TempDir("sock");
        var rec = new Recorder();
        await using var client = new AgentAttachClient(tmp.GetResolvedPath("nobody.sock"), AgentId, rec.OnAttached, rec.OnOutput);

        var outcome = await client.RunAsync(80, 24, CancellationToken.None);

        await Assert.That(outcome).IsAssignableTo<AttachOutcome.Failed>();
    }

    [Test]
    public async Task Dispose_during_blocked_first_reply_settles_detached_without_fault() {
        var (server, tmp) = NewServer(); await using var _s = server; using var _t = tmp;
        var rec = new Recorder();
        var client = new AgentAttachClient(server.Path, AgentId, rec.OnAttached, rec.OnOutput);
        var run = client.RunAsync(80, 24, CancellationToken.None);
        await server.AcceptAndPumpInboundAsync();
        await server.FirstFrame.Task;                   // Attach written, first reply pending

        await client.DisposeAsync();                    // must not throw

        await Assert.That(await run).IsEqualTo(new AttachOutcome.Detached());
    }

    [Test]
    public async Task Dispose_before_run_makes_a_later_run_return_detached_without_dialing() {
        using var tmp = new TempDir("sock");
        var rec = new Recorder();
        var client = new AgentAttachClient(tmp.GetResolvedPath("nobody.sock"), AgentId, rec.OnAttached, rec.OnOutput);
        await client.DisposeAsync();

        var outcome = await client.RunAsync(80, 24, CancellationToken.None);   // path does not even exist

        await Assert.That(outcome).IsEqualTo(new AttachOutcome.Detached());
    }

    [Test]
    public async Task Caller_cancellation_surfaces_as_oce_and_dispose_does_not_rethrow_it() {
        var (server, tmp) = NewServer(); await using var _s = server; using var _t = tmp;
        var rec = new Recorder();
        var client = new AgentAttachClient(server.Path, AgentId, rec.OnAttached, rec.OnOutput);
        using var cts = new CancellationTokenSource();
        var run = client.RunAsync(80, 24, cts.Token);
        await server.AcceptAndPumpInboundAsync();
        await server.SendAttachedAsync(AgentId, []);

        cts.Cancel();

        await Assert.ThrowsAsync<OperationCanceledException>(async () => await run);
        await client.DisposeAsync();                    // must complete cleanly
    }

    [Test]
    public async Task Dispose_while_caller_token_uncancelled_exits_a_stuck_callback_via_internal_token() {
        var (server, tmp) = NewServer(); await using var _s = server; using var _t = tmp;
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var client = new AgentAttachClient(server.Path, AgentId,
            (_, _, _) => Task.CompletedTask,
            async (_, ct) => { entered.SetResult(); await Task.Delay(Timeout.Infinite, ct); });
        var run = client.RunAsync(80, 24, CancellationToken.None);   // external token: none, never cancelled
        await server.AcceptAndPumpInboundAsync();
        await server.SendAttachedAsync(AgentId, []);
        await server.SendStdoutAsync([1]);
        await entered.Task;                              // callback is now stuck on the internal token

        await client.DisposeAsync();

        await Assert.That(await run).IsEqualTo(new AttachOutcome.Detached());
    }
}
