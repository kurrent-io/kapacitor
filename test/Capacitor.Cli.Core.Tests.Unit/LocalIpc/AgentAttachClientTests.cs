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
        // Completed inside OnAttached so a test can await having actually observed the
        // Attached frame before severing the connection — see the barrier comment at its
        // first use below.
        public readonly TaskCompletionSource AttachedObserved = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Func<byte[], string?, CancellationToken, Task> OnAttached => (s, r, _) => { Attached.Add((s, r)); AttachedObserved.TrySetResult(); return Task.CompletedTask; };
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

        // The Received pump is a background drain (ScriptedAttachServer's own doc comment);
        // waiting for the frame we actually assert on makes the read deterministic instead of
        // racing that pump.
        await server.WaitForReceivedAsync(FrameType.Resize);

        await Assert.That(first.Type).IsEqualTo(FrameType.Attach);
        await Assert.That(first.Text).IsEqualTo(AgentId);
        await Assert.That(outcome).IsEqualTo(new AttachOutcome.Exited(7));
        await Assert.That(rec.Attached.Single().Snapshot).IsEquivalentTo(new byte[] { 1, 2, 3 });
        await Assert.That(rec.Attached.Single().Reason).IsNull();
        await Assert.That(rec.Output.Single()).IsEquivalentTo(new byte[] { 4, 5 });
        // resize nudge at the initial size, after the read-write Attached:
        var resize = server.SnapshotReceived().Single(f => f.Type == FrameType.Resize);
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
        // Windows: an abortive close (socket dispose without a graceful shutdown) sends RST,
        // and RST discards whatever the peer hasn't read yet — unlike Unix's FIN, which is
        // only delivered after buffered data. Without this wait, CloseConnection() can beat
        // the client's read of Attached, so it never observes attach and the run settles
        // Failed instead of ConnectionLost.
        await rec.AttachedObserved.Task;
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
        await rec.AttachedObserved.Task;              // Windows RST guard — see comment above
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

        // Deterministic: the stream is ordered, so observing the post-attach Resize nudge proves
        // any erroneous pre-attach Stdin (which would have arrived first) is already recorded too.
        await server.WaitForReceivedAsync(FrameType.Resize);

        var received = server.SnapshotReceived();
        await Assert.That(received.Count(f => f.Type == FrameType.Stdin)).IsEqualTo(0);
        // the only Resize is the post-attach nudge at the run's initial size:
        await Assert.That(received.Count(f => f.Type == FrameType.Resize)).IsEqualTo(1);
        await Assert.That(received.Single(f => f.Type == FrameType.Resize).Cols).IsEqualTo((ushort)80);
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
    public async Task Out_of_range_initial_size_is_clamped_not_dropped_from_the_opening_nudge() {
        var (server, tmp) = NewServer(); await using var _s = server; using var _t = tmp;
        var rec = new Recorder();
        await using var client = new AgentAttachClient(server.Path, AgentId, rec.OnAttached, rec.OnOutput);

        var run = client.RunAsync(70000, -5, CancellationToken.None);
        await server.AcceptAndPumpInboundAsync();
        await server.SendAttachedAsync(AgentId, []);
        await server.SendExitedAsync(0);
        await run;

        await server.WaitForReceivedAsync(FrameType.Resize);
        var resize = server.SnapshotReceived().Single(f => f.Type == FrameType.Resize);
        await Assert.That(resize.Cols).IsEqualTo((ushort)65535);
        await Assert.That(resize.Rows).IsEqualTo((ushort)1);
    }

    /// The dial itself never blocks a local Unix socket connect on every platform this suite
    /// runs on -- it resolves (success or refusal) within microseconds. So "detach before the
    /// server accepts" is forced by scheduling asymmetry, not by an OS-level pending connect:
    /// RunAsync is queued onto the pool (a real dial + _writeLock acquisition, real work), while
    /// DetachAsync fires immediately after on the calling thread (a flag write, no I/O) -- it
    /// reliably records intent before TryWriteAttachAsync's under-lock re-check runs, whether or
    /// not the dial itself ever gets cancelled. Repeated because it is still a genuine race, not
    /// a guaranteed ordering; the vacuous-test guard below proves it actually lands.
    [Test]
    public async Task Detach_racing_the_dial_prevents_the_daemon_from_ever_seeing_an_attach() {
        const int attempts = 20;
        var sawNoAttach = false;
        for (var attempt = 0; attempt < attempts; attempt++) {
            var (server, tmp) = NewServer(); await using var _s = server; using var _t = tmp;
            var rec = new Recorder();
            await using var client = new AgentAttachClient(server.Path, AgentId, rec.OnAttached, rec.OnOutput);

            var runTask = Task.Run(() => client.RunAsync(80, 24, CancellationToken.None));
            await client.DetachAsync();

            // Accept CONCURRENTLY with the run, never after it: when the dial wins this race the
            // client has written a graceful Detach and is parked in its read loop awaiting the peer,
            // because DetachAsync leaves the transport open so a terminal frame can still beat
            // Detached. An accept that waits for the run waits for a client that is waiting for it.
            //
            // Bounded, because an aborted dial reaches no listener backlog: a timeout here means the
            // dial never landed, which is the outcome under test.
            using var acceptCts = new CancellationTokenSource(TimeSpan.FromMilliseconds(300));
            try { await server.AcceptAndPumpInboundAsync(acceptCts.Token); } catch (OperationCanceledException) { }

            // Half-close, so the client sees EOF and settles on the outcome it chose. Closing outright
            // would settle it too, but would also discard anything not yet drained — and what was
            // drained is the whole question below.
            server.ShutdownSend();

            var outcome = await runTask.WaitAsync(TimeSpan.FromSeconds(5));

            // The client writes nothing further once its run has settled, so closing it here ends the
            // pump at EOF rather than at a deadline. Joining the pump is what makes "no Attach
            // arrived" a fact about the wire instead of a snapshot taken before it caught up.
            await client.DisposeAsync();
            await server.InboundDrained;

            var receivedAttach = server.SnapshotReceived().Any(f => f.Type == FrameType.Attach);

            if (!receivedAttach) {
                sawNoAttach = true;
                await Assert.That(outcome).IsEqualTo(new AttachOutcome.Detached());
            }
        }

        await Assert.That(sawNoAttach).IsTrue();   // otherwise this race never actually landed -- test would be vacuous
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
        await rec.AttachedObserved.Task;              // Windows RST guard — see comment above

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
        await rec.AttachedObserved.Task;              // Windows RST guard — see comment above

        server.CloseConnection();
        await client.SendInputAsync([1]);                          // write failure → loser or winner, sink throws either way

        await Assert.That(await run).IsEqualTo(new AttachOutcome.ConnectionLost());
        await client.DisposeAsync();                               // still clean
    }

    /// A second, independent throwing-sink drive. The output callback parks on the
    /// internal token (`Task.Delay(Timeout.Infinite, ct)`), so `DisposeAsync`
    /// unblocks it cleanly via cooperative cancellation and, crucially, the pump
    /// can never independently race the write for the cause slot — the write is
    /// the ONLY producer that can possibly reach the sink here, so the call count
    /// and context are asserted exactly, not just "at least one call".
    [Test]
    public async Task Throwing_sink_is_invoked_and_swallowed_without_altering_the_outcome() {
        var (server, tmp) = NewServer(); await using var _s = server; using var _t = tmp;
        var sinkCalls = 0;
        var contexts = new List<string>();
        var attachedObserved = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var client = new AgentAttachClient(server.Path, AgentId,
            (_, _, _) => { attachedObserved.TrySetResult(); return Task.CompletedTask; },
            async (_, ct) => await Task.Delay(Timeout.Infinite, ct),   // parks the pump; only Dispose unblocks it
            (c, _) => { Interlocked.Increment(ref sinkCalls); lock (contexts) contexts.Add(c); throw new InvalidOperationException("sink bug"); });
        var run = client.RunAsync(80, 24, CancellationToken.None);
        await server.AcceptAndPumpInboundAsync();
        await server.SendAttachedAsync(AgentId, []);
        await attachedObserved.Task;                  // Windows RST guard — see comment above
        await server.SendStdoutAsync([1]);
        await Task.Delay(50);                                       // pump is now parked inside the callback

        server.CloseConnection();
        await client.SendInputAsync([1, 2, 3]);                     // the only producer that can possibly fail right now

        await client.DisposeAsync();                                 // unblocks the parked callback via the internal token
        await Assert.That(await run).IsEqualTo(new AttachOutcome.ConnectionLost());  // the already-claimed cause wins

        await Assert.That(sinkCalls).IsEqualTo(1);
        await Assert.That(contexts).IsEquivalentTo(["outbound write"]);
        await client.DisposeAsync();                                 // idempotent even after a throwing sink
    }

    /// A deterministic pump-side LOSS (review finding C1): the pump is parked
    /// inside `onOutput` (armed, not yet released), so nothing on the read side
    /// can claim anything while an input-write failure independently claims
    /// `ConnectionLost` and tears down the local transport. Releasing the
    /// callback then lets the pump's own next read hit the now-disposed stream —
    /// its exception LOSES the already-claimed slot, and since it lost to
    /// `ConnectionLost` (not `Detached`), the socket-close exclusion does not
    /// apply: it must be observed through the sink exactly once, alongside the
    /// writer's own report.
    [Test]
    public async Task Pump_exception_losing_to_writer_connection_lost_is_reported_once() {
        var (server, tmp) = NewServer(); await using var _s = server; using var _t = tmp;
        var sink = new RecordingSink();
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var attachedObserved = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var client = new AgentAttachClient(server.Path, AgentId,
            (_, _, _) => { attachedObserved.TrySetResult(); return Task.CompletedTask; },
            async (_, _) => { await gate.Task; },        // blocks the pump, then returns normally (no fault)
            sink.Callback);
        var run = client.RunAsync(80, 24, CancellationToken.None);
        await server.AcceptAndPumpInboundAsync();
        await server.SendAttachedAsync(AgentId, []);
        await attachedObserved.Task;                      // Windows RST guard — see comment above
        await server.SendStdoutAsync([1]);
        await Task.Delay(50);                             // pump is now blocked inside the callback, gate not yet open

        server.CloseConnection();
        await client.SendInputAsync([9]);                 // the writer claims ConnectionLost, closes the local transport

        gate.SetResult();                                  // release the pump: its next read hits the disposed stream

        await Assert.That(await run).IsEqualTo(new AttachOutcome.ConnectionLost());
        await client.DisposeAsync();

        await Assert.That(sink.Entries.Count(e => e.Context == "transport")).IsEqualTo(1);
        await Assert.That(sink.Entries.Count(e => e.Context == "outbound write")).IsEqualTo(1);
        await Assert.That(sink.Entries.Count).IsEqualTo(2);
    }

    /// A deterministic pump-side WIN (review finding C4): nothing else races this
    /// exception — the pump's own truncated-frame read is the first and only
    /// cause-slot claim. Per the ruling, a winning transport exception is now
    /// ALSO reported: `ConnectionLost` carries no detail, so the sink is the only
    /// place it ever surfaces — the most common real failure (the daemon dying,
    /// detected by the reader) must yield an errno somewhere, not silence.
    [Test]
    public async Task Pump_exception_winning_the_slot_is_reported_once() {
        var (server, tmp) = NewServer(); await using var _s = server; using var _t = tmp;
        var sink = new RecordingSink();
        var rec = new Recorder();
        var client = new AgentAttachClient(server.Path, AgentId, rec.OnAttached, rec.OnOutput, sink.Callback);
        var run = client.RunAsync(80, 24, CancellationToken.None);
        await server.AcceptAndPumpInboundAsync();
        await server.SendAttachedAsync(AgentId, []);
        await rec.AttachedObserved.Task;              // Windows RST guard — see comment above
        await server.SendRawThenCloseAsync([0x41, 0x00]);   // stdout type byte + half a length: mid-header truncation

        await Assert.That(await run).IsEqualTo(new AttachOutcome.ConnectionLost());
        await client.DisposeAsync();

        await Assert.That(sink.Entries.Count).IsEqualTo(1);
        await Assert.That(sink.Entries.Single().Context).IsEqualTo("transport");
    }

    /// The other half of the input-write-failure-vs-detach-intent ordering (review
    /// finding I2 — the brief's "both orderings" requirement's second half): here
    /// `DisposeAsync`'s `Detached` claim wins first, and an outbound write —
    /// already past its own guard check and genuinely in flight — faults when
    /// `DisposeAsync`'s teardown lands underneath it. `ReportIfLost`'s
    /// socket-close exclusion must swallow that fault: the sink stays empty. This
    /// is a genuine, unforced race (see the loop comment below for why and how it
    /// is repeated rather than hit in one shot) — not a claim of proven
    /// concurrency, only of repeated, honest attempts at it.
    [Test]
    public async Task Write_losing_to_an_already_claimed_detach_is_excluded_from_the_sink() {
        // No test-side hook exists into the write's actual I/O (frozen API), so the
        // desired interleaving — a write already past its guard check and genuinely
        // in flight (either still inside FrameCodec.WriteAsync, or in its own
        // finally releasing _writeLock) when DisposeAsync's Detached claim and
        // teardown land — cannot be forced deterministically in one shot. Confirmed
        // empirically: a single attempt hits it about half the time (observed both
        // an IOException from the disposed stream and an ObjectDisposedException
        // from _writeLock.Release() racing _writeLock.Dispose()). So this forces
        // the ordering across repeats instead of a single race hoped to land: every
        // attempt must stay silent regardless of whether that specific interleaving
        // landed on it, and 25 attempts make it near-certain at least one did.
        // Verified by mutation: deleting ReportIfLost's exclusion turns this red.
        for (var attempt = 0; attempt < 25; attempt++) {
            var (server, tmp) = NewServer(); await using var _s = server; using var _t = tmp;
            var sink = new RecordingSink();
            var rec = new Recorder();
            var client = new AgentAttachClient(server.Path, AgentId, rec.OnAttached, rec.OnOutput, sink.Callback);
            var run = client.RunAsync(80, 24, CancellationToken.None);
            await server.AcceptAndPumpInboundAsync();
            await server.SendAttachedAsync(AgentId, []);
            await Task.Delay(10);

            var huge = new byte[8 * 1024 * 1024 - 64];      // near FrameCodec's payload cap: real in-flight time on write
            var writeTask = Task.Run(() => client.SendInputAsync(huge));
            await Task.Yield();                              // give the write a chance past its guard, into real I/O
            await client.DisposeAsync();                     // claims Detached first, then tears down the local transport
            await writeTask;                                 // must not throw regardless of the race's outcome

            await Assert.That(await run).IsEqualTo(new AttachOutcome.Detached());
            await Assert.That(sink.Entries).IsEmpty();
        }
    }

    /// Two real producers race for the one cause slot and for `_sinkLock`: an
    /// input write failure and an output-callback fault. The write's own sink
    /// call is made to PARK — holding `_sinkLock` — until the test has
    /// independently confirmed the callback fault is genuinely in flight and
    /// blocked trying to acquire that same lock (a real 50ms window, not a race
    /// hoped to land). This forces the write to claim the slot first (the
    /// callback is gated until after the write's report call has already
    /// parked), so the loser rule is pinned deterministically under PROVEN
    /// contention rather than a coincidental absence of overlap: `MaxConcurrent
    /// == 1` is evidence, not a tautology, because the second producer had a
    /// real, confirmed opportunity to run concurrently and was made to wait for
    /// the lock instead.
    [Test]
    public async Task Concurrent_losers_are_serialized_into_the_sink() {
        var (server, tmp) = NewServer(); await using var _s = server; using var _t = tmp;
        var boom = new InvalidOperationException("callback exploded");
        var proceed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var entries = new List<(string Context, Exception Ex)>();
        var entriesGate = new object();
        var current = 0;
        var maxConcurrent = 0;
        var sinkCallCount = 0;
        var firstInSink = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseFirst = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var attachedObserved = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        void Sink(string c, Exception e) {
            var now = Interlocked.Increment(ref current);
            maxConcurrent = Math.Max(maxConcurrent, now);
            if (Interlocked.Increment(ref sinkCallCount) == 1) {
                firstInSink.TrySetResult();                    // tell the test: parked inside Report, holding _sinkLock
                releaseFirst.Task.GetAwaiter().GetResult();     // block synchronously until the test says go
            }
            lock (entriesGate) entries.Add((c, e));
            Interlocked.Decrement(ref current);
        }

        var client = new AgentAttachClient(server.Path, AgentId,
            (_, _, _) => { attachedObserved.TrySetResult(); return Task.CompletedTask; },
            async (_, _) => { await proceed.Task; throw boom; },    // stdout callback: armed, not yet fired
            Sink);
        var run = client.RunAsync(80, 24, CancellationToken.None);
        await server.AcceptAndPumpInboundAsync();
        await server.SendAttachedAsync(AgentId, []);
        await attachedObserved.Task;                               // Windows RST guard — see comment above
        await server.SendStdoutAsync([1]);
        await Task.Delay(50);                                      // pump is blocked inside the callback, gated on `proceed`

        server.CloseConnection();                                  // break the transport under the writer
        var writeTask = Task.Run(async () => await client.SendInputAsync([9]).ConfigureAwait(false));

        await firstInSink.Task;             // the write's own report has entered the sink and parked, holding _sinkLock
        proceed.SetResult();                // now let the callback fault fire — it must lose the already-claimed slot
        await Task.Delay(50);               // real wall-clock time for it to reach Report and block on the held lock
        releaseFirst.SetResult();           // release the write's parked call; the blocked callback call can now proceed

        await writeTask;                    // must not throw
        var outcome = await run;            // the write already won the slot: no callback fault is ever thrown here
        await client.DisposeAsync();

        await Assert.That(maxConcurrent).IsEqualTo(1);                          // serialized under proven contention
        await Assert.That(outcome).IsEqualTo(new AttachOutcome.ConnectionLost());
        await Assert.That(entries.Count(e => e.Context == "outbound write")).IsEqualTo(1);
        await Assert.That(entries.Count(e => ReferenceEquals(e.Ex, boom))).IsEqualTo(1);
        await Assert.That(entries.Count).IsEqualTo(2);
    }
}
