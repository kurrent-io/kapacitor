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
    readonly ConcurrentDictionary<string, ReviewerGrant> _reviewerTokens = new(StringComparer.Ordinal);
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
        // Read the field ONCE. DisposeAsync exchanges it to null BEFORE disposing, so a plain read
        // is enough: reference reads are atomic, and the only bad outcome — capturing a non-null
        // reference that is disposed a moment later — is handled by the catch below. (An
        // interlocked read would add nothing here.) Cancelling an already-disposed CTS throws, and
        // this runs on the host's dispose path where a throw is fatal, so treat it as an
        // already-stopped bridge rather than an error.
        var cts = _cts;
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
            // Deliberately broad. This is a teardown boundary: DI stops walking its remaining
            // disposables the moment an exception escapes, so letting anything through here would
            // strand other services' cleanup as well as killing the process. Logged rather than
            // swallowed silently, so a real teardown failure is still diagnosable.
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
    public string RegisterReviewerToken(
            IReadOnlyList<string> allowlistServers,
            BorrowedReviewContextGeneration? reviewContext = null,
            // The launch's activity clock, so a tool-call hit on this token advances it. Optional and
            // trailing so pre-existing call sites keep compiling; production always supplies one.
            AgentActivityClock? activityClock = null,
            // Forwards a borrowed reviewer's result submission to the server using the DAEMON's
            // credential. Supplied only for a borrowed snapshot, whose sandbox redirects HOME and so
            // leaves the result channel with no token store of its own; every other reviewer keeps
            // authenticating for itself and passes null, which withholds the capability entirely
            // rather than exposing an endpoint that would 500.
            //
            // It hangs off the GRANT rather than the bridge so it is revoked in the same operation
            // that revokes the token — a submit path outliving its reviewer would let a lingering
            // child report into a flow whose participant the server has already reaped.
            Func<string, string, CancellationToken, Task<(int Status, string Body)>>? submitForwarder = null) {
        if (_listener is null || _sharedToken is null)
            throw new InvalidOperationException("LocalPermissionBridge not started");

        lock (_prefixLock) {
            var token = NewToken();
            while (string.Equals(token, _sharedToken, StringComparison.Ordinal) || _reviewerTokens.ContainsKey(token))
                token = NewToken();   // CSPRNG collisions are negligible; never silently reuse one

            _listener.Prefixes.Add($"http://127.0.0.1:{_port}/{token}/");
            _reviewerTokens[token] = new ReviewerGrant(
                [.. allowlistServers], reviewContext, activityClock, submitForwarder);

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

    /// <summary>Atomically publishes a completed immutable sidecar generation for a live reviewer.
    /// Returns the retired generation so its on-disk storage can be removed after the swap.</summary>
    public BorrowedReviewContextGeneration? PublishReviewerContext(
            string reviewerBridgeUrlOrToken,
            BorrowedReviewContextGeneration generation) {
        var token = ExtractToken(reviewerBridgeUrlOrToken)
            ?? throw new InvalidOperationException("reviewer_context_token_invalid");
        while (_reviewerTokens.TryGetValue(token, out var current)) {
            var replacement = current with { ReviewContext = generation };
            if (_reviewerTokens.TryUpdate(token, replacement, current))
                return current.ReviewContext;
        }
        throw new InvalidOperationException("reviewer_context_token_revoked");
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
            // This capability is deliberately routed before the permission-request parser. It has
            // one exact method/path, accepts no query or caller-selected path, and exists only on a
            // live reviewer grant. The shared interactive token is absent from this dictionary.
            var rawUrl = context.Request.RawUrl;
            if (context.Request.HttpMethod == "GET" && rawUrl is not null) {
                var trimmedRaw = rawUrl.TrimStart('/');
                var slash = trimmedRaw.IndexOf('/');
                if (slash > 0) {
                    var contextToken = trimmedRaw[..slash];
                    var expected = $"/{contextToken}/review-context/workspace-mcp-configs";
                    if (rawUrl.Equals(expected, StringComparison.Ordinal) &&
                        _reviewerTokens.TryGetValue(contextToken, out var contextGrant) &&
                        contextGrant.ReviewContext is { } generation) {
                        context.Response.ContentType = "application/json";
                        context.Response.StatusCode = 200;
                        context.Response.ContentLength64 = generation.JsonUtf8.LongLength;
                        await context.Response.OutputStream.WriteAsync(generation.JsonUtf8, ct);
                        context.Response.Close();
                        return;
                    }
                }
                context.Response.StatusCode = 404;
                context.Response.Close();
                return;
            }

            // The borrowed reviewer's result-delivery capability, routed before the permission-request
            // parser for the same reasons as review-context above: one exact method/path, no query or
            // caller-selected path segment, and reachable only on a live grant that was minted WITH a
            // forwarder. A grant without one (every non-borrowed reviewer, which authenticates for
            // itself) falls through to 404 rather than exposing an endpoint that could only fail.
            if (context.Request.HttpMethod == "POST" && rawUrl is not null) {
                var trimmedRaw = rawUrl.TrimStart('/');
                var slash      = trimmedRaw.IndexOf('/');
                if (slash > 0) {
                    var submitToken = trimmedRaw[..slash];
                    // The API path is chosen HERE, from a fixed table, never echoed from the request.
                    // The caller is a sandboxed vendor child; letting it name the upstream path would
                    // turn an authenticated daemon proxy into an open relay onto the kcap API.
                    var apiPath = rawUrl switch {
                        _ when rawUrl.Equals($"/{submitToken}/flow-result", StringComparison.Ordinal)
                            => "/api/flows/reviewer/result",
                        _ when rawUrl.Equals($"/{submitToken}/flow-message", StringComparison.Ordinal)
                            => "/api/flows/participant/message",
                        _   => null
                    };
                    if (apiPath is not null) {
                        if (!_reviewerTokens.TryGetValue(submitToken, out var submitGrant) ||
                            submitGrant.SubmitForwarder is not { } forward) {
                            context.Response.StatusCode = 404;
                            context.Response.Close();

                            return;
                        }

                        // The clock advances on a submit for the same reason it advances on a tool
                        // call: a reviewer mid-delivery is demonstrably alive, and letting the wedge
                        // detector reap it here would discard the very result it spent the round
                        // producing.
                        submitGrant.ActivityClock?.Advance();

                        string submitBody;
                        using (var submitReader = new StreamReader(
                                context.Request.InputStream, Encoding.UTF8, leaveOpen: false))
                            submitBody = await submitReader.ReadToEndAsync(ct);

                        var (status, responseBody) = await forward(apiPath, submitBody, ct);
                        var payload = Encoding.UTF8.GetBytes(responseBody);
                        context.Response.ContentType     = "application/json";
                        context.Response.StatusCode      = status;
                        context.Response.ContentLength64 = payload.LongLength;
                        await context.Response.OutputStream.WriteAsync(payload, ct);
                        context.Response.Close();

                        return;
                    }
                }
            }

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
            var isReviewer = _reviewerTokens.TryGetValue(token, out var reviewerGrant);

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

            if (isReviewer) {
                // Advance BEFORE the tool-name check and any allow/deny decision below: a malformed
                // request from a live reviewer is still evidence the process is alive.
                reviewerGrant!.ActivityClock?.Advance();

                // Unattended participant: a well-formed tool name is required to classify.
                if (string.IsNullOrWhiteSpace(toolName)) {
                    context.Response.StatusCode = 400;
                    context.Response.Close();

                    return;
                }

                if (IsReservedChannelTool(toolName)) {
                    // The reserved result channel (kcap-flow-result) is injected only for flow
                    // participants, and every tool it advertises is in the contract-tested
                    // unattended-safe set — each only POSTs to the participant's own flow run,
                    // authorized server-side against the caller's active agent assignment.
                    // Auto-approve without a server round-trip: an unattended participant can't
                    // get a user decision otherwise.
                    LogReservedChannelToolAutoApproved(logger, toolName, sessionId, vendor);
                    decision = new PermissionDecision("allow", null, null);
                } else if (IsReviewerToolAllowed(
                               vendor, toolName, reviewerGrant!.AllowlistServers)) {
                    // Auto-approve its bound kcap tools; DENY an out-of-allowlist (or
                    // non-config-locked-vendor bare) call outright rather than defer to a prompt
                    // no human can answer.
                    decision = new PermissionDecision("allow", null, null);
                } else {
                    LogReviewerToolDenied(logger, sessionId, toolName);
                    decision = new PermissionDecision("deny", null, null);
                }
            } else {
                // Shared (interactive) token → the server permission path, unchanged. This
                // deliberately includes the reserved channel's own tool names: an interactive
                // session never legitimately carries that server, so an identically-named tool
                // here is untrusted and takes the normal prompt.
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
    /// True when the permission request names one of the reserved result channel's unattended-safe
    /// tools. The match parses the canonical <c>mcp__&lt;server&gt;__&lt;tool&gt;</c> shape and
    /// compares whole segments (hyphens normalized to underscores) — substring matching here is
    /// spoofable, e.g. <c>mcp__evil_kcap_flow_result__send_flow_message</c>. Bare names are exact
    /// set membership. Callers gate this on the reviewer token, so an interactive session's
    /// identically-named tool still takes the normal prompt path.
    /// </summary>
    static bool IsReservedChannelTool(string? toolName) {
        if (string.IsNullOrEmpty(toolName)) return false;

        const string prefix = "mcp__";

        // Bare tool name, no server prefix: exact safe-set membership only.
        if (!toolName.StartsWith(prefix, StringComparison.Ordinal))
            return KcapMcpRegistry.ReservedResultChannelUnattendedSafeTools.Contains(toolName);

        var afterPrefix = toolName[prefix.Length..];
        var sep         = afterPrefix.IndexOf("__", StringComparison.Ordinal);

        if (sep <= 0) return false;   // malformed qualified name → not the reserved channel (fail-safe)

        var server = afterPrefix[..sep].Replace('-', '_');
        var tool   = afterPrefix[(sep + 2)..];

        return string.Equals(server, ReservedChannelServerSegment, StringComparison.Ordinal)
            && KcapMcpRegistry.ReservedResultChannelUnattendedSafeTools.Contains(tool);
    }

    /// <summary>The reserved channel id in its underscore normalization (<c>kcap_flow_result</c>) —
    /// the form a hyphenated or Claude-sanitized server segment reduces to for the exact-equality
    /// comparison above.</summary>
    static readonly string ReservedChannelServerSegment =
        KcapMcpRegistry.ReservedResultChannelId.Replace('-', '_');

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

    sealed record ReviewerGrant(
        string[] AllowlistServers,
        BorrowedReviewContextGeneration? ReviewContext,
        AgentActivityClock? ActivityClock = null,
        Func<string, string, CancellationToken, Task<(int Status, string Body)>>? SubmitForwarder = null);

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

    [LoggerMessage(Level = LogLevel.Debug, Message = "Auto-approved reserved flow-channel tool {ToolName} for unattended participant session {SessionId} (vendor={Vendor}) without surfacing a prompt")]
    static partial void LogReservedChannelToolAutoApproved(ILogger logger, string toolName, string sessionId, string vendor);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Denied out-of-allowlist tool {ToolName} for unattended participant session {SessionId}")]
    static partial void LogReviewerToolDenied(ILogger logger, string sessionId, string toolName);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Permission bridge handler error")]
    static partial void LogBridgeHandlerError(ILogger logger, Exception exception);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Permission bridge shutdown failed; continuing teardown")]
    static partial void LogDisposeFailed(ILogger logger, Exception exception);
}
