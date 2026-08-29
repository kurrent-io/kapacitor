using System.Net.Sockets;
using System.Runtime.CompilerServices;
using System.Text.Json;

namespace Capacitor.Cli.Core.LocalIpc;

/// One permission subscription attempt as a typed stream. `Subscribed` is a client-local
/// boundary emitted after the subscribe write flushes; it does not prove the daemon registered
/// the subscription. The enumeration ending for any reason but caller cancellation means "this
/// attempt is over" — the consumer decides whether to go again.
public abstract record PermissionStreamEvent {
    public sealed record Subscribed : PermissionStreamEvent;
    public sealed record Pending(PermissionPendingDto Request) : PermissionStreamEvent;
    public sealed record Resolved(PermissionResolvedDto Settlement) : PermissionStreamEvent;
}

public static class PermissionSubscription {
    public static async IAsyncEnumerable<PermissionStreamEvent> RunAsync(
            DaemonStore store, string daemonName, [EnumeratorCancellation] CancellationToken ct = default) {
        using var sock = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
        NetworkStream? stream = null;
        try {
            try {
                await sock.ConnectAsync(new UnixDomainSocketEndPoint(store.SocketPath(daemonName)), ct);
                stream = new NetworkStream(sock, ownsSocket: false);
                await FrameCodec.WriteAsync(stream, new LocalFrame(FrameType.PermissionSubscribe), ct);
            } catch (OperationCanceledException) when (ct.IsCancellationRequested) {
                throw;
            } catch (Exception ex) when (ex is IOException or SocketException) {
                yield break;
            }

            yield return new PermissionStreamEvent.Subscribed();

            while (true) {
                LocalFrame? frame;
                try {
                    frame = await FrameCodec.ReadAsync(stream!, ct);
                } catch (OperationCanceledException) when (ct.IsCancellationRequested) {
                    throw;
                } catch (Exception ex) when (ex is IOException or SocketException or InvalidDataException) {
                    yield break;
                }
                if (frame is null) yield break;

                switch (frame.Type) {
                    case FrameType.PermissionPending: {
                        PermissionPendingDto? dto;
                        try { dto = JsonSerializer.Deserialize(frame.Text, PermissionIpcJsonContext.Default.PermissionPendingDto); }
                        catch (JsonException) { yield break; }
                        // Skipped, not fatal: ending here would make the resubscribe replay redeliver it forever.
                        if (!PermissionWire.IsPendingStructurallyValid(dto)) continue;
                        yield return new PermissionStreamEvent.Pending(dto!);
                        break;
                    }
                    case FrameType.PermissionResolved: {
                        PermissionResolvedDto? dto;
                        try { dto = JsonSerializer.Deserialize(frame.Text, PermissionIpcJsonContext.Default.PermissionResolvedDto); }
                        catch (JsonException) { yield break; }
                        if (dto is null || string.IsNullOrEmpty(dto.RequestId)) continue;
                        yield return new PermissionStreamEvent.Resolved(dto);
                        break;
                    }
                    default:
                        yield break; // protocol confusion
                }
            }
        } finally {
            if (stream is not null) await stream.DisposeAsync();
        }
    }
}
