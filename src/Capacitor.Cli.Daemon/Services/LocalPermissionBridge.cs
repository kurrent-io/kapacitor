using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Capacitor.Cli.Core;
using Capacitor.Cli.Core.Harness.Claude;
using Capacitor.Cli.Core.LocalIpc;
using Capacitor.Cli.Core.Policy;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Capacitor.Cli.Daemon.Services;

internal readonly record struct PermissionAttribution(string? AgentId, string SessionId, string? Cwd);
internal readonly record struct AttributedAgent(string AgentId, PolicySnapshot? PolicySnapshot = null);

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
        ILogger<LocalPermissionBridge> logger,
        PermissionPromptBroker?        broker      = null,
        PermissionDecisionLog?         decisionLog = null
    ) : IHostedService, IAsyncDisposable {
    const int    MaxBindAttempts = 15;
    const string PathSuffix      = "/permission-request";

    internal static readonly TimeSpan ResponseWriteTimeout = TimeSpan.FromSeconds(2);
    internal static readonly TimeSpan RequestReadTimeout   = TimeSpan.FromSeconds(2);
    internal static readonly TimeSpan ShutdownDrain        = TimeSpan.FromSeconds(2);

    readonly PermissionPromptBroker _broker      = broker ?? new();
    readonly PermissionDecisionLog? _decisionLog = decisionLog;
    int _serverLegsInFlight;

    // One gate owns admission and the in-flight count together, so a snapshot taken under it
    // is exact: nothing admitted after it, nothing admitted before it invisible.
    readonly object _admission = new();
    bool _admitting = true;
    int  _inFlight;

    internal int  InFlightHandlersForTest => Volatile.Read(ref _inFlight);
    internal bool AdmittingForTest { get { lock (_admission) return _admitting; } }
    internal Func<Task>? BeforeHandlerRunsForTest { get; set; }

    internal PermissionPromptBroker BrokerForTest => _broker;
    internal PermissionDecisionLog? DecisionLogForTest => _decisionLog;

    /// Assigned by the orchestrator after construction (it takes this bridge in its own
    /// constructor, so the dependency cannot point the other way). Null = every request is
    /// unattributed and takes the server-only path.
    internal Func<PermissionAttribution, AttributedAgent?>? AttributeHandler { get; set; }

    internal int ServerLegsInFlightForTest => Volatile.Read(ref _serverLegsInFlight);

    // Revoked reviewer prefixes are intentionally retained (see RevokeReviewerToken), so the
    // listener's prefix set only grows over a daemon's lifetime, bounded by the reviewer-launch
    // count. Warn once each time the count crosses a multiple of this step so runaway growth in a
    // very long-lived daemon is diagnosable rather than silent.
    const int    ReviewerPrefixHighWaterStep = 1024;

    /// <summary>Cap on a reviewer submission body. The poster is a sandboxed vendor child, so an
    /// unbounded read is a memory-exhaustion lever.</summary>
    internal const int MaxSubmitBodyBytes = 1024 * 1024;

    /// <summary>Cap on a permission-request body posted by the local hook. Tool input and
    /// suggestions are each already bounded to <see cref="PermissionWire.MaxElementBytes"/> on the
    /// wire, so a well-formed request never approaches this; the extra bytes cover the surrounding
    /// envelope (session_id, tool_name, agent_id, cwd).</summary>
    internal const int MaxPermissionRequestBodyBytes = PermissionWire.MaxElementBytes * 2 + 4096;

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
                _admitting   = true;
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

        // Closing the listener before the drain would abort the very responses the claims promised.
        lock (_admission) _admitting = false;
        var drainDeadline = DateTime.UtcNow + ShutdownDrain;
        while (Volatile.Read(ref _inFlight) > 0 && DateTime.UtcNow < drainDeadline)
            await Task.Delay(10, CancellationToken.None);

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
            // Relays a borrowed reviewer's submission under the DAEMON's credential; null for every
            // other reviewer, which authenticates for itself. On the GRANT so it is revoked with the
            // token — a submit path outliving its reviewer could report into an already-reaped flow.
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

            // Prefixes are never removed on revoke, so this count only rises — surface a warning at
            // each high-water step so a leak is diagnosable (the count grows by one per launch).
            var prefixCount = _listener.Prefixes.Count;
            if (prefixCount % ReviewerPrefixHighWaterStep == 0)
                LogReviewerPrefixHighWater(logger, prefixCount);

            return $"http://127.0.0.1:{_port}/{token}";
        }
    }

    /// <summary>Revoke a reviewer token (accepts the URL from <see cref="RegisterReviewerToken"/> or
    /// the bare token). Idempotent. After revocation, requests on that token 404 (fail-safe).</summary>
    public void RevokeReviewerToken(string reviewerBridgeUrlOrToken) {
        var token = ExtractToken(reviewerBridgeUrlOrToken);
        if (token is null) return;

        // Removing the token from the dictionary is the ONE authoritative revocation: HandleAsync
        // re-validates every request against _reviewerTokens (a stray prefix "can't quietly admit
        // anything"), so a dict miss is a deterministic 404. We deliberately do NOT remove the
        // HttpListener prefix. On the managed (Linux/macOS) HttpListener, a request on a keep-alive
        // connection to a just-removed prefix no longer routes to our handler and instead yields a
        // transport-level artifact — a spurious empty-body 200 or a connection reset — rather than
        // the clean 404 our code would return. Keeping the prefix registered means the request still
        // reaches HandleAsync, where the dict miss produces the intended 404. The prefixes are freed
        // when the listener closes; their count is bounded by the daemon's reviewer-launch count.
        _reviewerTokens.TryRemove(token, out _);
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

            bool admitted;
            lock (_admission) {
                admitted = _admitting;
                if (admitted) _inFlight++;
            }
            if (!admitted) {
                try { context.Response.StatusCode = 503; context.Response.Close(); } catch { /* peer gone */ }
                continue;
            }
            // CancellationToken.None on the Task.Run scheduling token, deliberately: a delegate
            // cancelled before it starts never runs its finally, and the count would never reach zero.
            _ = Task.Run(() => RunTrackedAsync(context, ct), CancellationToken.None);
        }
    }

    async Task RunTrackedAsync(HttpListenerContext context, CancellationToken ct) {
        try {
            if (BeforeHandlerRunsForTest is { } hold) await hold();
            await HandleAsync(context, ct);
        } finally {
            lock (_admission) _inFlight--;
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

            // Borrowed-reviewer result delivery. Routed like review-context above: exact method/path,
            // live grant only. A grant minted without a forwarder 404s rather than exposing an
            // endpoint that could only fail.
            if (context.Request.HttpMethod == "POST" && rawUrl is not null) {
                var trimmedRaw = rawUrl.TrimStart('/');
                var slash      = trimmedRaw.IndexOf('/');
                if (slash > 0) {
                    var submitToken = trimmedRaw[..slash];
                    // Upstream path chosen from a fixed table, never echoed from the request: the
                    // caller is sandboxed, and echoing would make this an open authenticated relay.
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

                        // A reviewer mid-delivery is alive; reaping it here would discard the result.
                        submitGrant.ActivityClock?.Advance();

                        var submitBody = await ReadCappedBodyAsync(context.Request.InputStream, MaxSubmitBodyBytes, ct);
                        if (submitBody is null) {
                            context.Response.StatusCode = 413;
                            context.Response.Close();

                            return;
                        }

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

            // Not bound to ct: a handler admitted before shutdown must still reach its own claim
            // below, so this reads under its own bounded RequestReadTimeout instead. Capped at
            // MaxPermissionRequestBodyBytes so an unbounded read from the local hook can't exhaust
            // the daemon's memory.
            using var readCts = new CancellationTokenSource(RequestReadTimeout);
            var       body    = await ReadCappedBodyAsync(context.Request.InputStream, MaxPermissionRequestBodyBytes, readCts.Token);

            if (body is null) {
                context.Response.StatusCode = 413;
                context.Response.Close();

                return;
            }

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

            // HttpListener exposes no per-request "client disconnected" token: every leg below is
            // bound to the daemon-lifetime token, not the connection.
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
                // Shared (interactive) token: the reserved channel's tool names are not special-cased
                // here — an interactive session never legitimately carries that server, so an
                // identically-named tool is untrusted and takes the normal prompt.
                var canonicalSessionId = PermissionWire.Canonical(sessionId);
                var attributed = canonicalSessionId is null ? null : AttributeHandler?.Invoke(new PermissionAttribution(
                    node["agent_id"]?.GetValue<string>(), canonicalSessionId, node["cwd"]?.GetValue<string>()));
                var pending = attributed is { } a
                    ? BuildPending(Guid.NewGuid().ToString("N"), a.AgentId, canonicalSessionId!, vendor, toolName, toolInput, suggestions,
                        DateTimeOffset.UtcNow.ToString("O"))
                    : null;

                // The launched agent's own policy answers before a human is asked. The vendor gate
                // is deliberate: a hosted Codex request parks unevaluated. Anything the evaluation
                // throws leaves the request on the human lane rather than dropping it.
                if (vendor is "claude" && attributed is { PolicySnapshot: { IsEmpty: false } snapshot } governed) {
                    CanonicalAction?  action = null;
                    PolicyEvaluation? evaluation = null;
                    try {
                        action     = ClaudeActionNormalizer.Normalize(toolName, toolInput, node["cwd"]?.GetValue<string>());
                        evaluation = PolicyEngine.Evaluate(snapshot, action, EvaluationMode.Full);
                    } catch (Exception ex) {
                        LogPolicyEvaluationFailed(logger, ex, governed.AgentId);
                    }

                    if (action is { } act && evaluation is { } eval) {
                        if (eval.Outcome is PolicyOutcome.Allow or PolicyOutcome.Deny) {
                            var behavior = eval.Outcome == PolicyOutcome.Allow
                                ? PermissionSettlements.Allow
                                : PermissionSettlements.Deny;
                            _decisionLog?.Record(new PermissionDecisionRecord(
                                DateTimeOffset.UtcNow.ToString("O"), governed.AgentId, canonicalSessionId!, vendor,
                                toolName ?? "", behavior, PermissionSettlements.SourcePolicy));
                            _ = server.AppendAgentRunEventAsync(governed.AgentId, PolicyDecisionEvent(
                                canonicalSessionId!, governed.AgentId, snapshot, act, eval, behavior, behavior));
                            // No Register: a call the policy answered raises no card, and the
                            // decision log plus the run event are its audit trail.
                            await WriteResponseAsync(context, BuildHookResponseJson(new PermissionDecision(behavior, null, null), vendor));

                            return;
                        }

                        if (eval.Outcome == PolicyOutcome.Ask)
                            _ = server.AppendAgentRunEventAsync(governed.AgentId, PolicyDecisionEvent(
                                canonicalSessionId!, governed.AgentId, snapshot, act, eval, "ask", "parked"));
                    }
                }

                if (pending is null) {
                    // Server-only path.
                    if (attributed is not null) LogPendingOutOfBounds(logger, sessionId);
                    try {
                        decision = await server.RequestPermissionAsync(sessionId, toolName, toolInput, suggestions, ct);
                    } catch (Exception ex) {
                        LogRequestPermissionFailed(logger, ex, sessionId);
                        decision = new PermissionDecision("deny", null, null);
                    }
                } else {
                    var settlementTask = _broker.Register(pending);
                    _ = RunServerLegAsync(pending, toolName, toolInput, suggestions, settlementTask, ct);

                    PermissionSettlement settlement;
                    try {
                        settlement = await settlementTask.WaitAsync(ct);
                    } catch (OperationCanceledException) {
                        // Shutdown: claim rather than inspect. Losing means another party settled first.
                        if (_broker.TrySettle(pending.RequestId, PermissionSettlements.DenyDecision,
                                PermissionSettlements.Deny, PermissionSettlements.SourceDaemonShutdown)) {
                            await WriteResponseAsync(context, BuildHookResponseJson(PermissionSettlements.DenyDecision, vendor));
                            return;
                        }
                        settlement = await settlementTask;
                    }

                    _decisionLog?.Record(new PermissionDecisionRecord(
                        DateTimeOffset.UtcNow.ToString("O"), pending.AgentId, pending.SessionId, pending.Vendor,
                        pending.ToolName, settlement.Outcome, settlement.Source));
                    await WriteResponseAsync(context, BuildHookResponseJson(settlement.Decision, vendor));
                    return;
                }
            }

            await WriteResponseAsync(context, BuildHookResponseJson(decision, vendor));
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

    /// One evaluation per raised prompt, so there is nothing to correlate a decision against and
    /// nothing ambiguous about which call it answers.
    static PolicyDecisionEventV1 PolicyDecisionEvent(
            string sessionId, string agentId, PolicySnapshot snapshot, CanonicalAction action,
            PolicyEvaluation evaluation, string requested, string effective) =>
        new(sessionId, agentId, "claude", PolicySeams.HostedClaudePermission, snapshot.Id, PolicyEngine.Version,
            "full", requested, effective, PolicyWire.ToWire(action), PolicyWire.ToWire(evaluation.MatchedRules),
            snapshot.Degraded, null, null, false, DateTimeOffset.UtcNow.ToString("O"));

    /// Written under a bounded token of its own, never the bridge token: shutdown cancels that
    /// token before the drain, and a claimed answer must still reach the hook. A failed write
    /// aborts the connection rather than leaving it open for the caller's Close() to fault on
    /// already-sent headers.
    static async Task WriteResponseAsync(HttpListenerContext context, string responseJson) {
        using var writeCts = new CancellationTokenSource(ResponseWriteTimeout);
        var bytes = Encoding.UTF8.GetBytes(responseJson);
        context.Response.ContentType     = "application/json";
        context.Response.StatusCode      = 200;
        context.Response.ContentLength64 = bytes.LongLength;
        try {
            await context.Response.OutputStream.WriteAsync(bytes, writeCts.Token);
            context.Response.Close();
        } catch {
            context.Response.Abort();
            throw;
        }
    }

    internal static PermissionPendingDto? BuildPending(
            string requestId, string agentId, string sessionId, string vendor, string? toolName,
            JsonElement? toolInput, JsonElement? suggestions, string requestedAt) {
        var name = toolName ?? "";
        if (Encoding.UTF8.GetByteCount(name) > PermissionWire.MaxToolNameBytes) return null;
        if (Encoding.UTF8.GetByteCount(agentId) > PermissionWire.MaxAgentIdBytes) return null;
        var (input, inputOmitted)   = Bound(toolInput);
        var (sugg,  suggOmitted)    = Bound(suggestions);
        return new PermissionPendingDto(requestId, agentId, sessionId, vendor, name, input, sugg, inputOmitted, suggOmitted, requestedAt);

        static (JsonElement?, bool) Bound(JsonElement? el) =>
            el is { } e && Encoding.UTF8.GetByteCount(e.GetRawText()) > PermissionWire.MaxElementBytes ? (null, true) : (el, false);
    }

    /// Everything that touches the server for one request. Total: every exit returns normally
    /// and the bridge never awaits it. The settlement continuation only WAKES a wait — the
    /// broker's TCS runs continuations asynchronously — while the `abandoned` predicate, read
    /// synchronously before the hub invoke, is what keeps a settled request off the wire.
    async Task RunServerLegAsync(
            PermissionPendingDto pending, string? toolName, JsonElement? toolInput, JsonElement? suggestions,
            Task<PermissionSettlement> settlement, CancellationToken daemonToken) {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(daemonToken);
        _ = settlement.ContinueWith(_ => { try { cts.Cancel(); } catch (ObjectDisposedException) { } }, TaskScheduler.Default);
        try {
            Interlocked.Increment(ref _serverLegsInFlight);
            string serverRequestId;
            try {
                serverRequestId = await server.BeginPermissionRequestAsync(
                    pending.SessionId, toolName, toolInput, suggestions, cts.Token, () => settlement.IsCompleted);
            } catch (OperationCanceledException) {
                return; // shutdown, or the settlement woke the readiness wait: no server request exists
            } catch (PermissionRequestAbandonedException) {
                return;
            } catch (Exception ex) {
                LogServerLegBeginFailed(logger, ex, pending.RequestId);
                _broker.TrySettleIfNoSubscriber(pending.RequestId, PermissionSettlements.DenyDecision,
                    PermissionSettlements.Deny, PermissionSettlements.SourceNoUi);
                return;
            }

            if (settlement.IsCompleted) {
                await RelaySettlementAsync(pending, serverRequestId, settlement.Result);
                return;
            }

            PermissionDecision decision;
            try {
                decision = await server.AwaitPermissionDecisionAsync(serverRequestId, cts.Token);
            } catch (OperationCanceledException) {
                if (daemonToken.IsCancellationRequested) return;
                await RelaySettlementAsync(pending, serverRequestId, await settlement);
                return;
            }

            if (!_broker.TrySettle(pending.RequestId, decision, decision.Behavior, PermissionSettlements.SourceServer))
                LogServerDecisionArrivedLate(logger, pending.RequestId, (await settlement).Decision.Behavior);
        } catch (Exception ex) {
            LogServerLegFaulted(logger, ex, pending.RequestId);
        } finally {
            Interlocked.Decrement(ref _serverLegsInFlight);
        }
    }

    async Task RelaySettlementAsync(PermissionPendingDto pending, string serverRequestId, PermissionSettlement settlement) {
        if (settlement.Source == PermissionSettlements.SourceServer) return;
        var outcome = await server.RespondToPermissionAsync(pending.SessionId, serverRequestId, settlement.Decision);
        switch (outcome.Kind) {
            case ServerConnection.RespondOutcomeKind.NotPending:
                LogServerNoLongerHeld(logger, pending.RequestId, settlement.Decision.Behavior);
                break;
            case ServerConnection.RespondOutcomeKind.Failed:
                LogRespondFailed(logger, pending.RequestId, outcome.Reason ?? "");
                break;
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

    /// <summary>Reads at most <paramref name="maxBytes"/>, returning null when the body exceeds
    /// it. Bounded on BYTES ACTUALLY READ rather than Content-Length, which a chunked or hostile
    /// client need not send truthfully — checking the header alone would be a guard the attacker
    /// controls. One byte past the cap is enough to reject, so an oversized body is never fully
    /// buffered.</summary>
    static async Task<string?> ReadCappedBodyAsync(Stream input, int maxBytes, CancellationToken ct) {
        var buffer = new byte[8192];
        using var accumulated = new MemoryStream();

        while (true) {
            var read = await input.ReadAsync(buffer, ct);
            if (read == 0) break;

            if (accumulated.Length + read > maxBytes) return null;

            accumulated.Write(buffer, 0, read);
        }

        return Encoding.UTF8.GetString(accumulated.GetBuffer(), 0, (int)accumulated.Length);
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

    [LoggerMessage(Level = LogLevel.Warning, Message = "Policy evaluation failed for agent {AgentId}; the permission request takes the human lane")]
    static partial void LogPolicyEvaluationFailed(ILogger logger, Exception exception, string agentId);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Permission bridge handler error")]
    static partial void LogBridgeHandlerError(ILogger logger, Exception exception);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Permission bridge shutdown failed; continuing teardown")]
    static partial void LogDisposeFailed(ILogger logger, Exception exception);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Local permission bridge has {PrefixCount} listener prefixes; revoked reviewer prefixes are retained until the daemon stops")]
    static partial void LogReviewerPrefixHighWater(ILogger logger, int prefixCount);

    [LoggerMessage(Level = LogLevel.Debug, Message = "Permission request for session {SessionId} was attributed but exceeds the pending bounds; server-only path")]
    static partial void LogPendingOutOfBounds(ILogger logger, string sessionId);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Server leg for permission request {RequestId} could not begin")]
    static partial void LogServerLegBeginFailed(ILogger logger, Exception exception, string requestId);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Server leg for permission request {RequestId} faulted")]
    static partial void LogServerLegFaulted(ILogger logger, Exception exception, string requestId);

    [LoggerMessage(Level = LogLevel.Information, Message = "Server decision for permission request {RequestId} arrived after it was settled locally; the hook received {Behavior}")]
    static partial void LogServerDecisionArrivedLate(ILogger logger, string requestId, string behavior);

    [LoggerMessage(Level = LogLevel.Information, Message = "The server no longer held permission request {RequestId} when the local decision was relayed; the hook received {Behavior}")]
    static partial void LogServerNoLongerHeld(ILogger logger, string requestId, string behavior);

    [LoggerMessage(Level = LogLevel.Information, Message = "Relaying the local decision for permission request {RequestId} to the server failed: {Reason}")]
    static partial void LogRespondFailed(ILogger logger, string requestId, string reason);
}
