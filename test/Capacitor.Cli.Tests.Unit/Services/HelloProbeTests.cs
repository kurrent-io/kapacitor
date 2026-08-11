using System.Net.Sockets;
using System.Text.Json;
using Capacitor.Cli.Core;
using Capacitor.Cli.Core.LocalIpc;
using Capacitor.Cli.Services;

namespace Capacitor.Cli.Tests.Unit.Services;

/// <see cref="HelloProbe"/> exercised over a REAL Unix-domain socket — same harness idea as
/// LocalControlClientTests's ScriptedServer, but a single one-shot connection: HelloProbe is
/// one dial + Hello + reply, not a reconnecting long-lived client.
[NotInParallel(nameof(DaemonLockPaths) + ".OverrideDirectoryForTesting")]
public class HelloProbeTests {
    delegate Task ConnScript(NetworkStream s, CancellationToken ct);

    sealed class OneShotServer : IAsyncDisposable {
        readonly Socket _listener = new(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
        readonly CancellationTokenSource _cts = new();
        readonly Task _accept;

        public OneShotServer(string sockPath, ConnScript script) {
            _listener.Bind(new UnixDomainSocketEndPoint(sockPath));
            _listener.Listen(1);
            _accept = Task.Run(async () => {
                try {
                    using var conn = await _listener.AcceptAsync(_cts.Token);
                    await using var s = new NetworkStream(conn, ownsSocket: false);
                    await script(s, _cts.Token);
                } catch { /* shutdown or scripted teardown */ }
            });
        }

        public async ValueTask DisposeAsync() {
            _cts.Cancel();
            _listener.Dispose();
            try { await _accept; } catch { }
        }
    }

    static ConnScript ReplyWith(LocalFrame reply) => async (s, ct) => {
        var f = await FrameCodec.ReadAsync(s, ct);           // expect Hello
        if (f?.Type == FrameType.Hello)
            await FrameCodec.WriteAsync(s, reply, ct);
    };

    static async Task WithServerAsync(ConnScript script, Func<string, Task> body) {
        var sockDir = Directory.CreateTempSubdirectory("kcap-hp-");
        DaemonLockPaths.OverrideDirectoryForTesting(sockDir.FullName);
        var name = "hp-" + Guid.NewGuid().ToString("N")[..6];
        try {
            await using var server = new OneShotServer(LocalSocketPaths.Socket(name), script);
            await body(name);
        } finally {
            DaemonLockPaths.OverrideDirectoryForTesting(null);
            try { Directory.Delete(sockDir.FullName, true); } catch { /* best-effort */ }
        }
    }

    [Test]
    public async Task Valid_hello_reply_is_well_formed_with_fields_populated() {
        Skip.When(OperatingSystem.IsWindows(), "Unix-domain socket path");

        var replyJson = JsonSerializer.Serialize(
            new HelloReplyDto(1, "9.9.9", "probed-daemon", []), HelloIpcJsonContext.Default.HelloReplyDto);

        await WithServerAsync(ReplyWith(LocalFrame.HelloJson(FrameType.HelloReply, replyJson)), async name => {
            var result = await HelloProbe.RunAsync(name, TimeSpan.FromSeconds(5));

            await Assert.That(result.WellFormed).IsTrue();
            await Assert.That(result.ProtocolVersion).IsEqualTo(1);
            await Assert.That(result.DaemonVersion).IsEqualTo("9.9.9");
            await Assert.That(result.DaemonName).IsEqualTo("probed-daemon");
        });
    }

    [Test]
    public async Task No_listener_is_not_well_formed() {
        Skip.When(OperatingSystem.IsWindows(), "Unix-domain socket path");

        var sockDir = Directory.CreateTempSubdirectory("kcap-hp-");
        DaemonLockPaths.OverrideDirectoryForTesting(sockDir.FullName);
        try {
            var result = await HelloProbe.RunAsync("no-such-daemon", TimeSpan.FromSeconds(2));

            await Assert.That(result.WellFormed).IsFalse();
            await Assert.That(result.ProtocolVersion).IsNull();
            await Assert.That(result.DaemonVersion).IsNull();
            await Assert.That(result.DaemonName).IsNull();
        } finally {
            DaemonLockPaths.OverrideDirectoryForTesting(null);
            try { Directory.Delete(sockDir.FullName, true); } catch { /* best-effort */ }
        }
    }

    [Test]
    public async Task Error_frame_reply_is_not_well_formed() {
        Skip.When(OperatingSystem.IsWindows(), "Unix-domain socket path");

        await WithServerAsync(ReplyWith(LocalFrame.Error("nope")), async name => {
            var result = await HelloProbe.RunAsync(name, TimeSpan.FromSeconds(5));

            await Assert.That(result.WellFormed).IsFalse();
        });
    }
}
