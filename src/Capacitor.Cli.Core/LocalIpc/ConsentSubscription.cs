using System.Net.Sockets;
using System.Runtime.CompilerServices;
using System.Text.Json;

namespace Capacitor.Cli.Core.LocalIpc;

/// One consent subscription attempt as a typed stream (spec §4.2). `Subscribed` is a
/// CLIENT-LOCAL boundary — emitted right after the subscribe write flushes, before any read,
/// because an async iterator exposes no other observable point between "dialing" and
/// "subscribed" (with an empty replay the first read never completes). It does not prove the
/// daemon registered the subscription. The enumeration ENDING (for any reason but caller
/// cancellation) means "this attempt is over" — the consumer decides whether to go again.
public abstract record ConsentStreamEvent {
    public sealed record Subscribed : ConsentStreamEvent;
    public sealed record Pending(ConsentPendingDto Request) : ConsentStreamEvent;
}

public static class ConsentSubscription {
    public static async IAsyncEnumerable<ConsentStreamEvent> RunAsync(
            string daemonName, [EnumeratorCancellation] CancellationToken ct = default) {
        using var sock = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
        NetworkStream? stream = null;
        try {
            // Dial + subscribe write. ConsentSubscribeV2: a v1 daemon's codec rejects the byte
            // before routing and closes without replying — we yield Subscribed (the write
            // flushed) and then end on EOF, never registering a subscriber there (spec §4.1).
            try {
                await sock.ConnectAsync(new UnixDomainSocketEndPoint(LocalSocketPaths.Socket(daemonName)), ct);
                stream = new NetworkStream(sock, ownsSocket: false);
                await FrameCodec.WriteAsync(stream, new LocalFrame(FrameType.ConsentSubscribeV2), ct);
            } catch (OperationCanceledException) when (ct.IsCancellationRequested) {
                throw;
            } catch (Exception ex) when (ex is IOException or SocketException) {
                yield break; // failed dial/write: attempt over, no Subscribed, no clear (spec §5)
            }

            yield return new ConsentStreamEvent.Subscribed();

            while (true) {
                LocalFrame? frame;
                try {
                    frame = await FrameCodec.ReadAsync(stream!, ct);
                } catch (OperationCanceledException) when (ct.IsCancellationRequested) {
                    throw;
                } catch (Exception ex) when (ex is IOException or SocketException or InvalidDataException) {
                    yield break; // transport death / undecodable frame: attempt over
                }
                if (frame is null || frame.Type != FrameType.ConsentPending) yield break; // EOF / protocol confusion

                ConsentPendingDto? dto;
                try { dto = JsonSerializer.Deserialize(frame.Text, ConsentIpcJsonContext.Default.ConsentPendingDto); }
                catch (JsonException) { yield break; } // undecodable payload: dead connection

                // Structurally invalid (STJ leaves missing members null; `{}` decodes fine) is
                // SKIPPED, not fatal — ending here would thrash: the resubscribe replay would
                // redeliver the same invalid entry forever (spec §4.2).
                if (!IsStructurallyValid(dto)) continue;
                yield return new ConsentStreamEvent.Pending(dto!);
            }
        } finally {
            if (stream is not null) await stream.DisposeAsync();
        }
    }

    static bool IsStructurallyValid(ConsentPendingDto? dto) =>
        dto is not null
        && !string.IsNullOrEmpty(dto.RequestId)
        && !string.IsNullOrEmpty(dto.PromptId)   // consent/2 daemons always stamp it (spec §4.2)
        && !string.IsNullOrEmpty(dto.Kind)
        && !string.IsNullOrEmpty(dto.RepoPath)
        && !string.IsNullOrEmpty(dto.Vendor)
        && !string.IsNullOrEmpty(dto.RequestedAt)
        && dto.TimeoutSeconds > 0;
}
