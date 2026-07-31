using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Capacitor.Cli.Core;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Capacitor.Cli.Daemon.Services;

/// <summary>
/// Localhost-only HTTP bridge that fronts the server's permission flow for spawned
/// Claude processes. The daemon's local Claude permission hook posts here instead of
/// going through the server's <c>/hooks/permission-request</c> route — that route runs
/// through Cloudflare which severs the long-poll at ~120s; the bridge invokes the
/// server's SignalR <c>RequestPermission</c> hub method over the daemon's persistent
/// connection, where no HTTP-request timeout applies.
///
/// Bound to <c>127.0.0.1</c> on a random ephemeral port. The orchestrator publishes
/// <see cref="BaseUrl"/> via the <c>KCAP_DAEMON_URL</c> env var on every spawned
/// agent so the CLI <c>permission-request</c> command can detect and use it.
/// </summary>
internal sealed partial class LocalPermissionBridge(
        ServerConnection               server,
        ILogger<LocalPermissionBridge> logger
    ) : IHostedService, IAsyncDisposable {
    const int    MaxBindAttempts = 15;
    const string PathSuffix      = "/permission-request";

    static readonly object       PortClaimsLock = new();
    static readonly HashSet<int>  ClaimedPorts   = [];

    HttpListener?            _listener;
    Task?                    _acceptLoop;
    CancellationTokenSource? _cts;
    string?                  _sharedToken;
    int                      _port;
    int                      _listenerClosed;

    // Guards DisposeAsync so its body runs exactly once.
    //
    // DaemonRunner registers this type through TWO singleton descriptors —
    // AddSingleton<LocalPermissionBridge>() so the orchestrator can read the bound URL, and an
    // AddHostedService factory resolving that same instance so the listener starts before any
    // agent spawns. Microsoft DI tracks disposables per DESCRIPTOR and does not de-duplicate by
    // reference, so ServiceProviderEngineScope.DisposeAsync walks this one instance twice,
    // sequentially. No thread race is required.
    //
    // Without this guard the second pass reached StopAsync's _cts.CancelAsync() on an
    // already-disposed CTS, and the ObjectDisposedException surfaced inside
    // ServiceProviderEngineScope.DisposeAsync where nothing catches it — terminating the daemon
    // rather than shutting it down.
    int _disposed;

    // Live per-reviewer tokens → each token's bound (read-only) kcap allowlist servers. A request on
    // a reviewer token auto-approves that reviewer's kcap tools; the shared token keeps the
    // interactive prompt path. The token is a secret only the reviewer process holds, so an
    // interactive agent (which has only the shared token) can't reach the unattended path.
    readonly ConcurrentDictionary<string, string[]> _reviewerTokens = new(StringComparer.Ordinal);
    readonly object                                 _prefixLock     = new();

    /// <summary>
    /// Full URL the spawned CLI hook command should POST to. Includes the random per-run
    /// token as a path segment so unrelated local processes can't pose as a Claude hook
    /// even if they discover the ephemeral port. This is the SHARED (interactive) token; a
    /// review-flow reviewer instead gets a dedicated token from <see cref="RegisterReviewerToken"/>.
    /// </summary>
    public string? BaseUrl { get; private set; }

    public async Task StartAsync(CancellationToken cancellationToken) {
        // The TcpListener-based port probe has a TOCTOU window before HttpListener.Start
        // binds the same port. Retry up to MaxBindAttempts on TRANSIENT bind failures so a
        // single rare race doesn't crash daemon startup. Non-transient errors (URLACL on
        // Windows, permission issues) bubble up immediately so they aren't masked.
        for (var attempt = 1; attempt <= MaxBindAttempts; attempt++) {
            cancellationToken.ThrowIfCancellationRequested();

            var port = ReserveFreeLoopbackPort();
            if (!TryClaimPort(port)) {
                if (attempt < MaxBindAttempts)
                    await Task.Delay(Random.Shared.Next(10, 60), cancellationToken);
                continue;
            }

            var token = NewToken();

            var listener = new HttpListener();
            listener.Prefixes.Add($"http://127.0.0.1:{port}/{token}/");

            try {
                listener.Start();
                _listener    = listener;
                _listenerClosed = 0;
                _sharedToken = token;
                _port        = port;
                BaseUrl      = $"http://127.0.0.1:{port}/{token}";

                break;
            } catch (HttpListenerException ex) when (IsAddressInUse(ex)) {
                CloseSilently(listener);
                ReleasePortClaim(port);

                if (attempt == MaxBindAttempts) throw;

                LogBindRetry(logger, attempt, port, ex.Message);
                await Task.Delay(Random.Shared.Next(10, 60), cancellationToken);
            } catch {
                CloseSilently(listener);
                ReleasePortClaim(port);
                throw;
            }
        }

        if (_listener is null)
            throw new InvalidOperationException($"Failed to bind LocalPermissionBridge after {MaxBindAttempts} attempts");

        _cts        = new();
        _acceptLoop = Task.Run(() => AcceptLoopAsync(_cts.Token), _cts.Token);
        LogBridgeStarted(logger, BaseUrl!);
    }

    /// <summary>
    /// Detects "address already in use" across platforms. HttpListenerException's ErrorCode
    /// is the underlying socket/Win32 error: 10048 = WSAEADDRINUSE (Windows sockets), 32 =
    /// ERROR_SHARING_VIOLATION (Windows HttpListener prefix already occupied), 48 = EADDRINUSE
    /// (macOS), 98 = EADDRINUSE (Linux). Anything else (URLACL denial code 5, etc.) is not transient
    /// and shouldn't be retried.
    /// </summary>
    internal static bool IsAddressInUse(HttpListenerException ex) =>
        ex.ErrorCode is 10048 or 32 or 48 or 98;

    static bool TryClaimPort(int port) {
        lock (PortClaimsLock) return ClaimedPorts.Add(port);
    }

    static void ReleasePortClaim(int port) {
        if (port == 0) return;
        lock (PortClaimsLock) ClaimedPorts.Remove(port);
    }

    static void CloseSilently(HttpListener listener) {
        try { listener.Close(); } catch { /* best-effort cleanup after a failed bind */ }
    }

    public async Task StopAsync(CancellationToken cancellationToken) {
        // Read the field ONCE: DisposeAsync nulls it after disposing, so re-reading could see a
        // non-null reference here and null moments later. Cancelling an already-disposed CTS
        // throws, and this method runs on the host's dispose path where a throw is fatal — so
        // treat that as an already-stopped bridge rather than an error.
        var cts = Interlocked.CompareExchange(ref _cts, null, null);
        if (cts is not null) {
            try { await cts.CancelAsync(); }
            catch (ObjectDisposedException) { /* already stopped and disposed — nothing to cancel */ }
        }

        // Close exactly once, before awaiting the accept loop. Stop() alone releases the port but
        // leaves HttpListener's prefix registered until a later Close(); another bridge can claim
        // that port in between, making the old listener's eventual Close() throw EADDRINUSE.
        // Close() both stops the listener and unregisters its prefix as one shutdown operation.
        var listener = _listener;
        if (listener is not null && Interlocked.Exchange(ref _listenerClosed, 1) == 0)
            listener.Close();

        if (_acceptLoop is not null) {
            try {
                await _acceptLoop.WaitAsync(TimeSpan.FromSeconds(2), cancellationToken);
            } catch {
                /* shutting down */
            }
        }
    }

    /// <summary>
    /// Idempotent and non-throwing. Runs its body exactly once even under a concurrent second
    /// call, and swallows anything StopAsync raises: this executes inside the DI/host teardown
    /// (ServiceProviderEngineScope.DisposeAsync), where an escaping exception is unhandled and
    /// terminates the daemon instead of shutting it down.
    /// </summary>
    public async ValueTask DisposeAsync() {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;

        try {
            await StopAsync(CancellationToken.None);
        } catch (Exception ex) {
            // Never let a teardown failure escape into the host's dispose path.
            LogDisposeFailed(logger, ex);
        } finally {
            ReleasePortClaim(_port);
            _port = 0;
            // Null it out before disposing so a racing StopAsync sees "already stopped" rather
            // than a live-looking reference to a CTS that is about to be (or already is) disposed.
            var cts = Interlocked.Exchange(ref _cts, null);
            cts?.Dispose();
        }
    }

    /// <summary>
    /// Mint a dedicated bridge token for an unattended review-flow reviewer, bound to the read-only
    /// kcap servers it may auto-approve (<paramref name="allowlistServers"/>, canonical ids). Returns
    /// the full URL the reviewer must use as its <c>KCAP_DAEMON_URL</c>. The token is a CSPRNG secret
    /// and gets its own listener prefix so only that reviewer's hook can reach the unattended path.
    /// Revoke with <see cref="RevokeReviewerToken"/> once the reviewer exits.
    /// </summary>
    public string RegisterReviewerToken(IReadOnlyList<string> allowlistServers) {
        if (_listener is null || _sharedToken is null)
            throw new InvalidOperationException("LocalPermissionBridge not started");

        lock (_prefixLock) {
            var token = NewToken();
            while (string.Equals(token, _sharedToken, StringComparison.Ordinal) || _reviewerTokens.ContainsKey(token))
                token = NewToken();   // CSPRNG collisions are negligible; never silently reuse one

            _listener.Prefixes.Add($"http://127.0.0.1:{_port}/{token}/");
            _reviewerTokens[token] = [.. allowlistServers];

            return $"http://127.0.0.1:{_port}/{token}";
        }
    }

    /// <summary>Revoke a reviewer token (accepts the URL from <see cref="RegisterReviewerToken"/> or
    /// the bare token). Idempotent. After revocation, requests on that token 404 (fail-safe).</summary>
    public void RevokeReviewerToken(string reviewerBridgeUrlOrToken) {
        var token = ExtractToken(reviewerBridgeUrlOrToken);
        if (token is null) return;

        lock (_prefixLock) {
            if (_reviewerTokens.TryRemove(token, out _))
                _listener?.Prefixes.Remove($"http://127.0.0.1:{_port}/{token}/");
        }
    }

    /// <summary>Test seam: number of live reviewer tokens (verifies mint/revoke without a real
    /// HTTP round-trip, so orchestrator tests needn't contend on a loopback port).</summary>
    internal int ReviewerTokenCountForTest => _reviewerTokens.Count;

    // 128 bits of CSPRNG entropy as 32 lowercase hex chars — same shape as the original shared
    // token, unguessable, and safe to place in a bearer URL.
    static string NewToken() => RandomNumberGenerator.GetHexString(32, lowercase: true);

    // Accept either the full reviewer URL (http://127.0.0.1:{port}/{token}) or a bare token.
    static string? ExtractToken(string urlOrToken) {
        if (string.IsNullOrWhiteSpace(urlOrToken)) return null;

        return Uri.TryCreate(urlOrToken, UriKind.Absolute, out var uri)
            ? uri.AbsolutePath.Trim('/')
            : urlOrToken.Trim('/');
    }

    /// <summary>Instance-scoped test seam for deterministic port-collision coverage.</summary>
    internal Func<int>? ReserveLoopbackPortOverrideForTest;

    int ReserveFreeLoopbackPort() {
        if (ReserveLoopbackPortOverrideForTest is { } overridePort) return overridePort();

        // HttpListener doesn't accept port 0 in its prefix; reserve a free ephemeral
        // port via TcpListener and immediately release. There's a TOCTOU window before
        // HttpListener.Start binds the same port, but on a single-user developer machine
        // the race is benign — port collisions are vanishingly rare.
        var probe = new TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        try { return ((IPEndPoint)probe.LocalEndpoint).Port; } finally { probe.Stop(); }
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
            // Require token + vendor + endpoint match. The HttpListener prefix already routed us
            // here, but we re-validate explicitly so a stray prefix can't quietly admit anything.
            // Path shape: /{token}/{vendor}/permission-request.
            var path = context.Request.Url?.AbsolutePath;

            if (path is null
             || !path.EndsWith(PathSuffix, StringComparison.Ordinal)
             || context.Request.HttpMethod != "POST") {
                context.Response.StatusCode = 404;
                context.Response.Close();

                return;
            }

            // Extract the token (first path segment) and classify it against the LIVE token set:
            // the shared token → interactive; a live reviewer token → unattended auto-approve. An
            // unknown or revoked token fails safe with a 404.
            var trimmed    = path.TrimStart('/');
            var firstSlash = trimmed.IndexOf('/');

            if (firstSlash <= 0) {
                context.Response.StatusCode = 404;
                context.Response.Close();

                return;
            }

            var token      = trimmed[..firstSlash];
            var isShared   = string.Equals(token, _sharedToken, StringComparison.Ordinal);
            var isReviewer = _reviewerTokens.TryGetValue(token, out var reviewerAllowlist);

            if (!isShared && !isReviewer) {
                context.Response.StatusCode = 404;
                context.Response.Close();

                return;
            }

            // Vendor is the segment between "/{token}/" and the "/permission-request" suffix.
            var afterToken = path[(token.Length + 2)..];
            var vendor     = afterToken.Length > PathSuffix.Length ? afterToken[..^PathSuffix.Length] : "";

            if (vendor is not ("claude" or "codex")) {
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

            if (string.IsNullOrWhiteSpace(sessionId)) {
                context.Response.StatusCode = 400;
                context.Response.Close();

                return;
            }

            var toolName    = node["tool_name"]?.GetValue<string>();
            var toolInput   = ExtractElement(node, "tool_input");
            var suggestions = ExtractElement(node, "permission_suggestions");

            // The HttpListener API doesn't expose a per-request "client disconnected" token,
            // so the SignalR call is bound to the daemon-shutdown token only. RequestPermissionAsync
            // now retries across reconnects, so if Claude exits mid-wait the server hub call can
            // stay open across reconnects until the user decides or the daemon shuts down — it is
            // NOT bounded by a single connection's lifetime (the hook client's ~10h timeout is the
            // practical end-to-end ceiling). Switching to Kestrel + HttpContext.RequestAborted
            // would give us per-request cancellation; out of scope for this PR.
            PermissionDecision decision;

            if (IsFlowResultSubmission(toolName)) {
                // The reviewer's own result-submission tool (kcap-flow-result → submit_review_result)
                // is unique to a server only injected for review-flow reviewers, so it's always safe.
                // Auto-approve on ANY live token without a server round-trip — an unattended reviewer
                // can't get a user decision otherwise.
                LogFlowResultAutoApproved(logger, sessionId, vendor);
                decision = new PermissionDecision("allow", null, null);
            } else if (isReviewer) {
                // Unattended reviewer: auto-approve its bound kcap tools; DENY an out-of-allowlist
                // (or non-config-locked-vendor bare) call outright rather than defer to a prompt no
                // human can answer. A well-formed tool name is required to classify.
                if (string.IsNullOrWhiteSpace(toolName)) {
                    context.Response.StatusCode = 400;
                    context.Response.Close();

                    return;
                }

                if (IsReviewerToolAllowed(vendor, toolName, reviewerAllowlist!)) {
                    decision = new PermissionDecision("allow", null, null);
                } else {
                    LogReviewerToolDenied(logger, sessionId, toolName);
                    decision = new PermissionDecision("deny", null, null);
                }
            } else {
                // Shared (interactive) token → the server permission path, unchanged.
                try {
                    decision = await server.RequestPermissionAsync(sessionId, toolName, toolInput, suggestions, ct);
                } catch (Exception ex) {
                    LogRequestPermissionFailed(logger, ex, sessionId);
                    decision = new PermissionDecision("deny", null, null);
                }
            }

            var responseJson = BuildHookResponseJson(decision, vendor);
            var bytes        = Encoding.UTF8.GetBytes(responseJson);

            context.Response.ContentType     = "application/json";
            context.Response.StatusCode      = 200;
            context.Response.ContentLength64 = bytes.LongLength;
            await context.Response.OutputStream.WriteAsync(bytes, ct);
            context.Response.Close();
        } catch (Exception ex) {
            LogBridgeHandlerError(logger, ex);

            try {
                context.Response.StatusCode = 500;
                context.Response.Close();
            } catch {
                /* response already closed */
            }
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

    /// <summary>
    /// True when the permission request is for the review-flow reviewer's result-submission tool
    /// (the <c>kcap-flow-result</c> server's <c>submit_review_result</c>). This auto-approve bypasses
    /// the server permission boundary, so the match is deliberately precise rather than a loose
    /// substring: it accepts either the bare tool name (a vendor that passes the raw MCP tool name,
    /// e.g. Codex) OR a vendor-prefixed id that both names the <c>kcap-flow-result</c> server AND ends
    /// in the exact tool — e.g. Claude's <c>mcp__kcap_flow_result__submit_review_result</c> (Claude
    /// sanitizes the hyphens to underscores). Requiring the server token means a coincidental
    /// "…submit_review_result" exposed by some other MCP server on an interactive hosted agent can't
    /// slip past the prompt.
    /// </summary>
    static bool IsFlowResultSubmission(string? toolName) {
        if (string.IsNullOrEmpty(toolName)) return false;

        // Bare tool name, no server prefix.
        if (string.Equals(toolName, "submit_review_result", StringComparison.Ordinal)) return true;

        // Vendor-prefixed MCP id: require the flow-result server identity AND the exact tool suffix.
        var namesFlowResultServer =
            toolName.Contains("kcap_flow_result", StringComparison.Ordinal) ||
            toolName.Contains("kcap-flow-result", StringComparison.Ordinal);

        return namesFlowResultServer && toolName.EndsWith("submit_review_result", StringComparison.Ordinal);
    }

    /// <summary>
    /// Whether a tool call arriving on a reviewer token is within the reviewer's bound (read-only)
    /// kcap allowlist. A BARE tool name (no server qualifier) is allowed ONLY for a config-locked
    /// vendor (<c>codex</c>): its MCP config confines callable tools to the bound servers, so the
    /// token — not the name — is the authorization. Any other vendor's bare name (e.g. Claude's
    /// built-in <c>Bash</c>) is NOT proven to be a kcap tool → denied. A SERVER-QUALIFIED name
    /// (Claude's <c>mcp__&lt;server&gt;__&lt;tool&gt;</c>) is allowed only when <c>&lt;server&gt;</c>
    /// is in the bound allowlist. Matching is hyphen/underscore- and case-insensitive.
    /// </summary>
    static bool IsReviewerToolAllowed(string vendor, string toolName, string[] boundAllowlist) {
        const string prefix = "mcp__";

        // Bare name: only a config-locked vendor's bare names are provably kcap tools. Codex
        // clears+whitelists mcp_servers; any other vendor's bare name is denied.
        if (!toolName.StartsWith(prefix, StringComparison.Ordinal))
            return string.Equals(vendor, "codex", StringComparison.Ordinal);

        var afterPrefix = toolName[prefix.Length..];
        var sep         = afterPrefix.IndexOf("__", StringComparison.Ordinal);

        if (sep <= 0) return false;   // malformed qualified name → deny (fail-safe)

        var server = afterPrefix[..sep].Replace('-', '_');

        foreach (var allowed in boundAllowlist)
            if (string.Equals(server, allowed.Replace('-', '_'), StringComparison.OrdinalIgnoreCase))
                return true;

        return false;
    }

    static string BuildHookResponseJson(PermissionDecision decision, string vendor) =>
        vendor switch {
            "claude" => BuildClaudeResponse(decision),
            "codex"  => BuildCodexResponse(decision),
            _        => throw new InvalidOperationException($"Unsupported vendor: {vendor}")
        };

    static string BuildClaudeResponse(PermissionDecision decision) {
        // Mirrors the server-side BuildHookResponse. Claude expects camelCase keys here
        // (hookSpecificOutput, hookEventName, applyPermissions, updatedInput) — these are
        // outside the server's snake_case JSON convention because Claude defines them.
        var decisionNode = new JsonObject { ["behavior"] = decision.Behavior };

        if (decision.ApplyPermissions is { } ap) decisionNode["applyPermissions"] = JsonNode.Parse(ap.GetRawText());
        if (decision.UpdatedInput is { } ui) decisionNode["updatedInput"]         = JsonNode.Parse(ui.GetRawText());

        var payload = new JsonObject {
            ["hookSpecificOutput"] = new JsonObject {
                ["hookEventName"] = "PermissionRequest",
                ["decision"]      = decisionNode
            }
        };

        return payload.ToJsonString();
    }

    static string BuildCodexResponse(PermissionDecision decision) {
        // Codex only consumes behavior — strip applyPermissions and updatedInput so the
        // response stays valid for Codex's stricter hook schema.
        var payload = new JsonObject {
            ["hookSpecificOutput"] = new JsonObject {
                ["hookEventName"] = "PermissionRequest",
                ["decision"]      = new JsonObject { ["behavior"] = decision.Behavior }
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

    [LoggerMessage(Level = LogLevel.Debug, Message = "Auto-approved review-flow result submission for session {SessionId} (vendor={Vendor}) without surfacing a prompt")]
    static partial void LogFlowResultAutoApproved(ILogger logger, string sessionId, string vendor);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Denied out-of-allowlist tool {ToolName} for unattended reviewer session {SessionId}")]
    static partial void LogReviewerToolDenied(ILogger logger, string sessionId, string toolName);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Permission bridge handler error")]
    static partial void LogBridgeHandlerError(ILogger logger, Exception exception);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Permission bridge shutdown failed; continuing teardown")]
    static partial void LogDisposeFailed(ILogger logger, Exception exception);
}
