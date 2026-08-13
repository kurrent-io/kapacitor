using System.Net.Sockets;
using System.Text.Json;

namespace Capacitor.Cli.Core.LocalIpc;

/// One bounded probe cycle's outcome. Reachable=false covers BOTH a fully unreachable peer
/// (dial/connect failure) and a reachable-but-undecodable one (EOF, wrong frame type, bad
/// JSON) — a caller cannot and need not distinguish those from the outside; Hello/Snapshot are
/// null in that case. Reachable=true with Snapshot=null means hello answered but the SECOND
/// (StatusSubscribe) dial or read failed. IdentityConsistent is false whenever the two dials
/// might have landed on different daemon processes: either result missing, or the pid/instance
/// pair from hello disagreeing with the one on the first snapshot's daemon block (see ProbeAsync).
public sealed record ProbeResult(
    bool Reachable, HelloReplyDto? Hello, DaemonStatusDto? Snapshot, bool IdentityConsistent);

/// Bounded one-shot diagnostic dial: ONE hello connection, then a SEPARATE StatusSubscribe
/// connection read for its first snapshot only — both bounded by the same `timeout` budget.
/// Unlike <see cref="LocalControlClient"/> this never retries and never stays attached; it
/// exists for one-shot diagnostics (e.g. `kcap daemon status`, doctor-style checks) where "the
/// daemon didn't answer" is itself the useful signal, not something to retry through. It never
/// throws on an unreachable or undecodable peer — that's reported as Reachable=false — but a
/// caller's own cancellation (ct, as opposed to the internal timeout) still propagates, per
/// normal Task cancellation semantics.
public static class LocalControlProbe {
    /// <param name="timeout">A SINGLE shared budget for the whole call — not a per-dial
    /// timeout. It bounds the hello dial+read AND the StatusSubscribe dial+read together, so a
    /// slow hello leaves correspondingly less time for the snapshot half.</param>
    public static async Task<ProbeResult> ProbeAsync(string daemonName, TimeSpan timeout, CancellationToken ct = default) {
        using var timeoutCts = new CancellationTokenSource(timeout);
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(ct, timeoutCts.Token);
        var linked = linkedCts.Token;

        HelloReplyDto hello;
        try {
            await using var conn = await DialAsync(daemonName, linked);
            await FrameCodec.WriteAsync(conn, new LocalFrame(FrameType.Hello), linked);
            var frame = await FrameCodec.ReadAsync(conn, linked);
            if (frame is null || frame.Type != FrameType.HelloReply) return new ProbeResult(false, null, null, false);
            var dto = JsonSerializer.Deserialize(frame.Text, HelloIpcJsonContext.Default.HelloReplyDto);
            if (dto is null) return new ProbeResult(false, null, null, false);
            hello = dto;
        } catch (Exception ex) when (IsProbeFailure(ex, ct)) {
            return new ProbeResult(false, null, null, false);
        }

        try {
            await using var conn = await DialAsync(daemonName, linked);
            await FrameCodec.WriteAsync(conn, new LocalFrame(FrameType.StatusSubscribe), linked);
            var frame = await FrameCodec.ReadAsync(conn, linked);
            if (frame is null || frame.Type != FrameType.DaemonStatus) return new ProbeResult(true, hello, null, false);
            var snapshot = JsonSerializer.Deserialize(frame.Text, StatusIpcJsonContext.Default.DaemonStatusDto);
            // A well-formed-JSON-but-structurally-degenerate payload (e.g. {"daemon":null,...})
            // deserializes to a non-null DTO with null members — STJ leaves declared-non-nullable
            // reference members at their default on absent/null JSON rather than throwing (see
            // DaemonStatusValidator's own doc comment). Skipping this would either NRE on
            // snapshot.Daemon.Pid below or silently pass through a null-riddled Snapshot — same
            // validation LocalControlClient.ReadSnapshotAsync applies before ever trusting a DTO.
            if (!DaemonStatusValidator.IsValid(snapshot)) return new ProbeResult(true, hello, null, false);
            var daemonInfo = snapshot!.Daemon;

            var consistent = hello.Pid is null || daemonInfo.Pid is null
                || (hello.Pid == daemonInfo.Pid && hello.InstanceId == daemonInfo.InstanceId);
            return new ProbeResult(true, hello, snapshot, consistent);
        } catch (Exception ex) when (IsProbeFailure(ex, ct)) {
            return new ProbeResult(true, hello, null, false);
        }
    }

    static async Task<NetworkStream> DialAsync(string daemonName, CancellationToken ct) {
        var sock = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
        try {
            await sock.ConnectAsync(new UnixDomainSocketEndPoint(LocalSocketPaths.Socket(daemonName)), ct);
            return new NetworkStream(sock, ownsSocket: true);
        } catch { sock.Dispose(); throw; }
    }

    /// Every documented probe-failure exception, PLUS a timeout-driven cancellation — but NOT
    /// one driven by the caller's own `ct`, which must propagate like any other cancellation.
    static bool IsProbeFailure(Exception ex, CancellationToken callerCt) => ex switch {
        SocketException or IOException or InvalidDataException or JsonException => true,
        OperationCanceledException => !callerCt.IsCancellationRequested,
        _ => false,
    };
}
