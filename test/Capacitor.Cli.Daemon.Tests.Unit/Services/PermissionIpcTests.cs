using System.IO.Pipes;
using System.Text.Json;
using Capacitor.Cli.Core;
using Capacitor.Cli.Core.LocalIpc;
using Capacitor.Cli.Daemon.Services;
using Microsoft.Extensions.Logging.Abstractions;

namespace Capacitor.Cli.Daemon.Tests.Unit.Services;

public class PermissionIpcTests {
    static PermissionPendingDto Dto(string id = "r1") =>
        new(id, "a1", "s1", "claude", "Bash", null, null, false, false, "t");

    /// Composes two one-directional anonymous pipes into one duplex stream, so a reader and a
    /// writer on the same end never contend for the same pipe handle. HandleSubscribeAsync reads
    /// its EOF watcher and writes its pushed frames on the SAME stream concurrently — a plain
    /// Out-only pipe would throw on the watcher's first read and cancel the subscription instantly.
    sealed class DuplexPipeStream(Stream reader, Stream writer) : Stream {
        public override bool CanRead  => true;
        public override bool CanWrite => true;
        public override bool CanSeek  => false;
        public override long Length   => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

        public override void Flush() => writer.Flush();
        public override Task FlushAsync(CancellationToken ct) => writer.FlushAsync(ct);

        public override int Read(byte[] buffer, int offset, int count) => reader.Read(buffer, offset, count);
        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken ct) =>
            reader.ReadAsync(buffer, offset, count, ct);
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default) =>
            reader.ReadAsync(buffer, ct);

        public override void Write(byte[] buffer, int offset, int count) => writer.Write(buffer, offset, count);
        public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken ct) =>
            writer.WriteAsync(buffer, offset, count, ct);
        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken ct = default) =>
            writer.WriteAsync(buffer, ct);

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();

        protected override void Dispose(bool disposing) {
            if (disposing) { reader.Dispose(); writer.Dispose(); }
            base.Dispose(disposing);
        }
    }

    static (Stream server, Stream client) Duplex() {
        var serverToClient = new AnonymousPipeServerStream(PipeDirection.Out);
        var serverToClientRead = new AnonymousPipeClientStream(PipeDirection.In, serverToClient.ClientSafePipeHandle);
        var clientToServer = new AnonymousPipeServerStream(PipeDirection.Out);
        var clientToServerRead = new AnonymousPipeClientStream(PipeDirection.In, clientToServer.ClientSafePipeHandle);

        var server = new DuplexPipeStream(reader: clientToServerRead, writer: serverToClient);
        var client = new DuplexPipeStream(reader: serverToClientRead, writer: clientToServer);
        return (server, client);
    }

    [Test]
    public async Task Subscribe_replays_pending_then_pushes_resolved() {
        var broker = new PermissionPromptBroker();
        _ = broker.Register(Dto("r1"));
        var ipc = new PermissionIpc(broker, NullLogger<PermissionIpc>.Instance);
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var (server, client) = Duplex();
        var handler = ipc.HandleSubscribeAsync(server, cts.Token);

        var first = await FrameCodec.ReadAsync(client, cts.Token);
        await Assert.That(first!.Type).IsEqualTo(FrameType.PermissionPending);
        await Assert.That(JsonSerializer.Deserialize(first.Text, PermissionIpcJsonContext.Default.PermissionPendingDto)!.RequestId).IsEqualTo("r1");

        broker.TrySettle("r1", new PermissionDecision("allow", null, null), "allow", "server");
        var second = await FrameCodec.ReadAsync(client, cts.Token);
        await Assert.That(second!.Type).IsEqualTo(FrameType.PermissionResolved);
        await Assert.That(second.Text).Contains("\"source\":\"server\"");

        cts.Cancel();
        await handler;
        await Assert.That(broker.HasSubscriber).IsFalse();
    }

    [Test]
    [Arguments("""{"request_id":"r1","decision":"allow","apply_permissions":null,"updated_input":null}""", true, null)]
    [Arguments("""{"request_id":"nope","decision":"allow","apply_permissions":null,"updated_input":null}""", false, "no pending permission request with that id")]
    [Arguments("""{"request_id":"r1","decision":"maybe","apply_permissions":null,"updated_input":null}""", false, "invalid resolve payload (decision must be allow|deny)")]
    [Arguments("""{"decision":"allow"}""", false, "invalid resolve payload (decision must be allow|deny)")]
    [Arguments("""{ not json""", false, "malformed resolve payload")]
    public async Task Resolve_acks(string payload, bool ok, string? error) {
        var broker = new PermissionPromptBroker();
        var settlement = broker.Register(Dto("r1"));
        var ipc = new PermissionIpc(broker, NullLogger<PermissionIpc>.Instance);
        var (server, client) = Duplex();

        await ipc.HandleResolveAsync(payload, server, CancellationToken.None);
        var reply = await FrameCodec.ReadAsync(client, CancellationToken.None);
        await Assert.That(reply!.Type).IsEqualTo(FrameType.PermissionAck);
        var ack = JsonSerializer.Deserialize(reply.Text, PermissionIpcJsonContext.Default.PermissionAckDto)!;
        await Assert.That(ack.Ok).IsEqualTo(ok);
        await Assert.That(ack.Error).IsEqualTo(error);
        if (ok) {
            var s = await settlement;
            await Assert.That(s.Source).IsEqualTo("app");
            await Assert.That(s.Decision.Behavior).IsEqualTo("allow");
        }
    }

    [Test]
    public async Task Resolve_relays_apply_permissions_verbatim() {
        var broker = new PermissionPromptBroker();
        var settlement = broker.Register(Dto("r1"));
        var ipc = new PermissionIpc(broker, NullLogger<PermissionIpc>.Instance);
        var (server, _) = Duplex();
        await ipc.HandleResolveAsync("""{"request_id":"r1","decision":"allow","apply_permissions":[{"type":"toolAlwaysAllow","tool":"Bash"}],"updated_input":null}""", server, CancellationToken.None);
        var s = await settlement;
        await Assert.That(s.Decision.ApplyPermissions!.Value.GetRawText()).IsEqualTo("""[{"type":"toolAlwaysAllow","tool":"Bash"}]""");
    }
}
