using System.Net.Sockets;
using System.Text.Json;
using Capacitor.Cli.Core.LocalIpc;

namespace Capacitor.Cli.Services;

public sealed record HelloProbeResult(bool WellFormed, int? ProtocolVersion, string? DaemonVersion, string? DaemonName);

/// <summary>
/// One-shot dial + Hello + HelloReply against a daemon's local control socket, bounded by
/// <c>timeout</c>. Mirrors the hello leg of <c>LocalControlClient.RunCycleAsync</c>
/// but deliberately WITHOUT its <c>status/1</c> capability gate — that gate is exactly what
/// install-verify (which validates version itself) and start-verify (which must accept a
/// capability-incompatible hello) must not apply.
/// </summary>
static class HelloProbe {
    static readonly HelloProbeResult NotWellFormed = new(false, null, null, null);

    public static async Task<HelloProbeResult> RunAsync(string daemonName, TimeSpan timeout) {
        using var cts = new CancellationTokenSource(timeout);
        try {
            var sock = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
            await using var stream = await ConnectAsync(sock, daemonName, cts.Token);

            await FrameCodec.WriteAsync(stream, new LocalFrame(FrameType.Hello), cts.Token);
            var reply = await FrameCodec.ReadAsync(stream, cts.Token);
            if (reply is null || reply.Type != FrameType.HelloReply) return NotWellFormed;

            var dto = JsonSerializer.Deserialize(reply.Text, HelloIpcJsonContext.Default.HelloReplyDto);
            if (dto is null) return NotWellFormed;
            return new HelloProbeResult(true, dto.ProtocolVersion, dto.DaemonVersion, dto.DaemonName);
        } catch {
            return NotWellFormed;
        }
    }

    static async Task<NetworkStream> ConnectAsync(Socket sock, string daemonName, CancellationToken ct) {
        try {
            await sock.ConnectAsync(new UnixDomainSocketEndPoint(LocalSocketPaths.Socket(daemonName)), ct);
            return new NetworkStream(sock, ownsSocket: true);
        } catch { sock.Dispose(); throw; }
    }
}
