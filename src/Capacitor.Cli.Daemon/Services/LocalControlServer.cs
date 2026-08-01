using System.Net.Sockets;
using System.Text.Json;
using Capacitor.Cli.Core.LocalIpc;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Capacitor.Cli.Daemon.Services;

/// Accepts local (Unix-domain-socket / named-pipe) client connections and routes each
/// opening frame to <see cref="AgentOrchestrator"/>. The socket file is owner-only (0600)
/// — anything that can open it can spawn processes and stream a terminal, so it sits at
/// the same trust boundary as the daemon PID/lock files.
internal sealed partial class LocalControlServer(
        DaemonConfig config, AgentOrchestrator orchestrator, RestartCoordinator restart,
        LaunchConsentIpc consentIpc, ILogger<LocalControlServer> logger
    ) : BackgroundService {
    protected override async Task ExecuteAsync(CancellationToken ct) {
        var path = LocalSocketPaths.Socket(config.Name);
        try { if (File.Exists(path)) File.Delete(path); } catch { /* stale; bind fails loudly below */ }

        using var listener = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
        listener.Bind(new UnixDomainSocketEndPoint(path));
        if (!OperatingSystem.IsWindows())
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite); // 0600
        listener.Listen(16);
        LogListening(path);

        try {
            while (!ct.IsCancellationRequested) {
                var conn = await listener.AcceptAsync(ct);
                _ = HandleConnectionAsync(conn, ct); // fire-and-forget; handler owns its lifetime
            }
        } catch (OperationCanceledException) when (ct.IsCancellationRequested) { /* shutdown */ }
        finally { try { File.Delete(path); } catch { /* best-effort */ } }
    }

    async Task HandleConnectionAsync(Socket conn, CancellationToken ct) {
        using var _ = conn;
        await using var stream = new NetworkStream(conn, ownsSocket: false);
        try {
            var first = await FrameCodec.ReadAsync(stream, ct);
            if (first is null) return;
            switch (first.Type) {
                case FrameType.Spawn:  await orchestrator.HandleLocalSpawnAsync(first, stream, ct); break;
                case FrameType.Attach: await orchestrator.HandleLocalAttachAsync(first.Text, stream, ct); break;
                case FrameType.List:   await orchestrator.HandleLocalListAsync(stream, ct); break;
                case FrameType.Stop:   await orchestrator.HandleLocalStopAsync(first.Text, stream, ct); break;
                case FrameType.StopV2: {
                    var (force, id) = FrameCodec.StopV2(first);
                    await orchestrator.HandleLocalStopV2Async(force, id, stream, ct);

                    break;
                }
                case FrameType.Restart: await HandleRestartAsync(first.Text, stream, ct); break;
                case FrameType.ConsentSubscribe: await consentIpc.HandleSubscribeAsync(stream, ct); break;
                case FrameType.ConsentResolve:   await consentIpc.HandleResolveAsync(first.Text, stream, ct); break;
                case FrameType.ConsentRulesGet:  await consentIpc.HandleRulesGetAsync(stream, ct); break;
                case FrameType.ConsentRulesPut:  await consentIpc.HandleRulesPutAsync(first.Text, stream, ct); break;
                case FrameType.Hello: await HandleHelloAsync(first.Text, stream, ct); break;
                default: await FrameCodec.WriteAsync(stream, LocalFrame.Error($"expected Spawn/Attach/List/Stop/StopV2/Restart/ConsentSubscribe/ConsentResolve/ConsentRulesGet/ConsentRulesPut/Hello, got {first.Type}"), ct); break;
            }
        } catch (Exception ex) when (ex is not OperationCanceledException) {
            LogConnectionError(ex);
        }
    }

    async Task HandleRestartAsync(string mode, Stream stream, CancellationToken ct) {
        var force = mode is "force";

        // Bare "now" refuses while busy (don't silently queue); "when-idle"/"force" accept.
        if (mode is "now" && restart.IsBusy()) {
            await FrameCodec.WriteAsync(stream,
                LocalFrame.Error("daemon busy — agents running or eval in progress; use --when-idle or --force"), ct);

            return;
        }

        // RequestRestart evaluates immediately and reports what actually happened, so the
        // ack reflects reality (queued vs restarting vs failed vs manual-restart-required).
        var reply = restart.RequestRestart(force) switch {
            RestartRequestResult.Restarting     => LocalFrame.RestartAck("restarting"),
            RestartRequestResult.Queued         => LocalFrame.RestartAck("queued"),
            RestartRequestResult.Failed         => LocalFrame.Error("restart failed to start; the daemon will retry"),
            RestartRequestResult.ManualRequired => LocalFrame.Error("foreground daemon — exit and restart it manually"),
            _                                   => LocalFrame.Error("unknown restart result"),
        };
        await FrameCodec.WriteAsync(stream, reply, ct);
    }

    /// Answers an optional Hello with the daemon's protocol version, binary version,
    /// configured name, and capability list. The payload (a <see cref="ClientHelloDto"/>) is
    /// diagnostics-only — logged when present, never validated or gated on: an empty or
    /// malformed payload gets exactly the same reply as a well-formed one.
    async Task HandleHelloAsync(string payload, Stream stream, CancellationToken ct) {
        try {
            var hello = JsonSerializer.Deserialize(payload, HelloIpcJsonContext.Default.ClientHelloDto);
            if (hello is { ClientName.Length: > 0 }) LogClientHello(hello.ClientName, hello.ClientVersion ?? "");
        } catch (JsonException) { /* diagnostics-only payload; reply identically regardless */ }

        var reply = new HelloReplyDto(
            1, DaemonRunner.ResolveDaemonVersion(), config.Name, LocalControlCapabilities.Current);
        var json = JsonSerializer.Serialize(reply, HelloIpcJsonContext.Default.HelloReplyDto);
        await FrameCodec.WriteAsync(stream, LocalFrame.HelloJson(FrameType.HelloReply, json), ct);
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Local control socket listening at {Path}")]
    partial void LogListening(string path);
    [LoggerMessage(Level = LogLevel.Warning, Message = "Local control connection faulted")]
    partial void LogConnectionError(Exception ex);
    [LoggerMessage(Level = LogLevel.Debug, Message = "Client hello: {ClientName} {ClientVersion}")]
    partial void LogClientHello(string clientName, string clientVersion);
}
