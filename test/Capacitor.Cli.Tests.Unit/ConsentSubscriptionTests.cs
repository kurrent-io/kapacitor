using System.Net.Sockets;
using System.Text.Json;
using Capacitor.Cli.Core;
using Capacitor.Cli.Core.LocalIpc;

namespace Capacitor.Cli.Tests.Unit;

/// <summary>
/// ConsentSubscription.RunAsync over a REAL Unix socket driven by a scripted server (spec §4.2).
/// Harness conventions (short socket paths for the macOS sockaddr_un ~104-byte limit, Windows
/// guard, [NotInParallel], daemon-name→socket-path arrangement, the ScriptedOpsServer shape) are
/// copied from <see cref="LocalControlOpsTests"/>.
/// </summary>
public class ConsentSubscriptionTests {
    delegate Task ConnScript(Socket raw, NetworkStream s, CancellationToken ct);

    sealed class ScriptedOpsServer : IAsyncDisposable {
        readonly Socket _listener = new(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
        readonly CancellationTokenSource _cts = new();
        readonly ConnScript[] _scripts;
        volatile int _served;
        readonly Task _accept;

        public ScriptedOpsServer(string sockPath, params ConnScript[] scripts) {
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
                            try { await script(c, s, _cts.Token); } catch { /* scripted teardown */ }
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

    /// Reads the ConsentSubscribeV2 frame then parks on an awaited TaskCompletionSource so no
    /// reply EVER arrives — the empty-replay boundary (spec §4.2). Only server teardown (the
    /// script's own ct, cancelled by ScriptedOpsServer.DisposeAsync) unblocks this.
    static ConnScript SubscribeThenPark() => async (_, s, ct) => {
        var f = await FrameCodec.ReadAsync(s, ct);
        if (f?.Type != FrameType.ConsentSubscribeV2) return;
        var tcs = new TaskCompletionSource();
        await tcs.Task.WaitAsync(ct);
    };

    static ConnScript SubscribePush(params string[] pendingJsons) => async (_, s, ct) => {
        var f = await FrameCodec.ReadAsync(s, ct);
        if (f?.Type != FrameType.ConsentSubscribeV2) return;
        foreach (var json in pendingJsons)
            await FrameCodec.WriteAsync(s, LocalFrame.ConsentJson(FrameType.ConsentPending, json), ct);
    };

    /// Answers the subscribe with a decodable but wrong frame type — protocol confusion,
    /// distinct from an undecodable frame type (FrameCodec's own InvalidDataException path).
    static ConnScript SubscribeThenWrongFrameType() => async (_, s, ct) => {
        var f = await FrameCodec.ReadAsync(s, ct);
        if (f?.Type != FrameType.ConsentSubscribeV2) return;
        await FrameCodec.WriteAsync(s, LocalFrame.ConsentJson(FrameType.ConsentRules, "{}"), ct);
    };

    /// A faithful v1 daemon: reads the raw 5-byte header, sees a type byte its codec has no case
    /// for, and closes without writing ANY frame — never a routing-default Error reply (spec §4.1,
    /// the V1CodecReject shape from Task 4).
    static ConnScript V1CodecReject() => async (_, s, ct) => {
        var head = new byte[5];
        var read = 0;
        while (read < 5) {
            var n = await s.ReadAsync(head.AsMemory(read), ct);
            if (n == 0) return;
            read += n;
        }
    };

    static string ValidPendingJson(string requestId, string promptId) =>
        JsonSerializer.Serialize(
            new ConsentPendingDto(requestId, null, "tool", "/repo", "claude", "2026-08-08T00:00:00Z", 30, null, promptId),
            ConsentIpcJsonContext.Default.ConsentPendingDto);

    /// Builds a v1-shaped payload (every field besides prompt_id is valid) with the given
    /// literal fragment appended for the prompt_id member — "" omits the member entirely.
    static string PendingJsonWithPromptIdFragment(string requestId, string promptIdFragment) =>
        $$"""{"request_id":"{{requestId}}","requester":null,"kind":"tool","repo_path":"/repo","vendor":"claude","requested_at":"2026-08-08T00:00:00Z","timeout_seconds":30,"requester_display":null{{promptIdFragment}}}""";

    /// Runs `body` against an isolated socket dir with a scripted server listening for `name`.
    static async Task WithServerAsync(ConnScript[] scripts, Func<string, Task> body) {
        var sockDir = Directory.CreateTempSubdirectory("kcap-csx-");
        DaemonLockPaths.OverrideDirectoryForTesting(sockDir.FullName);
        try {
            var name = "csx-" + Guid.NewGuid().ToString("N")[..6];
            await using var server = new ScriptedOpsServer(LocalSocketPaths.Socket(name), scripts);
            await body(name);
        } finally {
            DaemonLockPaths.OverrideDirectoryForTesting(null);
            try { Directory.Delete(sockDir.FullName, true); } catch { }
        }
    }

    static async Task<List<ConsentStreamEvent>> CollectAsync(string daemonName, TimeSpan timeout) {
        using var cts = new CancellationTokenSource(timeout);
        var events = new List<ConsentStreamEvent>();
        await foreach (var e in ConsentSubscription.RunAsync(daemonName, cts.Token)) events.Add(e);
        return events;
    }

    [Test]
    [NotInParallel(nameof(DaemonLockPaths) + ".OverrideDirectoryForTesting")]
    public async Task Subscribed_is_yielded_after_the_write_and_before_any_frame() {
        if (OperatingSystem.IsWindows()) return;

        await WithServerAsync([SubscribeThenPark()], async name => {
            using var cts = new CancellationTokenSource();
            var enumerator = ConsentSubscription.RunAsync(name, cts.Token).GetAsyncEnumerator();
            try {
                var moved = await enumerator.MoveNextAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5));
                await Assert.That(moved).IsTrue();
                await Assert.That(enumerator.Current).IsTypeOf<ConsentStreamEvent.Subscribed>();
            } finally {
                await enumerator.DisposeAsync();
            }
        });
    }

    [Test]
    [NotInParallel(nameof(DaemonLockPaths) + ".OverrideDirectoryForTesting")]
    public async Task Replay_and_push_frames_yield_pending_events_in_order() {
        if (OperatingSystem.IsWindows()) return;

        var a = ValidPendingJson("r1", "p1");
        var b = ValidPendingJson("r2", "p2");
        await WithServerAsync([SubscribePush(a, b)], async name => {
            var events = await CollectAsync(name, TimeSpan.FromSeconds(5));

            await Assert.That(events.Count).IsEqualTo(3);
            await Assert.That(events[0]).IsTypeOf<ConsentStreamEvent.Subscribed>();
            var p0 = (ConsentStreamEvent.Pending)events[1];
            await Assert.That(p0.Request.RequestId).IsEqualTo("r1");
            var p1 = (ConsentStreamEvent.Pending)events[2];
            await Assert.That(p1.Request.RequestId).IsEqualTo("r2");
        });
    }

    [Test]
    [NotInParallel(nameof(DaemonLockPaths) + ".OverrideDirectoryForTesting")]
    public async Task Failed_connect_ends_without_subscribed() {
        if (OperatingSystem.IsWindows()) return;

        var sockDir = Directory.CreateTempSubdirectory("kcap-csx-");
        DaemonLockPaths.OverrideDirectoryForTesting(sockDir.FullName);
        try {
            var events = await CollectAsync("csx-none", TimeSpan.FromSeconds(5));
            await Assert.That(events.Count).IsEqualTo(0);
        } finally {
            DaemonLockPaths.OverrideDirectoryForTesting(null);
            try { Directory.Delete(sockDir.FullName, true); } catch { }
        }
    }

    [Test]
    [NotInParallel(nameof(DaemonLockPaths) + ".OverrideDirectoryForTesting")]
    public async Task Unexpected_frame_type_ends_the_enumeration() {
        if (OperatingSystem.IsWindows()) return;

        await WithServerAsync([SubscribeThenWrongFrameType()], async name => {
            var events = await CollectAsync(name, TimeSpan.FromSeconds(5));
            await Assert.That(events.Count).IsEqualTo(1);
            await Assert.That(events[0]).IsTypeOf<ConsentStreamEvent.Subscribed>();
        });
    }

    [Test]
    [NotInParallel(nameof(DaemonLockPaths) + ".OverrideDirectoryForTesting")]
    public async Task Undecodable_json_ends_the_enumeration() {
        if (OperatingSystem.IsWindows()) return;

        await WithServerAsync([SubscribePush("not-json")], async name => {
            var events = await CollectAsync(name, TimeSpan.FromSeconds(5));
            await Assert.That(events.Count).IsEqualTo(1);
            await Assert.That(events[0]).IsTypeOf<ConsentStreamEvent.Subscribed>();
        });
    }

    [Test]
    [NotInParallel(nameof(DaemonLockPaths) + ".OverrideDirectoryForTesting")]
    public async Task Structurally_invalid_pending_is_skipped_and_the_stream_continues() {
        if (OperatingSystem.IsWindows()) return;

        var valid = ValidPendingJson("r1", "p1");
        await WithServerAsync([SubscribePush("{}", valid)], async name => {
            var events = await CollectAsync(name, TimeSpan.FromSeconds(5));
            await Assert.That(events.Count).IsEqualTo(2);
            await Assert.That(events[0]).IsTypeOf<ConsentStreamEvent.Subscribed>();
            var pending = (ConsentStreamEvent.Pending)events[1];
            await Assert.That(pending.Request.RequestId).IsEqualTo("r1");
        });
    }

    [Test]
    [NotInParallel(nameof(DaemonLockPaths) + ".OverrideDirectoryForTesting")]
    public async Task Prompt_id_requirement_is_isolated() {
        if (OperatingSystem.IsWindows()) return;

        var absent = PendingJsonWithPromptIdFragment("r1", "");
        var nullValue = PendingJsonWithPromptIdFragment("r2", ",\"prompt_id\":null");
        var empty = PendingJsonWithPromptIdFragment("r3", ",\"prompt_id\":\"\"");
        var final = ValidPendingJson("r4", "p4");

        await WithServerAsync([SubscribePush(absent, nullValue, empty, final)], async name => {
            var events = await CollectAsync(name, TimeSpan.FromSeconds(5));
            await Assert.That(events.Count).IsEqualTo(2);
            await Assert.That(events[0]).IsTypeOf<ConsentStreamEvent.Subscribed>();
            var pending = (ConsentStreamEvent.Pending)events[1];
            await Assert.That(pending.Request.RequestId).IsEqualTo("r4");
        });
    }

    [Test]
    [NotInParallel(nameof(DaemonLockPaths) + ".OverrideDirectoryForTesting")]
    public async Task V1_codec_daemon_yields_subscribed_then_ends() {
        if (OperatingSystem.IsWindows()) return;

        await WithServerAsync([V1CodecReject()], async name => {
            var events = await CollectAsync(name, TimeSpan.FromSeconds(5));
            await Assert.That(events.Count).IsEqualTo(1);
            await Assert.That(events[0]).IsTypeOf<ConsentStreamEvent.Subscribed>();
        });
    }

    [Test]
    [NotInParallel(nameof(DaemonLockPaths) + ".OverrideDirectoryForTesting")]
    public async Task Cancellation_propagates() {
        if (OperatingSystem.IsWindows()) return;

        await WithServerAsync([SubscribeThenPark()], async name => {
            using var cts = new CancellationTokenSource();
            var enumTask = Task.Run(async () => {
                await foreach (var _ in ConsentSubscription.RunAsync(name, cts.Token)) { }
            });
            await Task.Delay(50); // let connect+write+Subscribed land so cancellation hits the pending read
            cts.Cancel();
            await Assert.ThrowsAsync<OperationCanceledException>(async () => await enumTask);
        });
    }
}
