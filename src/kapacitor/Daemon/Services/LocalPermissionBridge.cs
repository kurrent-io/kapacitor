using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace kapacitor.Daemon.Services;

/// <summary>
/// Localhost-only HTTP bridge that fronts the server's permission flow for spawned
/// Claude processes. The daemon's local Claude permission hook posts here instead of
/// going through the server's <c>/hooks/permission-request</c> route — that route runs
/// through Cloudflare which severs the long-poll at ~120s; the bridge invokes the
/// server's SignalR <c>RequestPermission</c> hub method over the daemon's persistent
/// connection, where no HTTP-request timeout applies.
///
/// Bound to <c>127.0.0.1</c> on a random ephemeral port. The orchestrator publishes
/// <see cref="BaseUrl"/> via the <c>KAPACITOR_DAEMON_URL</c> env var on every spawned
/// agent so the CLI <c>permission-request</c> command can detect and use it.
/// </summary>
internal sealed partial class LocalPermissionBridge(
        ServerConnection                server,
        ILogger<LocalPermissionBridge>  logger
    ) : IHostedService, IAsyncDisposable {
    HttpListener?            _listener;
    Task?                    _acceptLoop;
    CancellationTokenSource? _cts;

    public string? BaseUrl { get; private set; }

    public Task StartAsync(CancellationToken cancellationToken) {
        var port = ReserveFreeLoopbackPort();

        _listener = new HttpListener();
        _listener.Prefixes.Add($"http://127.0.0.1:{port}/");
        _listener.Start();

        BaseUrl = $"http://127.0.0.1:{port}";
        _cts    = new CancellationTokenSource();

        _acceptLoop = Task.Run(() => AcceptLoopAsync(_cts.Token), _cts.Token);
        LogBridgeStarted(logger, BaseUrl);

        return Task.CompletedTask;
    }

    public async Task StopAsync(CancellationToken cancellationToken) {
        if (_cts is not null) await _cts.CancelAsync();
        _listener?.Stop();

        if (_acceptLoop is not null) {
            try {
                await _acceptLoop.WaitAsync(TimeSpan.FromSeconds(2), cancellationToken);
            } catch { /* shutting down */ }
        }
    }

    public async ValueTask DisposeAsync() {
        await StopAsync(CancellationToken.None);
        _listener?.Close();
        _cts?.Dispose();
    }

    static int ReserveFreeLoopbackPort() {
        // HttpListener doesn't accept port 0 in its prefix; reserve a free ephemeral
        // port via TcpListener and immediately release. There's a TOCTOU window before
        // HttpListener.Start binds the same port, but on a single-user developer machine
        // the race is benign — port collisions are vanishingly rare.
        var probe = new TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        try { return ((IPEndPoint)probe.LocalEndpoint).Port; }
        finally { probe.Stop(); }
    }

    async Task AcceptLoopAsync(CancellationToken ct) {
        while (!ct.IsCancellationRequested && _listener!.IsListening) {
            HttpListenerContext context;
            try {
                context = await _listener.GetContextAsync();
            } catch (ObjectDisposedException) {
                break;
            } catch (HttpListenerException) {
                break;
            }

            // Fire-and-forget — each request is independent and the SignalR
            // round-trip blocks until the user decides (potentially hours).
            _ = Task.Run(() => HandleAsync(context, ct), ct);
        }
    }

    async Task HandleAsync(HttpListenerContext context, CancellationToken ct) {
        try {
            if (context.Request.Url?.AbsolutePath != "/permission-request" || context.Request.HttpMethod != "POST") {
                context.Response.StatusCode = 404;
                context.Response.Close();
                return;
            }

            using var reader = new StreamReader(context.Request.InputStream, Encoding.UTF8);
            var       body   = await reader.ReadToEndAsync(ct);
            var       node   = JsonNode.Parse(body);

            if (node is null) {
                context.Response.StatusCode = 400;
                context.Response.Close();
                return;
            }

            // Match the wire shape Claude's PermissionRequest hook posts: session_id is the
            // canonical (dashless) form, tool_name + tool_input + permission_suggestions are
            // pass-through.
            var sessionId = node["session_id"]?.GetValue<string>()?.Replace("-", "");

            if (sessionId is null) {
                context.Response.StatusCode = 400;
                context.Response.Close();
                return;
            }

            var toolName    = node["tool_name"]?.GetValue<string>();
            var toolInput   = ExtractElement(node, "tool_input");
            var suggestions = ExtractElement(node, "permission_suggestions");

            // Use a CTS chained on the request's lifetime so a closed/aborted local connection
            // (Claude exit, daemon shutdown) cancels the SignalR call promptly.
            using var requestCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            PermissionDecision decision;

            try {
                decision = await server.RequestPermissionAsync(sessionId, toolName, toolInput, suggestions, requestCts.Token);
            } catch (Exception ex) {
                LogRequestPermissionFailed(logger, ex, sessionId);
                decision = new PermissionDecision("deny", null, null);
            }

            var responseJson = BuildHookResponseJson(decision);
            var bytes        = Encoding.UTF8.GetBytes(responseJson);

            context.Response.ContentType   = "application/json";
            context.Response.StatusCode    = 200;
            context.Response.ContentLength64 = bytes.LongLength;
            await context.Response.OutputStream.WriteAsync(bytes, ct);
            context.Response.Close();
        } catch (Exception ex) {
            LogBridgeHandlerError(logger, ex);

            try {
                context.Response.StatusCode = 500;
                context.Response.Close();
            } catch { /* response already closed */ }
        }
    }

    static JsonElement? ExtractElement(JsonNode root, string property) {
        var child = root[property];
        if (child is null) return null;

        // JsonNode → JsonElement via raw JSON is the AOT-safe path; child.GetValue<JsonElement>()
        // is finicky on JsonObject children.
        return JsonDocument.Parse(child.ToJsonString()).RootElement.Clone();
    }

    static string BuildHookResponseJson(PermissionDecision decision) {
        // Mirrors the server-side BuildHookResponse. Claude expects camelCase keys here
        // (hookSpecificOutput, hookEventName, applyPermissions, updatedInput) — these are
        // outside the server's snake_case JSON convention because Claude defines them.
        var decisionNode = new JsonObject { ["behavior"] = decision.Behavior };

        if (decision.ApplyPermissions is { } ap) decisionNode["applyPermissions"] = JsonNode.Parse(ap.GetRawText());
        if (decision.UpdatedInput is { } ui)     decisionNode["updatedInput"]     = JsonNode.Parse(ui.GetRawText());

        var payload = new JsonObject {
            ["hookSpecificOutput"] = new JsonObject {
                ["hookEventName"] = "PermissionRequest",
                ["decision"]      = decisionNode
            }
        };

        return payload.ToJsonString();
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Local permission bridge listening on {BaseUrl}")]
    static partial void LogBridgeStarted(ILogger logger, string baseUrl);

    [LoggerMessage(Level = LogLevel.Warning, Message = "RequestPermission via SignalR failed for session {SessionId}; falling back to deny")]
    static partial void LogRequestPermissionFailed(ILogger logger, Exception exception, string sessionId);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Permission bridge handler error")]
    static partial void LogBridgeHandlerError(ILogger logger, Exception exception);
}
