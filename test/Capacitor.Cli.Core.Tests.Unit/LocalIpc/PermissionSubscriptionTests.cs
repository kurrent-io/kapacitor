using System.Net.Sockets;
using Capacitor.Cli.Core.LocalIpc;

namespace Capacitor.Cli.Core.Tests.Unit.LocalIpc;

public class PermissionSubscriptionTests {
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

    static ConnScript SubscribePush(params LocalFrame[] frames) => async (_, s, ct) => {
        var f = await FrameCodec.ReadAsync(s, ct);
        if (f?.Type != FrameType.PermissionSubscribe) return;
        foreach (var frame in frames) await FrameCodec.WriteAsync(s, frame, ct);
    };

    static ConnScript SubscribeThenWrongFrameType() => async (_, s, ct) => {
        var f = await FrameCodec.ReadAsync(s, ct);
        if (f?.Type != FrameType.PermissionSubscribe) return;
        await FrameCodec.WriteAsync(s, LocalFrame.ConsentJson(FrameType.ConsentRules, "{}"), ct);
    };

    static string Pending(string id, string toolName = "Bash") =>
        $$"""{"request_id":"{{id}}","agent_id":"a1","session_id":"s1","vendor":"claude","tool_name":"{{toolName}}","tool_input":null,"suggestions":null,"tool_input_omitted":false,"suggestions_omitted":false,"requested_at":"t"}""";

    static string Resolved(string id) => $$"""{"request_id":"{{id}}","outcome":"allow","source":"server"}""";

    static async Task WithServerAsync(ConnScript[] scripts, Func<DaemonStore, string, Task> body) {
        using var daemons = new TempDaemonStore();
        const string name = "permission";
        await using var server = new ScriptedOpsServer(daemons.Store.SocketPath(name), scripts);
        await body(daemons.Store, name);
    }

    static async Task<List<PermissionStreamEvent>> CollectAsync(DaemonStore store, string daemonName, TimeSpan timeout) {
        using var cts = new CancellationTokenSource(timeout);
        var events = new List<PermissionStreamEvent>();
        await foreach (var e in PermissionSubscription.RunAsync(store, daemonName, cts.Token)) events.Add(e);
        return events;
    }

    [Test]
    public async Task Subscribed_then_pending_then_resolved_in_order() {
        if (OperatingSystem.IsWindows()) return;

        await WithServerAsync([SubscribePush(
            LocalFrame.PermissionJson(FrameType.PermissionPending, Pending("r1")),
            LocalFrame.PermissionJson(FrameType.PermissionResolved, Resolved("r1")))], async (store, name) => {
            var events = await CollectAsync(store, name, TimeSpan.FromSeconds(5));

            await Assert.That(events.Count).IsEqualTo(3);
            await Assert.That(events[0]).IsTypeOf<PermissionStreamEvent.Subscribed>();
            await Assert.That(((PermissionStreamEvent.Pending)events[1]).Request.RequestId).IsEqualTo("r1");
            await Assert.That(((PermissionStreamEvent.Resolved)events[2]).Settlement.Source).IsEqualTo("server");
        });
    }

    [Test]
    public async Task Invalid_pending_is_skipped_and_empty_tool_name_is_delivered() {
        if (OperatingSystem.IsWindows()) return;

        await WithServerAsync([SubscribePush(
            LocalFrame.PermissionJson(FrameType.PermissionPending, "{}"),
            LocalFrame.PermissionJson(FrameType.PermissionPending, Pending("r2", toolName: "")))], async (store, name) => {
            var events = await CollectAsync(store, name, TimeSpan.FromSeconds(5));

            await Assert.That(events.Count).IsEqualTo(2);
            await Assert.That(events[0]).IsTypeOf<PermissionStreamEvent.Subscribed>();
            var pending = (PermissionStreamEvent.Pending)events[1];
            await Assert.That(pending.Request.RequestId).IsEqualTo("r2");
            await Assert.That(pending.Request.ToolName).IsEqualTo("");
        });
    }

    [Test]
    public async Task Wrong_frame_type_ends_the_attempt_after_subscribed() {
        if (OperatingSystem.IsWindows()) return;

        await WithServerAsync([SubscribeThenWrongFrameType()], async (store, name) => {
            var events = await CollectAsync(store, name, TimeSpan.FromSeconds(5));

            await Assert.That(events.Count).IsEqualTo(1);
            await Assert.That(events[0]).IsTypeOf<PermissionStreamEvent.Subscribed>();
        });
    }

    [Test]
    public async Task Failed_dial_yields_nothing() {
        if (OperatingSystem.IsWindows()) return;

        using var daemons = new TempDaemonStore();
        var events = await CollectAsync(daemons.Store, "nobody", TimeSpan.FromSeconds(5));

        await Assert.That(events.Count).IsEqualTo(0);
    }
}
