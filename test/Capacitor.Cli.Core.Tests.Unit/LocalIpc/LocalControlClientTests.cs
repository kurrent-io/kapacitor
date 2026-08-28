using System.Net.Sockets;
using System.Text.Json;
using Capacitor.Cli.Core.LocalIpc;
using TUnit.Assertions.Enums;
using TUnit.Core.Enums;

namespace Capacitor.Cli.Core.Tests.Unit.LocalIpc;

/// <summary>
/// LocalControlClient state machine over a REAL Unix socket driven by a scripted server
/// (spec §4.4). All tests drive the internal seams with small REAL timeouts/backoff delays
/// (deterministic enough: the scripted peer answers or stalls instantly, so waits are bounded
/// by generous polling deadlines rather than tight tolerances — see
/// <see cref="Backoff_delay_advances_across_failures_and_resets_after_connected"/> for why a
/// real clock was chosen there over a <c>FakeTimeProvider</c>).
///
/// <para>Runs exclusively (<see cref="NotInParallelAttribute"/>): every test drives a REAL Unix
/// socket and a loopback-style handshake bounded by small real timeouts. Those bounds assume prompt
/// connect and event-stream propagation, which the rest of the assembly's socket and thread-pool load
/// can deny — so this class needs the host to itself.</para>
/// </summary>
[NotInParallel]
[ExcludeOn(OS.Windows)] // Unix-domain socket path
public class LocalControlClientTests {
    /// One scripted connection behavior; the server runs them in accept order and repeats
    /// the last script for further connections.
    delegate Task ConnScript(NetworkStream s, CancellationToken ct);

    sealed class ScriptedServer : IAsyncDisposable {
        readonly Socket _listener = new(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
        readonly CancellationTokenSource _cts = new();
        readonly ConnScript[] _scripts;
        volatile int _served;
        readonly Task _accept;

        /// Number of connections accepted so far. Written only from the accept loop; read from
        /// test code as a polled, monotonically-increasing counter (never decremented), so a
        /// plain volatile field — no lock needed — is enough for the poll-until-N pattern below.
        public int Served => _served;

        public ScriptedServer(string sockPath, params ConnScript[] scripts) {
            _scripts = scripts;
            _listener.Bind(new UnixDomainSocketEndPoint(sockPath));
            _listener.Listen(8);
            _accept = Task.Run(async () => {
                try {
                    while (!_cts.IsCancellationRequested) {
                        var conn = await _listener.AcceptAsync(_cts.Token);
                        var script = _scripts[Math.Min(_served++, _scripts.Length - 1)];
                        _ = Task.Run(async () => {
                            using var c = conn;
                            await using var s = new NetworkStream(c, ownsSocket: false);
                            try { await script(s, _cts.Token); } catch { /* scripted teardown */ }
                        }, _cts.Token);
                    }
                } catch { /* shutdown */ }
            });
        }

        public async ValueTask DisposeAsync() {
            _cts.Cancel();
            _listener.Dispose();
            if (_accept is { } a) { try { await a; } catch { } }
        }
    }

    // ---- script building blocks ----
    static string ValidStatusJson(string daemonName = "m", params string[] agentIds) {
        var agents = string.Join(',', agentIds.Select(id =>
            $$"""{"id":"{{id}}","kind":"agent","vendor":"codex","repo_path":null,"status":"Running","flow_run_id":null,"flow_role":null,"requester":null,"created_at":"2026-08-01T00:00:00Z","model":null}"""));
        return $$"""{"daemon":{"name":"{{daemonName}}","version":"1.0","server_url":"http://s","connection":"connected","max_agents":5,"active_agents":{{agentIds.Length}}},"agents":[{{agents}}]}""";
    }

    static ConnScript HelloThen(string replyJson) => async (s, ct) => {
        var f = await FrameCodec.ReadAsync(s, ct);                       // expect Hello
        if (f?.Type == FrameType.Hello)
            await FrameCodec.WriteAsync(s, LocalFrame.HelloJson(FrameType.HelloReply, replyJson), ct);
    };
    static ConnScript HelloEof() => FrameCodec.ReadAsync; // read, close silently
    static ConnScript HelloStall() => async (s, ct) => {
        await FrameCodec.ReadAsync(s, ct); await Task.Delay(Timeout.Infinite, ct);           // accept, never reply
    };
    /// Replies to Hello with a frame that decodes fine but isn't HelloReply — the "Error frame
    /// or any unexpected frame type answering Hello" branch of §4.2.
    static ConnScript HelloWrongFrameType() => async (s, ct) => {
        var f = await FrameCodec.ReadAsync(s, ct);
        if (f?.Type == FrameType.Hello)
            await FrameCodec.WriteAsync(s, LocalFrame.Error("nope"), ct);
    };
    /// Replies to Hello with a frame header whose type byte the codec has no case for —
    /// exercises the codec's own InvalidDataException path (undecodable frame), distinct from
    /// "decodes fine but is the wrong type" above.
    static ConnScript HelloUndecodable() => async (s, ct) => {
        await FrameCodec.ReadAsync(s, ct);
        var head = new byte[] { 200, 0, 0, 0, 0 }; // type=200 (unmapped), len=0
        await s.WriteAsync(head, ct);
    };
    static ConnScript SubscribePush(params string[] statusJsons) => async (s, ct) => {
        var f = await FrameCodec.ReadAsync(s, ct);                       // expect StatusSubscribe
        if (f?.Type != FrameType.StatusSubscribe) return;
        foreach (var json in statusJsons)
            await FrameCodec.WriteAsync(s, LocalFrame.StatusJson(FrameType.DaemonStatus, json), ct);
        await Task.Delay(Timeout.Infinite, ct);                          // stay open
    };
    /// Same push sequence as <see cref="SubscribePush"/> but returns (closing the connection)
    /// instead of staying open — used where the test wants a clean mid-stream EOF afterward.
    static ConnScript SubscribePushThenClose(params string[] statusJsons) => async (s, ct) => {
        var f = await FrameCodec.ReadAsync(s, ct);
        if (f?.Type != FrameType.StatusSubscribe) return;
        foreach (var json in statusJsons)
            await FrameCodec.WriteAsync(s, LocalFrame.StatusJson(FrameType.DaemonStatus, json), ct);
    };
    /// Same push sequence again, but afterward BLOCKS ON A READ instead of an infinite delay —
    /// a real Unix-socket peer close (not the server's own cancellation) unblocks that read
    /// with a clean EOF, which is the only way the SERVER side can observe that the CLIENT
    /// closed the subscribe connection. Used to pin that disposing the enumerator without
    /// cancelling (a bare `break`) still closes the socket.
    static ConnScript SubscribePushThenObserveClose(TaskCompletionSource closed, params string[] statusJsons) => async (s, ct) => {
        var f = await FrameCodec.ReadAsync(s, ct);
        if (f?.Type != FrameType.StatusSubscribe) return;
        foreach (var json in statusJsons)
            await FrameCodec.WriteAsync(s, LocalFrame.StatusJson(FrameType.DaemonStatus, json), ct);
        try { await FrameCodec.ReadAsync(s, ct); } catch { } // null (EOF) or an exception once the peer closes
        closed.TrySetResult();
    };
    static ConnScript SubscribeEof() => FrameCodec.ReadAsync;
    static ConnScript SubscribeStall() => async (s, ct) => {
        await FrameCodec.ReadAsync(s, ct); await Task.Delay(Timeout.Infinite, ct);
    };
    /// Answers StatusSubscribe with a decodable but semantically wrong frame type — the
    /// "or arriving on the subscribe connection" half of the same §4.2 branch as
    /// <see cref="HelloWrongFrameType"/>.
    static ConnScript SubscribeWrongFrameType() => async (s, ct) => {
        var f = await FrameCodec.ReadAsync(s, ct);
        if (f?.Type != FrameType.StatusSubscribe) return;
        await FrameCodec.WriteAsync(s, LocalFrame.Error("nope"), ct);
    };

    static string GoodHello(params string[] caps) => JsonSerializer.Serialize(
        new HelloReplyDto(1, "1.0", "m", [.. caps]), HelloIpcJsonContext.Default.HelloReplyDto);
    static string HelloWithVersion(string version, params string[] caps) => JsonSerializer.Serialize(
        new HelloReplyDto(1, version, "m", [.. caps]), HelloIpcJsonContext.Default.HelloReplyDto);

    /// Runs one scripted hello→subscribe cycle with the given raw JSON
    /// payloads, collecting events until either Connected or Unreachable is observed —
    /// used by the hello/snapshot identity-correlation tests below.
    static Task<List<LocalControlEvent>> RunScriptedCycleAsync(string helloJson, string statusJson) =>
        RunClientAsync(
            [HelloThen(helloJson), SubscribePush(statusJson)],
            evs => evs.OfType<LocalControlEvent.Connected>().Any() || evs.OfType<LocalControlEvent.Unreachable>().Any());

    /// Runs a client against scripts in an isolated socket dir; collects events until
    /// `until` returns true or the deadline passes; returns collected events.
    static async Task<List<LocalControlEvent>> RunClientAsync(
            ConnScript[] scripts, Func<List<LocalControlEvent>, bool> until,
            Action<LocalControlClient>? configure = null, TimeProvider? time = null) {
        using var daemons = new TempDaemonStore();
        const string name = "client";
        await using var server = new ScriptedServer(daemons.Store.SocketPath(name), scripts);
        var client = new LocalControlClient(daemons.Store, name, time) {
            RetryDelays = [TimeSpan.FromMilliseconds(1)],
            ConnectTimeout = TimeSpan.FromSeconds(2),
            HelloReplyTimeout = TimeSpan.FromMilliseconds(300),
            FirstSnapshotTimeout = TimeSpan.FromMilliseconds(300),
        };
        configure?.Invoke(client);
        var events = new List<LocalControlEvent>();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        try {
            await foreach (var e in client.RunAsync(cts.Token)) {
                events.Add(e);
                if (until(events)) break;
            }
        } catch (OperationCanceledException) { }
        return events;
    }

    /// Lock-guarded reads for the two tests below that poll a `List&lt;LocalControlEvent&gt;`
    /// from the test thread while a background `Task.Run` concurrently calls `events.Add` —
    /// `List&lt;T&gt;` isn't thread-safe, so an unguarded enumeration (`Count`, `OfType`, `Any`)
    /// racing an `Add` can throw `InvalidOperationException` (a real, if infrequent, CI flake).
    /// Both the writer and these readers take the SAME lock.
    static int CountLocked(object gate, List<LocalControlEvent> events) {
        lock (gate) return events.Count;
    }
    static bool HasConnectedLocked(object gate, List<LocalControlEvent> events) {
        lock (gate) return events.OfType<LocalControlEvent.Connected>().Any();
    }
    static int ConnectedCountLocked(object gate, List<LocalControlEvent> events) {
        lock (gate) return events.OfType<LocalControlEvent.Connected>().Count();
    }

    [Test]
    public async Task Gate_pass_yields_connecting_then_connected_with_first_snapshot_then_status() {
        var events = await RunClientAsync(
            [HelloThen(GoodHello("consent/1", "status/1")), SubscribePush(ValidStatusJson("m", "a1"), ValidStatusJson("m", "a1", "a2"))],
            evs => evs.OfType<LocalControlEvent.Status>().Any());

        await Assert.That(events[0]).IsTypeOf<LocalControlEvent.Connecting>();
        var connected = (LocalControlEvent.Connected)events[1];
        await Assert.That(connected.Capabilities!).Contains("status/1");
        await Assert.That(connected.FirstSnapshot.Agents.Count).IsEqualTo(1);
        var status = (LocalControlEvent.Status)events[2];
        await Assert.That(status.Snapshot.Agents.Count).IsEqualTo(2);
    }

    [Test]
    public async Task Capability_missing_and_hello_eof_classify_as_incompatible() {
        var noCap = await RunClientAsync(
            [HelloThen(GoodHello("consent/1"))],
            evs => evs.OfType<LocalControlEvent.Unreachable>().Any());
        await Assert.That(((LocalControlEvent.Unreachable)noCap[^1]).Reason).IsEqualTo("daemon_incompatible");

        var eof = await RunClientAsync(
            [HelloEof()],
            evs => evs.OfType<LocalControlEvent.Unreachable>().Any());
        await Assert.That(((LocalControlEvent.Unreachable)eof[^1]).Reason).IsEqualTo("daemon_incompatible");
    }

    [Test]
    public async Task Missing_socket_classifies_as_unreachable() {
        using var daemons = new TempDaemonStore();
        var client = new LocalControlClient(daemons.Store, "none") { RetryDelays = [TimeSpan.FromMilliseconds(1)] };
        var events = new List<LocalControlEvent>();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        await foreach (var e in client.RunAsync(cts.Token)) {
            events.Add(e);
            if (e is LocalControlEvent.Unreachable) break;
        }
        await Assert.That(((LocalControlEvent.Unreachable)events[^1]).Reason).IsEqualTo("daemon_unreachable");
    }

    // Note: a bespoke "connect refused" (file present, nothing listening) reproduction was
    // dropped — .NET unlinks the bound path on Socket disposal on this platform, so a stale
    // listener leaves no file behind to connect against; ENOENT and ECONNREFUSED both surface
    // as a plain SocketException, and Classify() doesn't discriminate between them (both map
    // to daemon_unreachable via the same catch-all), so Missing_socket_classifies_as_unreachable
    // above already exercises that code path.

    [Test] // "Error frame or any unexpected frame type answering Hello" (§4.2), both flavors
    public async Task Unexpected_and_undecodable_hello_replies_classify_as_incompatible() {
        var wrongType = await RunClientAsync(
            [HelloWrongFrameType()],
            evs => evs.OfType<LocalControlEvent.Unreachable>().Any());
        await Assert.That(((LocalControlEvent.Unreachable)wrongType[^1]).Reason).IsEqualTo("daemon_incompatible");

        var undecodable = await RunClientAsync(
            [HelloUndecodable()],
            evs => evs.OfType<LocalControlEvent.Unreachable>().Any());
        await Assert.That(((LocalControlEvent.Unreachable)undecodable[^1]).Reason).IsEqualTo("daemon_incompatible");
    }

    [Test] // same §4.2 branch, but "arriving on the subscribe connection" instead of answering Hello
    public async Task Unexpected_frame_type_on_subscribe_connection_classifies_as_incompatible() {
        var events = await RunClientAsync(
            [HelloThen(GoodHello("status/1")), SubscribeWrongFrameType()],
            evs => evs.OfType<LocalControlEvent.Unreachable>().Any());
        await Assert.That(events.OfType<LocalControlEvent.Connected>().Any()).IsFalse();
        await Assert.That(((LocalControlEvent.Unreachable)events[^1]).Reason).IsEqualTo("daemon_incompatible");
    }

    [Test] // silent peers classify via deadlines instead of hanging (spec §4.1)
    public async Task Silent_peers_classify_as_unreachable_via_phase_deadlines() {
        var helloStall = await RunClientAsync([HelloStall()],
            evs => evs.OfType<LocalControlEvent.Unreachable>().Any());
        await Assert.That(((LocalControlEvent.Unreachable)helloStall[^1]).Reason).IsEqualTo("daemon_unreachable");

        var subStall = await RunClientAsync([HelloThen(GoodHello("status/1")), SubscribeStall()],
            evs => evs.OfType<LocalControlEvent.Unreachable>().Any());
        await Assert.That(((LocalControlEvent.Unreachable)subStall[^1]).Reason).IsEqualTo("daemon_unreachable");
    }

    [Test] // malformed/invalid status is protocol evidence, first frame AND mid-stream, for both shapes
    public async Task Malformed_and_invalid_status_classify_as_incompatible() {
        string[] badShapes = ["{not json", """{"daemon":null,"agents":null}"""];

        foreach (var bad in badShapes) {
            var first = await RunClientAsync(
                [HelloThen(GoodHello("status/1")), SubscribePush(bad)],
                evs => evs.OfType<LocalControlEvent.Unreachable>().Any());
            await Assert.That(first.OfType<LocalControlEvent.Connected>().Any()).IsFalse();
            await Assert.That(((LocalControlEvent.Unreachable)first[^1]).Reason).IsEqualTo("daemon_incompatible");
        }

        foreach (var bad in badShapes) {
            var midStream = await RunClientAsync(
                [HelloThen(GoodHello("status/1")), SubscribePush(ValidStatusJson("m", "a1"), bad)],
                evs => evs.OfType<LocalControlEvent.Unreachable>().Any());
            await Assert.That(midStream.OfType<LocalControlEvent.Connected>().Any()).IsTrue();
            await Assert.That(((LocalControlEvent.Unreachable)midStream[^1]).Reason).IsEqualTo("daemon_incompatible");
        }
    }

    [Test] // subscribe-EOF before first frame: failed cycle, no Connected, no backoff reset
    public async Task Subscribe_eof_before_first_frame_is_a_failed_cycle() {
        var events = await RunClientAsync(
            [HelloThen(GoodHello("status/1")), SubscribeEof(), HelloThen(GoodHello("status/1")), SubscribeEof()],
            evs => evs.OfType<LocalControlEvent.Unreachable>().Any());
        await Assert.That(events.OfType<LocalControlEvent.Connected>().Any()).IsFalse();
    }

    [Test] // transition-only: a persistent outage yields ONE Unreachable however many cycles run
    public async Task Persistent_outage_yields_one_unreachable_event() {
        var events = await RunClientAsync(
            [HelloEof(), HelloEof(), HelloEof(), HelloThen(GoodHello("status/1")), SubscribePush(ValidStatusJson("m", "a1"))],
            evs => evs.OfType<LocalControlEvent.Connected>().Any());
        await Assert.That(events.OfType<LocalControlEvent.Unreachable>().Count()).IsEqualTo(1);
        // and the recovery reconnects with a FRESH first snapshot
        await Assert.That(events.OfType<LocalControlEvent.Connected>().Single().FirstSnapshot.Agents[0].Id).IsEqualTo("a1");
    }

    [Test] // a reason CHANGE yields a second event
    public async Task Reason_change_yields_a_new_unreachable_event() {
        var events = await RunClientAsync(
            [HelloEof(), HelloStall()],                    // incompatible, then unresponsive
            evs => evs.OfType<LocalControlEvent.Unreachable>().Count() >= 2);
        var reasons = events.OfType<LocalControlEvent.Unreachable>().Select(u => u.Reason).ToArray();
        await Assert.That(reasons).IsEquivalentTo(new[] { "daemon_incompatible", "daemon_unreachable" }, CollectionOrdering.Matching);
    }

    [Test] // spec decision 6: the hello reply's DaemonVersion propagates into the incompatible Unreachable
    public async Task Hello_reply_missing_status_cap_carries_daemon_version_into_incompatible() {
        var events = await RunClientAsync(
            [HelloThen(GoodHello("consent/1"))],              // DaemonVersion "1.0", no status/1
            evs => evs.OfType<LocalControlEvent.Unreachable>().Any());

        var unreachable = (LocalControlEvent.Unreachable)events[^1];
        await Assert.That(unreachable.Reason).IsEqualTo("daemon_incompatible");
        await Assert.That(unreachable.DaemonVersion).IsEqualTo("1.0");
    }

    [Test] // dedupe key is the (reason, version) PAIR: a version change re-emits even though the
           // reason stays "daemon_incompatible" across every cycle
    public async Task Daemon_version_change_while_incompatible_reemits_unreachable() {
        var events = await RunClientAsync(
            [HelloEof(),                                        // no dto ⇒ version null
             HelloThen(HelloWithVersion("1.0", "consent/1")),    // incompatible, version "1.0"
             HelloThen(HelloWithVersion("2.0", "consent/1"))],   // incompatible, version "2.0"
            evs => evs.OfType<LocalControlEvent.Unreachable>().Count() >= 3);

        var seen = events.OfType<LocalControlEvent.Unreachable>()
            .Select(u => (u.Reason, u.DaemonVersion)).ToArray();
        await Assert.That(seen).IsEquivalentTo(
            new[] { ("daemon_incompatible", null), ("daemon_incompatible", "1.0"), ("daemon_incompatible", "2.0") },
            CollectionOrdering.Matching);
    }

    [Test] // a transport failure never read a hello reply, so no version is ever known
    public async Task Transport_failure_has_null_daemon_version() {
        using var daemons = new TempDaemonStore();
        var client = new LocalControlClient(daemons.Store, "none-v") { RetryDelays = [TimeSpan.FromMilliseconds(1)] };
        var events = new List<LocalControlEvent>();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        await foreach (var e in client.RunAsync(cts.Token)) {
            events.Add(e);
            if (e is LocalControlEvent.Unreachable) break;
        }
        var unreachable = (LocalControlEvent.Unreachable)events[^1];
        await Assert.That(unreachable.Reason).IsEqualTo("daemon_unreachable");
        await Assert.That(unreachable.DaemonVersion).IsNull();
    }

    [Test] // daemon dies mid-stream → Unreachable; restart → Connected with fresh snapshot
    public async Task Mid_stream_death_then_restart_reconnects() {
        var events = await RunClientAsync(
            [HelloThen(GoodHello("status/1")), SubscribePushThenClose(ValidStatusJson("m", "a1")), // then conn closes
             HelloThen(GoodHello("status/1")), SubscribePush(ValidStatusJson("m", "a1", "a2"))],
            evs => evs.OfType<LocalControlEvent.Connected>().Count() >= 2);
        var second = events.OfType<LocalControlEvent.Connected>().Skip(1).Single();
        await Assert.That(second.FirstSnapshot.Agents.Count).IsEqualTo(2);
        await Assert.That(events.OfType<LocalControlEvent.Unreachable>().Count()).IsEqualTo(1);
    }

    [Test] // pins the disposal-leak fix: breaking out of the enumeration (no cancel) must still
           // close the live subscribe socket, not merely stop reading from it
    public async Task Breaking_out_of_the_enumeration_after_connected_disposes_the_subscribe_socket() {
        using var daemons = new TempDaemonStore();
        const string name = "client";
        var closed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var server = new ScriptedServer(daemons.Store.SocketPath(name),
            HelloThen(GoodHello("status/1")), SubscribePushThenObserveClose(closed, ValidStatusJson("m", "a1")));
        var client = new LocalControlClient(daemons.Store, name) { RetryDelays = [TimeSpan.FromMilliseconds(1)] };
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));

        await foreach (var e in client.RunAsync(cts.Token)) {
            // A bare `break` — never cancels `cts` — is exactly the disposal path the fix
            // targets: `await foreach` calls the enumerator's DisposeAsync() while it's
            // suspended at this very `yield return Connected`.
            if (e is LocalControlEvent.Connected) break;
        }

        // Without the fix this never completes (the server's read stays parked on a socket
        // nobody ever closed) and the test fails on this timeout — a deterministic signal,
        // not a flaky one: the WaitAsync bound only needs to be "generous enough", never
        // "tight enough".
        await closed.Task.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Test] // pins the completion-checkpoint fix: cancellation landing exactly between
           // RunCycleAsync succeeding and Connected being yielded must end the enumeration with
           // NO Connected ever surfacing, and must still close the stream it was handed
    public async Task Cancellation_landing_exactly_at_cycle_success_never_yields_connected() {
        using var daemons = new TempDaemonStore();
        const string name = "client";
        var closed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var server = new ScriptedServer(daemons.Store.SocketPath(name),
            HelloThen(GoodHello("status/1")), SubscribePushThenObserveClose(closed, ValidStatusJson("m", "a1")));
        var client = new LocalControlClient(daemons.Store, name) { RetryDelays = [TimeSpan.FromMilliseconds(1)] };
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        // Fires after RunCycleAsync has already returned success (the first valid snapshot
        // was read) but before RunAsync's ct checkpoint/yield — exactly the race the fix
        // closes.
        client.OnCycleSucceededForTest = cts.Cancel;

        var events = new List<LocalControlEvent>();
        await foreach (var e in client.RunAsync(cts.Token)) events.Add(e);

        await Assert.That(events.Count).IsEqualTo(1);
        await Assert.That(events[0]).IsTypeOf<LocalControlEvent.Connecting>();
        await Assert.That(events.OfType<LocalControlEvent.Connected>().Any()).IsFalse();

        // The stream handed back by the successful cycle must still be disposed on this
        // path — the server's blocked read observes the close.
        await closed.Task.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Test] // clean cancellation mid-backoff-wait, no fabricated events
    public async Task Cancellation_ends_the_enumeration_cleanly() {
        using var daemons = new TempDaemonStore();
        var client = new LocalControlClient(daemons.Store, "cxl") { RetryDelays = [TimeSpan.FromSeconds(30)] };
        using var cts = new CancellationTokenSource();
        var gate = new object();
        var events = new List<LocalControlEvent>();
        var run = Task.Run(async () => {
            await foreach (var e in client.RunAsync(cts.Token)) lock (gate) events.Add(e);
        });
        // wait for Connecting + first Unreachable, then cancel mid-backoff-wait. The poll
        // reads `events` from the test thread while the background task above still writes
        // it — both sides must go through the same lock, or an Add landing mid-enumeration
        // throws InvalidOperationException (List<T> is not thread-safe).
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (CountLocked(gate, events) < 2 && DateTime.UtcNow < deadline) await Task.Delay(10);
        cts.Cancel();
        await run.WaitAsync(TimeSpan.FromSeconds(5));
        // `run` has completed here, so the background writer is done — a plain read is safe.
        await Assert.That(events.Count).IsEqualTo(2); // nothing fabricated after cancel
    }

    [Test] // clean cancellation mid-stream (established connection, blocked on the next read)
    public async Task Cancellation_mid_stream_ends_the_enumeration_cleanly() {
        using var daemons = new TempDaemonStore();
        const string name = "client";
        await using var server = new ScriptedServer(daemons.Store.SocketPath(name),
            HelloThen(GoodHello("status/1")), SubscribePush(ValidStatusJson("m", "a1")));
        var client = new LocalControlClient(daemons.Store, name) { RetryDelays = [TimeSpan.FromMilliseconds(1)] };
        using var cts = new CancellationTokenSource();
        var gate = new object();
        var events = new List<LocalControlEvent>();
        var run = Task.Run(async () => {
            await foreach (var e in client.RunAsync(cts.Token)) lock (gate) events.Add(e);
        });

        // Same lock-guarded poll as above: the background writer and this read must not
        // race List<T>'s internal state.
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (!HasConnectedLocked(gate, events) && DateTime.UtcNow < deadline) await Task.Delay(10);

        cts.Cancel(); // cancels the pending (indefinitely blocked) read on the live stream
        await run.WaitAsync(TimeSpan.FromSeconds(5));

        // `run` has completed here, so the background writer is done — plain reads are safe.
        await Assert.That(events.Count).IsEqualTo(2); // Connecting, Connected — nothing fabricated after cancel
        await Assert.That(events.OfType<LocalControlEvent.Unreachable>().Any()).IsFalse();
    }

    [Test] // backoff advances across consecutive failures and resets to the start after a proven Connected
    public async Task Backoff_delay_advances_across_failures_and_resets_after_connected() {
        // Real TimeProvider.System (not FakeTimeProvider — see the class doc). Every assertion
        // below is deliberately ONE-SIDED:
        //  - "advanced" is a LOWER bound (deltaLong > 1s) — proves cycle1's wait was genuinely
        //    longer than a trivial reuse of the short delay, never an upper bound that a loaded
        //    CI box could blow through.
        //  - "reset" is proven by making a NON-reset schedule time out rather than by asserting
        //    an upper bound on the reset case: a third, minutes-long RetryDelays bucket means a
        //    schedule that failed to reset would need far longer than the poll deadline below,
        //    so WaitForServedAsync's own "eventually reaches N" assertion fails cleanly instead
        //    of racing a tight tolerance.
        using var daemons = new TempDaemonStore();
        const string name = "client";
        // cycle0 fails (index0 delay), cycle1 fails with the SAME reason (index1 delay —
        // proves the schedule advanced), cycle2 connects then immediately EOFs (a NEW
        // reason), cycle3 connects again — the backoff before cycle3's dial must be the
        // SHORT index0 delay again (proves the reset), not the un-reset index2 one.
        await using var server = new ScriptedServer(daemons.Store.SocketPath(name),
            HelloEof(), HelloEof(),
            HelloThen(GoodHello("status/1")), SubscribePushThenClose(ValidStatusJson("m", "a1")),
            HelloThen(GoodHello("status/1")), SubscribePush(ValidStatusJson("m", "a1", "a2")));

        var client = new LocalControlClient(daemons.Store, name) {
            // index0/index1 exercise "advances"; index2 is deliberately far outside the
            // poll deadline below so an un-reset schedule (which would land on index2,
            // since Math.Min(attempt, length-1) caps there once attempt=2) times out.
            RetryDelays = [TimeSpan.FromMilliseconds(100), TimeSpan.FromSeconds(2), TimeSpan.FromMinutes(2)],
            ConnectTimeout = TimeSpan.FromSeconds(10),
            HelloReplyTimeout = TimeSpan.FromSeconds(10),
            FirstSnapshotTimeout = TimeSpan.FromSeconds(10),
        };
        var events = new List<LocalControlEvent>();
        var gate = new object();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var run = Task.Run(async () => {
            await foreach (var e in client.RunAsync(cts.Token)) { lock (gate) events.Add(e); }
        });

        var sw = System.Diagnostics.Stopwatch.StartNew();
        async Task<TimeSpan> WaitForServedAsync(int n) {
            var deadline = DateTime.UtcNow.AddSeconds(10);
            while (server.Served < n && DateTime.UtcNow < deadline) await Task.Delay(5);
            await Assert.That(server.Served).IsGreaterThanOrEqualTo(n); // eventually-reaches-N: one-sided
            return sw.Elapsed;
        }

        await WaitForServedAsync(1);                     // cycle0 dialed
        var t1 = await WaitForServedAsync(2);             // cycle1 dialed, after the index0 (~100ms) backoff
        var t2 = await WaitForServedAsync(4);             // cycle2's hello+subscribe, after the index1 (~2s) backoff
        await WaitForServedAsync(6);                      // cycle3's hello+subscribe — only reachable within the
                                                            // poll deadline if the schedule actually reset

        // Served counts ACCEPTS, which run ahead of the client: the sixth accept lands before cycle3's
        // hello/subscribe has been read and its Connected emitted. Wait for the client to surface both
        // Connected events before cancelling, or the count assertion below races the very handshake it
        // is meant to observe.
        var connDeadline = DateTime.UtcNow.AddSeconds(10);
        while (ConnectedCountLocked(gate, events) < 2 && DateTime.UtcNow < connDeadline) await Task.Delay(5);

        cts.Cancel();
        try { await run; } catch (OperationCanceledException) { }

        var deltaLong = (t2 - t1).TotalMilliseconds; // the advanced (index1) delay
        await Assert.That(deltaLong).IsGreaterThan(1000); // well above the 100ms index0 delay — one-sided

        var reasons = events.OfType<LocalControlEvent.Unreachable>().Select(u => u.Reason).ToArray();
        await Assert.That(reasons).IsEquivalentTo(new[] { "daemon_incompatible", "daemon_unreachable" }, CollectionOrdering.Matching);
        await Assert.That(events.OfType<LocalControlEvent.Connected>().Count()).IsEqualTo(2);
    }

    // ---- hello↔snapshot instance correlation ----

    [Test]
    public async Task Mismatched_hello_and_snapshot_identity_never_emits_Connected() {
        // Use the suite's scripted-socket helper: hello reply from process A, snapshot from process B.
        // (Concretely: serve HelloReply {pid:111,instance_id:"A"} then DaemonStatus whose DaemonInfoDto
        // has {pid:222,instance_id:"B"}; iterate client.RunAsync and collect events until Unreachable.)
        var events = await RunScriptedCycleAsync(
            helloJson:  """{"protocol_version":1,"daemon_version":"1.0.0","daemon_name":"x","capabilities":["consent/1","status/1"],"pid":111,"instance_id":"A"}""",
            statusJson: """{"daemon":{"name":"x","version":"1.0.0","server_url":"http://s","connection":"connected","max_agents":5,"active_agents":0,"pid":222,"instance_id":"B"},"agents":[]}""");

        await Assert.That(events.OfType<LocalControlEvent.Connected>()).IsEmpty();
        await Assert.That(events.OfType<LocalControlEvent.Unreachable>().Any(u => u.Reason == "daemon_incompatible")).IsTrue();
    }

    [Test]
    public async Task Matching_hello_and_snapshot_identity_yields_connected_with_identity() {
        var events = await RunScriptedCycleAsync(
            helloJson:  """{"protocol_version":1,"daemon_version":"1.0.0","daemon_name":"x","capabilities":["consent/1","status/1"],"pid":111,"instance_id":"A"}""",
            statusJson: """{"daemon":{"name":"x","version":"1.0.0","server_url":"http://s","connection":"connected","max_agents":5,"active_agents":0,"pid":111,"instance_id":"A"},"agents":[]}""");

        var connected = events.OfType<LocalControlEvent.Connected>().Single();
        await Assert.That(connected.Identity).IsNotNull();
        await Assert.That(connected.Identity!.Pid).IsEqualTo(111);
        await Assert.That(connected.Identity!.InstanceId).IsEqualTo("A");
        await Assert.That(connected.Identity!.DaemonName).IsEqualTo("x");
        await Assert.That(connected.Identity!.DaemonVersion).IsEqualTo("1.0.0");
    }

    [Test]
    public async Task Hello_without_identity_fields_yields_connected_with_null_identity_pid() {
        // Pre-slice daemon: hello reply carries no pid/instance_id at all, even though the
        // snapshot (a current daemon's status payload) does — no mismatch may be inferred from
        // that asymmetry, so Connected must still fire, with Identity built from hello alone.
        var events = await RunScriptedCycleAsync(
            helloJson:  """{"protocol_version":1,"daemon_version":"1.0.0","daemon_name":"x","capabilities":["consent/1","status/1"]}""",
            statusJson: """{"daemon":{"name":"x","version":"1.0.0","server_url":"http://s","connection":"connected","max_agents":5,"active_agents":0,"pid":222,"instance_id":"B"},"agents":[]}""");

        var connected = events.OfType<LocalControlEvent.Connected>().Single();
        await Assert.That(connected.Identity).IsNotNull();
        await Assert.That(connected.Identity!.Pid).IsNull();
        await Assert.That(connected.Identity!.InstanceId).IsNull();
    }
}
