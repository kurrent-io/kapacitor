using System.Net.Sockets;
using System.Text.Json;
using Capacitor.Cli.Core;
using Capacitor.Cli.Core.LocalIpc;
using Microsoft.Extensions.Logging;

namespace Capacitor.Cli.Daemon.Services;

/// Local-socket handlers for the permission frames. Trust model: anything on the daemon's own
/// 0600 socket is the owner — no further auth.
internal sealed class PermissionIpc(PermissionPromptBroker broker, ILogger<PermissionIpc> logger) {
    public async Task HandleSubscribeAsync(Stream stream, CancellationToken ct) {
        var (id, reader) = broker.Subscribe();
        try {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            // EOF watcher: a vanished subscriber must be reaped promptly, or the broker keeps
            // broadcasting into a channel nobody drains.
            _ = Task.Run(async () => {
                try { while (await FrameCodec.ReadAsync(stream, cts.Token) is not null) { } }
                catch { }
                try { cts.Cancel(); } catch (ObjectDisposedException) { }
            }, cts.Token);

            await foreach (var item in reader.ReadAllAsync(cts.Token)) {
                var frame = item switch {
                    PermissionStreamItem.Pending p => LocalFrame.PermissionJson(FrameType.PermissionPending,
                        JsonSerializer.Serialize(p.Dto, PermissionIpcJsonContext.Default.PermissionPendingDto)),
                    PermissionStreamItem.Resolved r => LocalFrame.PermissionJson(FrameType.PermissionResolved,
                        JsonSerializer.Serialize(r.Dto, PermissionIpcJsonContext.Default.PermissionResolvedDto)),
                    _ => throw new InvalidOperationException("unknown stream item"),
                };
                await FrameCodec.WriteAsync(stream, frame, cts.Token);
            }
        } catch (OperationCanceledException) {
        } catch (IOException) {
            // A vanished subscriber is normal lifecycle for a long-lived subscription, not a fault.
        } catch (SocketException) {
        } finally {
            broker.Unsubscribe(id);
        }
    }

    public async Task HandleResolveAsync(string payload, Stream stream, CancellationToken ct) {
        PermissionAckDto ack;
        try {
            var dto = JsonSerializer.Deserialize(payload, PermissionIpcJsonContext.Default.PermissionResolveDto);
            if (dto is null || string.IsNullOrEmpty(dto.RequestId) || dto.Decision is not ("allow" or "deny")) {
                ack = new PermissionAckDto(false, "invalid resolve payload (decision must be allow|deny)");
            } else {
                var decision = new PermissionDecision(dto.Decision, dto.ApplyPermissions, dto.UpdatedInput);
                var settled  = broker.TrySettle(dto.RequestId, decision, dto.Decision, PermissionSettlements.SourceApp);
                ack = settled
                    ? new PermissionAckDto(true, null)
                    : new PermissionAckDto(false, "no pending permission request with that id");
                if (!settled) logger.LogDebug("Permission resolve for {RequestId} lost the claim", dto.RequestId);
            }
        } catch (JsonException) {
            ack = new PermissionAckDto(false, "malformed resolve payload");
        }
        var json = JsonSerializer.Serialize(ack, PermissionIpcJsonContext.Default.PermissionAckDto);
        await FrameCodec.WriteAsync(stream, LocalFrame.PermissionJson(FrameType.PermissionAck, json), ct);
    }
}
