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
    const int    MaxBindAttempts = 8;
    const string PathPrefix      = "/permission-request";

    HttpListener?            _listener;
    Task?                    _acceptLoop;
    CancellationTokenSource? _cts;
    string?                  _token;

    /// <summary>
    /// Full URL the spawned CLI hook command should POST to. Includes the random per-run
    /// token as a path segment so unrelated local processes can't pose as a Claude hook
    /// even if they discover the ephemeral port.
    /// </summary>
    public string? BaseUrl { get; private set; }

    public Task StartAsync(CancellationToken cancellationToken) {
        // The TcpListener-based port probe has a TOCTOU window before HttpListener.Start
        // binds the same port. Retry up to MaxBindAttempts on TRANSIENT bind failures so a
        // single rare race doesn't crash daemon startup. Non-transient errors (URLACL on
        // Windows, permission issues) bubble up immediately so they aren't masked.
        for (var attempt = 1; attempt <= MaxBindAttempts; attempt++) {
            var port  = ReserveFreeLoopbackPort();
            var token = Guid.NewGuid().ToString("N");

            var listener = new HttpListener();
            listener.Prefixes.Add($"http://127.0.0.1:{port}/{token}/");

            try {
                listener.Start();
                _listener = listener;
                _token    = token;
                BaseUrl   = $"http://127.0.0.1:{port}/{token}";
                break;
            } catch (HttpListenerException ex) when (attempt < MaxBindAttempts && IsAddressInUse(ex)) {
                LogBindRetry(logger, attempt, port, ex.Message);
                listener.Close();
            }
        }

        if (_listener is null)
            throw new InvalidOperationException($"Failed to bind LocalPermissionBridge after {MaxBindAttempts} attempts");

        _cts        = new CancellationTokenSource();
        _acceptLoop = Task.Run(() => AcceptLoopAsync(_cts.Token), _cts.Token);
        LogBridgeStarted(logger, BaseUrl!);

        return Task.CompletedTask;
    }

    /// <summary>
    /// Detects "address already in use" across platforms. HttpListenerException's ErrorCode
    /// is the underlying socket/Win32 error: 10048 = WSAEADDRINUSE (Windows), 48 = EADDRINUSE
    /// (macOS), 98 = EADDRINUSE (Linux). Anything else (URLACL denial code 5, etc.) is not
    /// transient and shouldn't be retried.
    /// </summary>
    static bool IsAddressInUse(HttpListenerException ex) =>
        ex.ErrorCode is 10048 or 48 or 98;

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
            // Require token + endpoint match. Token check first (constant-time-ish via string
            // equality is fine — discovery vector is the env var, not timing). The HttpListener
            // prefix already filters to /{token}/, but check explicitly so a misconfigured
            // listener prefix can't quietly admit anything.
            var path = context.Request.Url?.AbsolutePath;

            if (path != $"/{_token}{PathPrefix}" || context.Request.HttpMethod != "POST") {
                context.Response.StatusCode = 404;
                context.Response.Close();
                return;
            }

            using var reader = new StreamReader(context.Request.InputStream, Encoding.UTF8);
            var       body   = await reader.ReadToEndAsync(ct);

            JsonNode? node;
            try {
                node = JsonNode.Parse(body);
            } catch (JsonException) {
                // Malformed JSON from the local hook caller — that's a 400 (caller error),
                // not a 500 (daemon failure). Without this branch the outer Exception catch
                // would mislabel client-side parse errors as server faults.
                context.Response.StatusCode = 400;
                context.Response.Close();
                return;
            }

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

            // The HttpListener API doesn't expose a per-request "client disconnected" token,
            // so the SignalR call is bound to the daemon-shutdown token only. If Claude exits
            // mid-wait, the server hub call stays open until the user decides or the session
            // ends — wasteful but bounded by the ServerConnection's connection lifetime.
            // Switching to Kestrel + HttpContext.RequestAborted would give us per-request
            // cancellation; out of scope for this PR.
            PermissionDecision decision;

            try {
                decision = await server.RequestPermissionAsync(sessionId, toolName, toolInput, suggestions, ct);
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
        // is finicky on JsonObject children. Dispose the document — Clone() copies the buffer
        // so the returned element stays valid.
        using var doc = JsonDocument.Parse(child.ToJsonString());
        return doc.RootElement.Clone();
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

    [LoggerMessage(Level = LogLevel.Warning, Message = "Local permission bridge bind attempt {Attempt} on port {Port} failed: {Reason} — retrying")]
    static partial void LogBindRetry(ILogger logger, int attempt, int port, string reason);

    [LoggerMessage(Level = LogLevel.Warning, Message = "RequestPermission via SignalR failed for session {SessionId}; falling back to deny")]
    static partial void LogRequestPermissionFailed(ILogger logger, Exception exception, string sessionId);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Permission bridge handler error")]
    static partial void LogBridgeHandlerError(ILogger logger, Exception exception);
}
