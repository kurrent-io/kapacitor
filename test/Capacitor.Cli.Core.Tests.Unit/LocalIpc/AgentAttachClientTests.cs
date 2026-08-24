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

    sealed class RecordingSink {
        public readonly List<(string Context, Exception Ex)> Entries = [];
        readonly object _gate = new();
        public int MaxConcurrent; int _current;
        public Action<string, Exception> Callback => (c, e) => {
            var now = Interlocked.Increment(ref _current);
            MaxConcurrent = Math.Max(MaxConcurrent, now);
            lock (_gate) Entries.Add((c, e));
            Interlocked.Decrement(ref _current);
        };
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

    [Test]
    public async Task Double_dispose_and_dispose_after_a_completed_run_are_both_safe() {
        var (server, tmp) = NewServer(); await using var _s = server; using var _t = tmp;
        var rec = new Recorder();
        var client = new AgentAttachClient(server.Path, AgentId, rec.OnAttached, rec.OnOutput);
        var run = client.RunAsync(80, 24, CancellationToken.None);
        await server.AcceptAndPumpInboundAsync();
        await server.SendAttachedAsync(AgentId, []);
        await server.SendExitedAsync(0);
        await Assert.That(await run).IsEqualTo(new AttachOutcome.Exited(0));   // run already completed, unforced

        await client.DisposeAsync();                    // dispose after a completed run: must not throw
        await client.DisposeAsync();                    // second dispose: must not throw
    }

    [Test]
    public async Task Input_and_resize_before_attached_are_dropped() {
        var (server, tmp) = NewServer(); await using var _s = server; using var _t = tmp;
        var rec = new Recorder();
        await using var client = new AgentAttachClient(server.Path, AgentId, rec.OnAttached, rec.OnOutput);
        var run = client.RunAsync(80, 24, CancellationToken.None);
        await server.AcceptAndPumpInboundAsync();
        await server.FirstFrame.Task;

        await client.SendInputAsync([1]);
        await client.ResizeAsync(100, 30);
        await server.SendAttachedAsync(AgentId, []);
        await server.SendExitedAsync(0);
        await run;

        await Assert.That(server.Received.Count(f => f.Type == FrameType.Stdin)).IsEqualTo(0);
        // the only Resize is the post-attach nudge at the run's initial size:
        await Assert.That(server.Received.Count(f => f.Type == FrameType.Resize)).IsEqualTo(1);
        await Assert.That(server.Received.Single(f => f.Type == FrameType.Resize).Cols).IsEqualTo((ushort)80);
    }

    [Test]
    public async Task Explicit_input_and_resize_after_read_only_attach_are_dropped() {
        var (server, tmp) = NewServer(); await using var _s = server; using var _t = tmp;
        var rec = new Recorder();
        await using var client = new AgentAttachClient(server.Path, AgentId, rec.OnAttached, rec.OnOutput);
        var run = client.RunAsync(80, 24, CancellationToken.None);
        await server.AcceptAndPumpInboundAsync();
        await server.SendAttachedReadOnlyAsync(AgentId, "review", []);
        await Task.Delay(50);

        await client.SendInputAsync([1]);
        await client.ResizeAsync(100, 30);
        await server.SendExitedAsync(0);
        await run;

        await Assert.That(server.Received.Any(f => f.Type is FrameType.Stdin or FrameType.Resize)).IsFalse();
    }

    [Test]
    public async Task No_input_is_written_behind_a_queued_detach() {
        var (server, tmp) = NewServer(); await using var _s = server; using var _t = tmp;
        var rec = new Recorder();
        await using var client = new AgentAttachClient(server.Path, AgentId, rec.OnAttached, rec.OnOutput);
        var run = client.RunAsync(80, 24, CancellationToken.None);
        await server.AcceptAndPumpInboundAsync();
        await server.SendAttachedAsync(AgentId, []);
        await Task.Delay(50);

        await client.DetachAsync();
        await server.WaitForReceivedAsync(FrameType.Detach);   // deterministic: pump has drained it
        await client.SendInputAsync([9]);
        server.CloseConnection();
        await run;

        var detachIndex = server.Received.FindIndex(f => f.Type == FrameType.Detach);
        await Assert.That(detachIndex).IsGreaterThanOrEqualTo(0);
        await Assert.That(server.Received.Skip(detachIndex + 1).Any(f => f.Type == FrameType.Stdin)).IsFalse();
    }

    [Test]
    [Arguments(0, 24)]
    [Arguments(-1, 24)]
    [Arguments(80, 0)]
    [Arguments(70000, 24)]
    public async Task Invalid_dimensions_are_rejected_locally(int cols, int rows) {
        var (server, tmp) = NewServer(); await using var _s = server; using var _t = tmp;
        var rec = new Recorder();
        await using var client = new AgentAttachClient(server.Path, AgentId, rec.OnAttached, rec.OnOutput);
        var run = client.RunAsync(80, 24, CancellationToken.None);
        await server.AcceptAndPumpInboundAsync();
        await server.SendAttachedAsync(AgentId, []);
        await Task.Delay(50);
        var before = server.Received.Count(f => f.Type == FrameType.Resize);

        await client.ResizeAsync(cols, rows);
        await server.SendExitedAsync(0);
        await run;

        await Assert.That(server.Received.Count(f => f.Type == FrameType.Resize)).IsEqualTo(before);
    }

    /// Read side held open: the write failure alone must settle the run.
    [Test]
    public async Task Input_write_failure_settles_connection_lost_without_rethrow_or_hung_pump() {
        var (server, tmp) = NewServer(); await using var _s = server; using var _t = tmp;
        var rec = new Recorder();
        await using var client = new AgentAttachClient(server.Path, AgentId, rec.OnAttached, rec.OnOutput);
        var run = client.RunAsync(80, 24, CancellationToken.None);
        await server.AcceptAndPumpInboundAsync();
        await server.SendAttachedAsync(AgentId, []);
        await Task.Delay(50);

        server.CloseConnection();                                  // break the transport under the writer
        await client.SendInputAsync([1, 2, 3]);                    // must not throw

        await Assert.That(await run).IsEqualTo(new AttachOutcome.ConnectionLost());
    }

    [Test]
    public async Task Routine_dispose_produces_zero_diagnostics() {
        var (server, tmp) = NewServer(); await using var _s = server; using var _t = tmp;
        var sink = new RecordingSink();
        var rec = new Recorder();
        var client = new AgentAttachClient(server.Path, AgentId, rec.OnAttached, rec.OnOutput, sink.Callback);
        var run = client.RunAsync(80, 24, CancellationToken.None);
        await server.AcceptAndPumpInboundAsync();
        await server.SendAttachedAsync(AgentId, []);
        await Task.Delay(50);

        await client.DisposeAsync();
        await run;

        await Assert.That(sink.Entries).IsEmpty();
    }

    [Test]
    public async Task Callback_fault_losing_to_dispose_is_logged_once_and_run_settles_detached() {
        var (server, tmp) = NewServer(); await using var _s = server; using var _t = tmp;
        var sink = new RecordingSink();
        var boom = new InvalidOperationException("render exploded");
        var disposeStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        AgentAttachClient client = null!;
        client = new AgentAttachClient(server.Path, AgentId,
            (_, _, _) => Task.CompletedTask,
            async (_, _) => { await disposeStarted.Task; throw boom; },   // fault AFTER dispose claimed
            sink.Callback);
        var run = client.RunAsync(80, 24, CancellationToken.None);
        await server.AcceptAndPumpInboundAsync();
        await server.SendAttachedAsync(AgentId, []);
        await server.SendStdoutAsync([1]);
        await Task.Delay(50);

        var dispose = client.DisposeAsync();
        disposeStarted.SetResult();
        await dispose;                                             // completes normally, no rethrow

        await Assert.That(await run).IsEqualTo(new AttachOutcome.Detached());
        await Assert.That(sink.Entries.Count(e => ReferenceEquals(e.Ex, boom))).IsEqualTo(1);
    }

    [Test]
    public async Task Callback_fault_claiming_first_faults_run_and_dispose_does_not_rethrow() {
        var (server, tmp) = NewServer(); await using var _s = server; using var _t = tmp;
        var sink = new RecordingSink();
        var boom = new InvalidOperationException("render exploded");
        var client = new AgentAttachClient(server.Path, AgentId,
            (_, _, _) => Task.CompletedTask,
            (_, _) => throw boom,
            sink.Callback);
        var run = client.RunAsync(80, 24, CancellationToken.None);
        await server.AcceptAndPumpInboundAsync();
        await server.SendAttachedAsync(AgentId, []);
        await server.SendStdoutAsync([1]);

        var thrown = await Assert.ThrowsAsync<AttachCallbackException>(async () => await run);
        await client.DisposeAsync();                               // completes normally

        await Assert.That(thrown!.InnerException).IsSameReferenceAs(boom);
        // the fault WON — it is the result, not a losing diagnostic:
        await Assert.That(sink.Entries.Any(e => ReferenceEquals(e.Ex, boom))).IsFalse();
    }

    [Test]
    public async Task A_throwing_sink_is_swallowed_and_alters_nothing() {
        var (server, tmp) = NewServer(); await using var _s = server; using var _t = tmp;
        var rec = new Recorder();
        var client = new AgentAttachClient(server.Path, AgentId, rec.OnAttached, rec.OnOutput,
            (_, _) => throw new InvalidOperationException("sink bug"));
        var run = client.RunAsync(80, 24, CancellationToken.None);
        await server.AcceptAndPumpInboundAsync();
        await server.SendAttachedAsync(AgentId, []);
        await Task.Delay(50);

        server.CloseConnection();
        await client.SendInputAsync([1]);                          // write failure → loser or winner, sink throws either way

        await Assert.That(await run).IsEqualTo(new AttachOutcome.ConnectionLost());
        await client.DisposeAsync();                               // still clean
    }

    /// A second, independent throwing-sink drive: unlike the sink above, this one
    /// counts its own invocations, so the test can assert it was actually called
    /// (not merely that nothing escaped) while pinning the same "no rethrow, no
    /// altered outcome" contract on a fresh write-failure path.
    [Test]
    public async Task Throwing_sink_is_invoked_and_swallowed_without_altering_the_outcome() {
        var (server, tmp) = NewServer(); await using var _s = server; using var _t = tmp;
        var rec = new Recorder();
        var sinkCalls = 0;
        var client = new AgentAttachClient(server.Path, AgentId, rec.OnAttached, rec.OnOutput,
            (_, _) => { Interlocked.Increment(ref sinkCalls); throw new InvalidOperationException("sink bug"); });
        var run = client.RunAsync(80, 24, CancellationToken.None);
        await server.AcceptAndPumpInboundAsync();
        await server.SendAttachedAsync(AgentId, []);
        await Task.Delay(50);

        server.CloseConnection();
        await client.SendInputAsync([1, 2, 3]);                    // must not throw despite the sink throwing

        await Assert.That(await run).IsEqualTo(new AttachOutcome.ConnectionLost());
        await Assert.That(sinkCalls).IsGreaterThanOrEqualTo(1);    // the throwing sink was actually invoked
        await client.DisposeAsync();                               // still clean
        await client.DisposeAsync();                               // idempotent even after a throwing sink
    }

    /// Two real producers fail around the same moment — an input-write failure and
    /// an output-callback fault — and race for the one cause slot. Whichever wins
    /// decides `RunAsync`'s result; the loser rule then pins exactly which
    /// exception(s) reach the sink, and `MaxConcurrent == 1` proves `_sinkLock`
    /// actually serializes the two producers rather than merely happening not to
    /// overlap.
    [Test]
    public async Task Concurrent_losers_are_serialized_into_the_sink() {
        var (server, tmp) = NewServer(); await using var _s = server; using var _t = tmp;
        var sink = new RecordingSink();
        var boom = new InvalidOperationException("callback exploded");
        var proceed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var client = new AgentAttachClient(server.Path, AgentId,
            (_, _, _) => Task.CompletedTask,
            async (_, _) => { await proceed.Task; throw boom; },    // stdout callback: armed, not yet fired
            sink.Callback);
        var run = client.RunAsync(80, 24, CancellationToken.None);
        await server.AcceptAndPumpInboundAsync();
        await server.SendAttachedAsync(AgentId, []);
        await server.SendStdoutAsync([1]);
        await Task.Delay(50);                                      // pump is now blocked inside the callback

        server.CloseConnection();                                  // break the transport under the writer
        var write = client.SendInputAsync([9]);                    // races the callback fault for the cause slot
        proceed.SetResult();                                       // let the callback fault fire concurrently

        await write;                                               // must not throw either way

        AttachOutcome? outcome = null;
        AttachCallbackException? thrown = null;
        try { outcome = await run; }
        catch (AttachCallbackException ex) { thrown = ex; }
        await client.DisposeAsync();

        await Assert.That(sink.MaxConcurrent).IsEqualTo(1);        // serialized despite the race

        if (thrown is not null) {
            // the callback fault won the slot: it IS the run's result, not a diagnostic;
            // the write failure lost and is observed exactly once.
            await Assert.That(thrown.InnerException).IsSameReferenceAs(boom);
            await Assert.That(sink.Entries.Any(e => ReferenceEquals(e.Ex, boom))).IsFalse();
            await Assert.That(sink.Entries.Count(e => e.Context == "outbound write")).IsEqualTo(1);
        } else {
            // the write failure won the slot: ConnectionLost carries no detail, so it is
            // reported even as the winner; the callback fault lost and is observed once.
            await Assert.That(outcome).IsEqualTo(new AttachOutcome.ConnectionLost());
            await Assert.That(sink.Entries.Count(e => e.Context == "outbound write")).IsEqualTo(1);
            await Assert.That(sink.Entries.Count(e => ReferenceEquals(e.Ex, boom))).IsEqualTo(1);
        }
    }
}
