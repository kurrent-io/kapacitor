using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Nodes;
using Capacitor.Cli.Core.Acp;
using Capacitor.Cli.Daemon.Acp;
using Capacitor.Cli.Daemon.Services;
using Microsoft.Extensions.Logging;

namespace Capacitor.Cli.Daemon.Harness.Codex;

/// <summary>
/// Spawns one <c>codex app-server</c> child (optionally with a hook-trust seed override appended to
/// its argv) and returns the JSON-RPC connection over its stdio plus the process handle. The runtime
/// calls this once for the initial launch and, at most once more, to re-spawn with a
/// <c>hooks.state</c> seed when the hook-trust preflight requires it. Production wires it to a real
/// <c>Process.Start</c>; tests wire it to an in-process fake app-server over pipes.
/// </summary>
internal delegate Task<(CodexAppServerConnection Connection, IAcpProcess Process)> CodexAppServerSpawn(
    string? hookStateSeed, CancellationToken ct);

/// <summary>Everything the runtime needs to drive one hosted reviewer's app-server session, resolved
/// by the factory before the first spawn.</summary>
/// <param name="Sandbox">The resolved <see cref="CodexPosturePolicy"/> sandbox token
/// (<c>read-only</c> / <c>workspace-write</c> / <c>danger-full-access</c>), rendered per turn via
/// <see cref="CodexAppServerPosture.RenderSandboxPolicy"/>.</param>
/// <param name="Approval">The resolved approval token — always <c>never</c> for a reviewer.</param>
/// <param name="Effort">The requested reasoning effort, or null for the vendor default; passed on
/// every <c>turn/start</c> (the app-server carries effort as a per-turn protocol param, not argv).</param>
/// <param name="WritableRoots">Writable roots for <c>workspace-write</c> (the owned worktree);
/// ignored for the other sandboxes.</param>
/// <param name="ClientVersion">Daemon version stamped into <c>initialize.clientInfo.version</c>.</param>
internal sealed record CodexAppServerLaunch(
    string                Cwd,
    string?               Model,
    string?               Effort,
    string?               InitialPrompt,
    string                Sandbox,
    string                Approval,
    IReadOnlyList<string> WritableRoots,
    string                ClientVersion);

/// <summary>
/// A <see cref="IHostedAgentRuntime"/> that hosts a Codex reviewer over the <c>codex app-server</c>
/// JSON-RPC protocol instead of the interactive PTY. It is control-plane only: the transcript is
/// still recorded through the Codex hooks + <c>kcap watch</c> rollout path (which is why the
/// hook-trust preflight is load-bearing), so this runtime never aggregates transcript from the
/// protocol stream and <see cref="EmitsTerminalOutput"/> is <see langword="false"/>.
///
/// <para>Lifecycle (<see cref="StartAsync"/>, called by the factory): spawn → <c>initialize</c> →
/// <c>hooks/list</c> trust preflight (seed + one restart when required) → <c>thread/start</c> →
/// optional first <c>turn/start</c>. Each review round is a <c>turn/start</c> on the held thread;
/// <see cref="WaitForTurnIdleAsync"/> resolves from the matching <c>turn/completed</c> notification.
/// The reaper is fed through <see cref="AgentActivityClock"/> exactly as the ACP runtime feeds it.</para>
///
/// <para>Because <c>approvalPolicy</c> is pinned to <c>never</c>, any server-initiated approval
/// request is a protocol violation — answered with a valid on-the-wire <c>decline</c> (never a
/// JSON-RPC error, whose turn/process effect is Codex's to define) and logged at Error.</para>
/// </summary>
internal sealed partial class CodexAppServerHostedAgentRuntime : IHostedAgentRuntime {
    // Reviewers need lifecycle + usage only; opting out of the high-volume delta streams is a
    // performance choice (the reader always drains regardless — the app-server blocks rather than
    // dropping), not a correctness one.
    static readonly string[] DeltaOptOut = [
        "item/agentMessage/delta",
        "item/reasoning/textDelta",
        "item/reasoning/summaryTextDelta",
        "item/commandExecution/outputDelta",
        "item/fileChange/outputDelta",
    ];

    const string ClientName = "kcap-daemon";
    const int    BackpressureMaxAttempts = 6;

    readonly CodexAppServerSpawn  _spawn;
    readonly CodexAppServerLaunch _launch;
    readonly AgentActivityClock?  _clock;
    readonly ILogger              _logger;
    readonly TimeProvider         _time;
    readonly CancellationTokenSource _cts = new();

    readonly object              _turnGate = new();
    // Whole-runtime terminal signal: completed exactly once, when the LIVE child's read loop ends
    // (process death) or on dispose — never for the controlled child teardown during a hook-trust
    // restart. The orchestrator treats ReadOutputAsync ending as the finalize trigger, so this must
    // not fire early, and WaitForTurnIdleAsync must unblock on it so a round never outlives the child.
    readonly TaskCompletionSource _runtimeTerminal = new(TaskCreationOptions.RunContinuationsAsynchronously);

    CodexAppServerConnection? _connection;
    IAcpProcess?              _process;
    Task                      _runLoop = Task.CompletedTask;
    CancellationTokenSource?  _childCts;   // per-child read-loop token, so a teardown unblocks the loop
    volatile bool             _restarting; // true only across the hook-trust seed-and-restart window

    string?                   _threadId;
    string?                   _resolvedModel;
    string?                   _currentTurnId;      // server turn id once the start response lands; null in the request window
    TaskCompletionSource?     _turnCompleted;      // the current round's waiter
    (string TurnId, string? Status)? _pendingCompletion; // an early turn/completed whose id we could not match yet
    CodexTokenUsage?          _usage;
    int                       _disposed;

    public CodexAppServerHostedAgentRuntime(
            CodexAppServerSpawn spawn, CodexAppServerLaunch launch, AgentActivityClock? clock,
            ILogger logger, TimeProvider? timeProvider = null) {
        _spawn   = spawn;
        _launch  = launch;
        _clock   = clock;
        _logger  = logger;
        _time    = timeProvider ?? TimeProvider.System;
    }

    // ── IHostedAgentRuntime: identity / lifecycle observables ──────────────────────────────────

    public string Vendor              => "codex";
    public int    Pid                 => _process?.Pid ?? 0;
    public bool   HasExited           => _process?.HasExited ?? false;
    public int?   ExitCode            => _process?.ExitCode;
    public bool   EmitsTerminalOutput => false;

    /// <summary>Resolved model from the <c>thread/start</c> response (never the requested one);
    /// null until the handshake completes. Feeds the existing launch-attempt reporting.</summary>
    public string? ResolvedModel => _resolvedModel;

    /// <summary>Daemon-held thread id — the deterministic session-id correlation that replaces the
    /// <c>CodexSessionRolloutLocator</c> timestamp race.</summary>
    public string? ThreadId => _threadId;

    /// <summary>Latest cumulative token usage reported over <c>thread/tokenUsage/updated</c>, or null
    /// if none has arrived yet.</summary>
    public CodexTokenUsage? Usage => _usage;

    // ── Startup (called by the factory, not part of the interface) ─────────────────────────────

    /// <summary>
    /// Runs the full launch sequence. Throws <see cref="CodexHooksNotInstalledException"/> when a
    /// critical hook is missing (the orchestrator maps it to a LaunchFailed with worktree cleanup);
    /// any other failure between spawn and the thread being established likewise propagates as a
    /// launch failure. The factory disposes this runtime on any such failure, so a spawned child is
    /// never leaked.
    /// </summary>
    public async Task StartAsync(CancellationToken ct) {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, _cts.Token);

        await SpawnAndInitializeAsync(hookStateSeed: null, linked.Token).ConfigureAwait(false);

        var decision = CodexHookTrust.Classify(await ListHooksAsync(linked.Token).ConfigureAwait(false));
        switch (decision) {
            case CodexHookTrustDecision.Proceed:
                break;

            case CodexHookTrustDecision.SeedAndRestart seed:
                _logger.LogInformation("codex app-server: seeding hook trust and restarting the child.");
                // The teardown of child 1 is CONTROLLED — its read-loop end must not trip the
                // whole-runtime terminal signal or fault a (nonexistent) turn, so gate that window.
                _restarting = true;
                try {
                    await TeardownChildAsync().ConfigureAwait(false);
                    await SpawnAndInitializeAsync(seed.StateOverride, linked.Token).ConfigureAwait(false);
                } finally {
                    _restarting = false;
                }

                if (CodexHookTrust.Classify(await ListHooksAsync(linked.Token).ConfigureAwait(false))
                        is not CodexHookTrustDecision.Proceed)
                    throw new InvalidOperationException(
                        "codex app-server: hook-trust seeding did not take effect after restart.");
                break;

            case CodexHookTrustDecision.MissingRequiredHooks missing:
                throw new CodexHooksNotInstalledException(
                    $"codex app-server: required hooks missing for events [{string.Join(", ", missing.MissingEvents)}].");

            case CodexHookTrustDecision.Unseedable unseedable:
                throw new InvalidOperationException(
                    $"codex app-server: untrusted kcap hooks cannot be seeded (no current hash): [{string.Join(", ", unseedable.Keys)}].");
        }

        await StartThreadAsync(linked.Token).ConfigureAwait(false);
        _clock?.ClearLaunchStage();

        if (!string.IsNullOrEmpty(_launch.InitialPrompt))
            await StartTurnAsync(_launch.InitialPrompt, linked.Token).ConfigureAwait(false);
    }

    async Task SpawnAndInitializeAsync(string? hookStateSeed, CancellationToken ct) {
        var (connection, process) = await _spawn(hookStateSeed, ct).ConfigureAwait(false);
        _connection = connection;
        _process    = process;

        connection.OnNotification  += HandleNotification;
        connection.OnServerRequest  = DeclineServerRequestAsync;

        _childCts = CancellationTokenSource.CreateLinkedTokenSource(_cts.Token);
        _runLoop  = RunConnectionAsync(connection, _childCts.Token);
        _clock?.SetLaunchStage("spawned");

        var initParams = new JsonObject {
            ["clientInfo"]   = new JsonObject { ["name"] = ClientName, ["version"] = _launch.ClientVersion },
            ["capabilities"] = new JsonObject {
                ["optOutNotificationMethods"] = new JsonArray(DeltaOptOut.Select(m => (JsonNode?) m).ToArray()),
            },
        };
        await RequestAsync("initialize", initParams, ct).ConfigureAwait(false);
        _clock?.SetLaunchStage("initialized");
    }

    async Task RunConnectionAsync(CodexAppServerConnection connection, CancellationToken ct) {
        try {
            await connection.RunAsync(ct).ConfigureAwait(false);
        } finally {
            // A controlled teardown for the hook-trust restart is NOT terminal: only the live child's
            // read loop ending (death) or dispose flips the whole-runtime terminal signal.
            if (!_restarting) {
                _runtimeTerminal.TrySetResult();
                FaultPendingTurn(new ObjectDisposedException(nameof(CodexAppServerHostedAgentRuntime),
                    "codex app-server connection ended with a turn still in flight."));
            }
        }
    }

    async Task<IReadOnlyList<CodexHookEntry>> ListHooksAsync(CancellationToken ct) {
        var listParams = new JsonObject { ["cwds"] = new JsonArray((JsonNode?) _launch.Cwd) };
        var result     = await RequestAsync("hooks/list", listParams, ct).ConfigureAwait(false);

        var entries = new List<CodexHookEntry>();
        if (result.TryGetProperty("data", out var data) && data.ValueKind == JsonValueKind.Array) {
            foreach (var group in data.EnumerateArray()) {
                if (!group.TryGetProperty("hooks", out var hooks) || hooks.ValueKind != JsonValueKind.Array)
                    continue;

                foreach (var h in hooks.EnumerateArray()) {
                    entries.Add(new CodexHookEntry(
                        Key:         Str(h, "key") ?? "",
                        EventName:   Str(h, "eventName") ?? "",
                        Command:     Str(h, "command") ?? "",
                        CurrentHash: Str(h, "currentHash"),
                        TrustStatus: Str(h, "trustStatus") ?? ""));
                }
            }
        }
        return entries;
    }

    async Task StartThreadAsync(CancellationToken ct) {
        var startParams = new JsonObject {
            ["cwd"]               = _launch.Cwd,
            // thread/start.sandbox is the coarse SandboxMode STRING (read-only / workspace-write /
            // danger-full-access) — a different wire shape from turn/start.sandboxPolicy's object.
            // The resolved posture token already IS that string; the per-turn sandboxPolicy object
            // is the load-bearing containment.
            ["sandbox"]           = CodexAppServerPosture.RenderSandboxMode(_launch.Sandbox),
            ["approvalPolicy"]    = CodexAppServerPosture.RenderApprovalPolicy(_launch.Approval),
            ["approvalsReviewer"] = CodexAppServerPosture.ApprovalsReviewer,
        };
        if (!string.IsNullOrEmpty(_launch.Model))
            startParams["model"] = _launch.Model;

        var result = await RequestAsync("thread/start", startParams, ct).ConfigureAwait(false);

        _threadId      = result.TryGetProperty("thread", out var thread) ? Str(thread, "id") : null;
        _resolvedModel = Str(result, "model");
        _clock?.SetLaunchStage("thread_started");

        if (string.IsNullOrEmpty(_threadId))
            throw new InvalidOperationException("codex app-server: thread/start returned no thread id.");
    }

    // ── Turns / rounds ─────────────────────────────────────────────────────────────────────────

    async Task StartTurnAsync(string prompt, CancellationToken ct) {
        var threadId = _threadId ?? throw new InvalidOperationException("codex app-server: no thread to start a turn on.");

        var turnParams = new JsonObject {
            ["threadId"]          = threadId,
            ["input"]             = new JsonArray(new JsonObject { ["type"] = "text", ["text"] = prompt }),
            ["sandboxPolicy"]     = CodexAppServerPosture.RenderSandboxPolicy(_launch.Sandbox, _launch.WritableRoots),
            ["approvalPolicy"]    = CodexAppServerPosture.RenderApprovalPolicy(_launch.Approval),
            ["approvalsReviewer"] = CodexAppServerPosture.ApprovalsReviewer,
        };
        if (!string.IsNullOrEmpty(_launch.Model))  turnParams["model"]  = _launch.Model;
        if (!string.IsNullOrEmpty(_launch.Effort)) turnParams["effort"] = MapEffort(_launch.Effort);

        // Arm the completion signal + the turn-in-flight clock BEFORE the turn is on the wire, so a
        // fast turn/completed can neither race a null TCS nor leave the clock flipped back to
        // in-flight after it was cleared.
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (_turnGate) {
            _turnCompleted     = completion;
            _currentTurnId     = null;
            _pendingCompletion = null;
        }
        _clock?.SetTurnInFlight(true);

        JsonElement result;
        try {
            result = await RequestAsync("turn/start", turnParams, ct).ConfigureAwait(false);
        } catch {
            FaultPendingTurn(new OperationCanceledException("codex app-server: turn/start failed."));
            throw;
        }

        var turnId = result.TryGetProperty("turn", out var turn) ? Str(turn, "id") : null;
        if (string.IsNullOrEmpty(turnId)) {
            var ex = new InvalidOperationException("codex app-server: turn/start returned no turn id.");
            FaultPendingTurn(ex);
            throw ex;
        }

        var status = turn.ValueKind == JsonValueKind.Object ? Str(turn, "status") : null;

        // Bind the id, then reconcile against anything that raced ahead of this response:
        //   - a turn/completed that arrived while the id was unknown (stashed), OR
        //   - a response that itself came back already terminal.
        bool completeNow = false;
        string? completeStatus = null;
        lock (_turnGate) {
            _currentTurnId = turnId;
            if (_pendingCompletion is { } pc && string.Equals(pc.TurnId, turnId, StringComparison.Ordinal)) {
                completeNow = true;
                completeStatus = pc.Status;
            } else if (status is not null and not "inProgress") {
                completeNow = true;
                completeStatus = status;
            }
            _pendingCompletion = null; // discard a stash that was not ours
        }
        if (completeNow) CompleteTurn(turnId, completeStatus);
    }

    public Task SendUserInputAsync(string text) => StartTurnAsync(text, _cts.Token);

    public async Task SendUserInputAndWaitForWriteAsync(string text) =>
        await StartTurnAsync(text, _cts.Token).ConfigureAwait(false);

    public async Task WaitForTurnIdleAsync(CancellationToken ct) {
        TaskCompletionSource? tcs;
        lock (_turnGate) tcs = _turnCompleted;
        if (tcs is null) return; // no turn in flight

        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, _cts.Token);
        var completed = await Task.WhenAny(tcs.Task, _runtimeTerminal.Task,
            Task.Delay(Timeout.Infinite, linked.Token)).ConfigureAwait(false);

        if (completed == tcs.Task || completed == _runtimeTerminal.Task)
            return; // turn settled, or the child died — either way the round is no longer in flight

        await completed.ConfigureAwait(false); // propagate cancellation
    }

    // ── Notifications & the always-decline approval bridge ─────────────────────────────────────

    void HandleNotification(AcpNotification n) {
        switch (n.Method) {
            case "turn/completed": {
                var turnId = n.Params is { } p && p.TryGetProperty("turn", out var turn) ? Str(turn, "id") : null;
                var status = n.Params is { } p2 && p2.TryGetProperty("turn", out var t2) ? Str(t2, "status") : null;
                OnTurnCompletedNotification(turnId, status);
                break;
            }
            case "thread/tokenUsage/updated":
                _usage = ParseUsage(n.Params);
                _clock?.Advance();
                break;
            default:
                _clock?.Advance();
                break;
        }
    }

    void OnTurnCompletedNotification(string? turnId, string? status) {
        lock (_turnGate) {
            if (_currentTurnId is null) {
                // The turn/start response has not identified the current turn yet. Stash this
                // completion by id; StartTurnAsync applies it iff it matches, else discards it.
                if (turnId is not null) _pendingCompletion = (turnId, status);
                return;
            }
            if (turnId is not null && !string.Equals(_currentTurnId, turnId, StringComparison.Ordinal))
                return; // a stale completion from another turn
        }
        CompleteTurn(turnId, status);
    }

    /// <summary>The single point that settles the current round: clears the waiter under the lock
    /// (so exactly one caller wins), then — only if there was a waiter — clears the turn-in-flight
    /// clock and completes the TCS.</summary>
    void CompleteTurn(string? turnId, string? status) {
        TaskCompletionSource? tcs;
        lock (_turnGate) {
            if (_currentTurnId is not null && turnId is not null
                && !string.Equals(_currentTurnId, turnId, StringComparison.Ordinal))
                return; // not the turn we are waiting on

            tcs = _turnCompleted;
            _turnCompleted     = null;
            _currentTurnId     = null;
            _pendingCompletion = null;
        }
        if (tcs is null) return;

        _clock?.SetTurnInFlight(false);
        tcs.TrySetResult();
        if (status is "failed")
            _logger.LogWarning("codex app-server: turn completed with status=failed.");
    }

    void FaultPendingTurn(Exception ex) {
        TaskCompletionSource? tcs;
        lock (_turnGate) {
            tcs = _turnCompleted;
            _turnCompleted     = null;
            _currentTurnId     = null;
            _pendingCompletion = null;
        }
        if (tcs is null) return;

        _clock?.SetTurnInFlight(false);
        tcs.TrySetException(ex);
    }

    /// <summary>
    /// Under <c>approvalPolicy: never</c> no approval request should ever arrive; if one does it is a
    /// protocol violation. Answer it with a valid on-the-wire <c>decline</c> (never a JSON-RPC error,
    /// whose turn/process effect is Codex's to define) and log at Error. The response shape differs
    /// between the approval requests (<c>{decision:"decline"}</c>) and the MCP elicitation /
    /// tool-input requests (<c>{action:"decline"}</c>), keyed on the method name.
    /// </summary>
    Task<JsonElement?> DeclineServerRequestAsync(AcpRequest request, CancellationToken ct) {
        _logger.LogError(
            "codex app-server: unexpected server request '{Method}' under approvalPolicy:never; declining.",
            request.Method);

        var isElicitation = request.Method.Contains("elicitation", StringComparison.Ordinal)
                         || request.Method.Contains("requestUserInput", StringComparison.Ordinal);
        var body = isElicitation
            ? new JsonObject { ["action"]   = "decline" }
            : new JsonObject { ["decision"] = "decline" };

        return Task.FromResult<JsonElement?>(ToElement(body));
    }

    // ── Backpressure-bounded request helper ────────────────────────────────────────────────────

    /// <summary>Every client→server request goes through a bounded retry on the app-server's
    /// <c>-32001</c> bounded-ingress rejection; exhaustion surfaces the RPC error rather than
    /// spinning.</summary>
    async Task<JsonElement> RequestAsync(string method, JsonNode @params, CancellationToken ct) {
        var element = ToElement(@params);
        for (var attempt = 1; ; attempt++) {
            try {
                var connection = _connection ?? throw new InvalidOperationException("codex app-server: no connection.");
                return await connection.RequestAsync(method, element, ct).ConfigureAwait(false);
            } catch (CodexAppServerRpcException rpc) when (rpc.Code == -32001 && attempt < BackpressureMaxAttempts) {
                var delay = TimeSpan.FromMilliseconds(50 * attempt + (attempt * 17 % 40));
                await Task.Delay(delay, _time, ct).ConfigureAwait(false);
            }
        }
    }

    // ── Interface members with no protocol meaning for an unattended reviewer ──────────────────

    /// <summary>The runtime emits no terminal bytes, but this enumerable must NOT complete until the
    /// runtime is logically terminal: the orchestrator treats its completion as the finalize
    /// trigger, so an immediate <c>yield break</c> would finalize (and kill) the reviewer seconds
    /// after launch. Mirrors the ACP runtime — wait on the whole-runtime terminal signal or the
    /// caller's cancellation, then end.</summary>
    public async IAsyncEnumerable<byte[]> ReadOutputAsync([EnumeratorCancellation] CancellationToken ct = default) {
        var ctTcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var reg = ct.Register(() => ctTcs.TrySetResult()).ConfigureAwait(false);

        await Task.WhenAny(_runtimeTerminal.Task, ctTcs.Task).ConfigureAwait(false);
        yield break;
    }

    public Task SendSpecialKeyAsync(string key) => Task.CompletedTask;

    public Task SendRawInputAsync(byte[] data) =>
        throw new NotSupportedException("codex app-server hosted agents have no local-attach terminal surface.");

    public void Resize(ushort cols, ushort rows) { /* no terminal */ }

    public async Task RequestGracefulStopAsync() {
        var connection = _connection;
        var threadId   = _threadId;
        var turnId     = _currentTurnId;
        if (connection is null || threadId is null || turnId is null)
            return;

        try {
            var interruptParams = ToElement(new JsonObject { ["threadId"] = threadId, ["turnId"] = turnId });
            await connection.RequestAsync("turn/interrupt", interruptParams, _cts.Token).ConfigureAwait(false);
            // Bounded wait for the interrupted turn to settle before the caller falls through to
            // terminate; never let this block teardown.
            await WaitForTurnIdleAsync(_cts.Token).WaitAsync(TimeSpan.FromSeconds(5), _time).ConfigureAwait(false);
        } catch (Exception ex) {
            _logger.LogDebug(ex, "codex app-server: graceful stop (turn/interrupt) failed; falling through to terminate.");
        }
    }

    public Task WaitForExitAsync(TimeSpan? timeout = null) =>
        _process?.WaitForExitAsync(timeout) ?? Task.CompletedTask;

    public Task TerminateAsync(TimeSpan? timeout = null) =>
        _process?.TerminateAsync(timeout) ?? Task.CompletedTask;

    public async ValueTask DisposeAsync() {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return;

        try { await _cts.CancelAsync().ConfigureAwait(false); } catch { /* best-effort */ }

        await TeardownChildAsync().ConfigureAwait(false);

        // Ensure the terminal signal is set even if no read loop ever ran (e.g. spawn failed before
        // the loop started) so a ReadOutputAsync consumer never hangs.
        _runtimeTerminal.TrySetResult();
        FaultPendingTurn(new ObjectDisposedException(nameof(CodexAppServerHostedAgentRuntime)));
        _cts.Dispose();
    }

    /// <summary>Tears down the current child + connection and AWAITS its retiring read loop, so a
    /// replacement (the hook-trust restart) is never installed while the old loop is still live.</summary>
    async Task TeardownChildAsync() {
        var connection = _connection;
        var process    = _process;
        var runLoop    = _runLoop;
        var childCts   = _childCts;
        _connection = null;
        _process    = null;
        _childCts   = null;

        // Cancel the read loop FIRST so it unblocks from ReadLineAsync (disposing the stream alone
        // does not reliably unblock a pending pipe read), then await the retiring loop before a
        // replacement is installed.
        if (childCts is not null) try { await childCts.CancelAsync().ConfigureAwait(false); } catch { /* best-effort */ }
        try { await runLoop.WaitAsync(TimeSpan.FromSeconds(5), _time).ConfigureAwait(false); } catch { /* best-effort */ }

        if (connection is not null) {
            connection.OnNotification -= HandleNotification;
            try { await connection.DisposeAsync().ConfigureAwait(false); } catch { /* best-effort */ }
        }
        if (process is not null)
            try { await process.DisposeAsync().ConfigureAwait(false); } catch { /* best-effort */ }
        childCts?.Dispose();
    }

    // ── JSON helpers ───────────────────────────────────────────────────────────────────────────

    static string MapEffort(string effort) =>
        string.Equals(effort, "max", StringComparison.OrdinalIgnoreCase) ? "xhigh" : effort;

    static string? Str(JsonElement o, string name) =>
        o.ValueKind == JsonValueKind.Object && o.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String
            ? v.GetString()
            : null;

    static long Long(JsonElement o, string name) =>
        o.ValueKind == JsonValueKind.Object && o.TryGetProperty(name, out var v)
            && v.ValueKind == JsonValueKind.Number && v.TryGetInt64(out var n)
            ? n : 0;

    static CodexTokenUsage? ParseUsage(JsonElement? paramsEl) {
        if (paramsEl is not { } p || !p.TryGetProperty("tokenUsage", out var u) || u.ValueKind != JsonValueKind.Object)
            return null;
        if (!u.TryGetProperty("total", out var total) || total.ValueKind != JsonValueKind.Object)
            return null;

        return new CodexTokenUsage(
            InputTokens:     Long(total, "inputTokens"),
            CachedInputTokens: Long(total, "cachedInputTokens"),
            OutputTokens:    Long(total, "outputTokens"),
            ReasoningOutputTokens: Long(total, "reasoningOutputTokens"),
            TotalTokens:     Long(total, "totalTokens"));
    }

    // JsonNode → JsonElement without reflection (AOT-safe): the node writes its own JSON.
    static JsonElement ToElement(JsonNode node) =>
        JsonDocument.Parse(node.ToJsonString()).RootElement.Clone();
}

/// <summary>Cumulative thread token usage from <c>thread/tokenUsage/updated.total</c>.</summary>
internal readonly record struct CodexTokenUsage(
    long InputTokens, long CachedInputTokens, long OutputTokens, long ReasoningOutputTokens, long TotalTokens);
