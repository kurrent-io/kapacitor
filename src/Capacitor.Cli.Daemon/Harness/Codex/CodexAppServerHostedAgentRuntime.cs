using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Channels;
using Capacitor.Cli.Core;
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
/// <param name="ResumeSessionId">A parked Codex thread id to REOPEN via <c>thread/resume</c> instead of
/// minting a fresh one with <c>thread/start</c>. Non-null only for a parked reviewer relaunch; the
/// resumed thread keeps its id, so no second SessionStarted is emitted. A resume that the app-server
/// rejects fails the launch (coded) rather than silently starting a new thread.</param>
internal sealed record CodexAppServerLaunch(
    string                Cwd,
    string?               Model,
    string?               Effort,
    string?               InitialPrompt,
    string                Sandbox,
    string                Approval,
    IReadOnlyList<string> WritableRoots,
    string                ClientVersion,
    string?               ResumeSessionId = null);

/// <summary>
/// A <see cref="IHostedAgentRuntime"/> that hosts a Codex agent over the <c>codex app-server</c>
/// JSON-RPC protocol instead of the interactive PTY. It exposes <see cref="IAcpTranscriptSource"/>
/// (§2.4): every app-server notification is translated by <see cref="CodexNotificationMapper"/> into the
/// canonical/ephemeral <see cref="AcpEventEnvelope"/> vocabulary and drained through a bounded
/// <see cref="CodexForwardBuffer"/>, so the envelope transcript — not the hooks + <c>kcap watch</c>
/// rollout path — is the ingestion source for hosted app-server sessions. <see cref="EmitsTerminalOutput"/>
/// stays <see langword="false"/> (this is a structured, not terminal, view).
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
internal sealed partial class CodexAppServerHostedAgentRuntime : IHostedAgentRuntime, IAcpTranscriptSource {
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

    // §2.4 forward buffer sizing. The capacity trades memory for the SignalR-outage window a canonical
    // burst can ride before the read loop blocks; the stall timeout bounds that block before a
    // deterministic terminal fault. `codex.appServer.forwardStallSeconds` (spec-named) maps to the
    // injectable stall timeout below; the default engages when the caller passes none.
    const int    ForwardBufferCapacity      = 1024;
    const double DefaultForwardStallSeconds = 30;

    readonly CodexAppServerSpawn  _spawn;
    readonly CodexAppServerLaunch _launch;
    readonly AgentActivityClock?  _clock;
    readonly ILogger              _logger;
    readonly TimeProvider         _time;
    readonly CancellationTokenSource _cts = new();

    // Whole-runtime terminal signal: completed exactly once, when the LIVE child's read loop ends
    // (process death) or on dispose — never for the controlled child teardown during a hook-trust
    // restart. The orchestrator treats ReadOutputAsync ending as the finalize trigger, so this must
    // not fire early, and WaitForTurnIdleAsync must unblock on it so a round never outlives the child.
    readonly TaskCompletionSource _runtimeTerminal = new(TaskCreationOptions.RunContinuationsAsynchronously);

    // The single input serializer: all input enqueues here, one dispatcher chooses turn/start vs
    // turn/steer and owns the completion-window race (§2.2). Turn state (active turn id, in-flight
    // clock, WaitForTurnIdle) is derived from it — the runtime no longer tracks turns itself.
    readonly CodexTurnInputDispatcher _dispatcher;

    // §2.4 envelope transcript: the mapper translates every app-server notification into
    // canonical/ephemeral envelopes, drained through the bounded forward buffer (IAcpTranscriptSource).
    readonly CodexNotificationMapper _mapper;
    readonly CodexForwardBuffer      _forwardBuffer;

    CodexAppServerConnection? _connection;
    IAcpProcess?              _process;
    Task                      _runLoop = Task.CompletedTask;
    CancellationTokenSource?  _childCts;   // per-child read-loop token, so a teardown unblocks the loop
    volatile bool             _restarting; // true only across the hook-trust seed-and-restart window

    string?                   _threadId;
    string?                   _resolvedModel;
    CodexTokenUsage?          _usage;
    int                       _disposed;

    // §2.4 envelope emission is gated OFF until §2.5 activates the envelope-source ingestion (deferred
    // first-turn, source claim, and the hooks/watch dedup). Feeding the buffer while it is NOT drained
    // by an attached AcpTranscriptForwarder would fill it and stall the read loop; draining it WITHOUT
    // the §2.5 dedup would double-ingest a reviewer session (hooks + envelopes). So the transcript
    // surface (IAcpTranscriptSource, mapper, buffer) ships here dormant; §2.5 flips this one flag
    // together with the factory's Transcript wiring and the dedup guards.
    readonly bool _emitEnvelopeTranscript;

    // Whether the FIRST turn is deferred behind a source claim (the §2.5 deferred-first-turn contract).
    // Distinct from _emitEnvelopeTranscript (envelope EMISSION, §2.4): the two are logically separable
    // (emission needs no seal; a seal needs no emission) and each is independently testable, even
    // though production flips BOTH together at the envelope-source activation.
    readonly bool _deferFirstTurn;

    // The held initial prompt's dispatch, when the first turn is DEFERRED: its turn/start is enqueued
    // sealed at StartAsync (so it sits at the head) but not sent until the orchestrator's source claim
    // acks and calls BeginFirstTurnAsync. The task completes when that first turn/start's RESPONSE
    // lands — the signal the orchestrator confirms on. Null with no initial prompt, or single-phase.
    Task? _firstTurnDispatch;

    // §2.3 interactive approvals: non-null only for an INTERACTIVE launch (approvalPolicy != never) that
    // was given a requestInteraction delegate — it forwards server-initiated requestApproval to the user
    // and answers with their decision. Null on the reviewer path (never), which keeps DeclineServerRequestAsync.
    readonly CodexApprovalBridge? _approvalBridge;

    const double DefaultApprovalTimeoutSeconds = 45;

    public CodexAppServerHostedAgentRuntime(
            CodexAppServerSpawn spawn, CodexAppServerLaunch launch, AgentActivityClock? clock,
            ILogger logger, TimeProvider? timeProvider = null, TimeSpan? forwardStallTimeout = null,
            bool emitEnvelopeTranscript = false, bool deferFirstTurn = false,
            string? agentId = null,
            Func<AcpInteractionRequest, CancellationToken, Task<AcpInteractionDecision>>? requestInteraction = null,
            TimeSpan? approvalTimeout = null) {
        _spawn   = spawn;
        _launch  = launch;
        _clock   = clock;
        _logger  = logger;
        _time    = timeProvider ?? TimeProvider.System;
        _emitEnvelopeTranscript = emitEnvelopeTranscript;
        _deferFirstTurn = deferFirstTurn;
        // Interactive iff approvals are actually raised (never ⇒ reviewer ⇒ decline path) AND we can both
        // correlate (agentId) and reach the user (requestInteraction).
        if (requestInteraction is not null && agentId is not null
         && !string.Equals(_launch.Approval, "never", StringComparison.Ordinal)) {
            _approvalBridge = new CodexApprovalBridge(
                requestInteraction, agentId, logger,
                approvalTimeout ?? TimeSpan.FromSeconds(DefaultApprovalTimeoutSeconds));
        }
        _dispatcher = new CodexTurnInputDispatcher(
            startTurn: IssueTurnStartAsync, steerTurn: IssueTurnSteerAsync,
            logger: logger, ct: _cts.Token, onTurnInFlight: flip => _clock?.SetTurnInFlight(flip),
            // Deferring the first turn seals the dispatcher — externally-reachable input arriving during
            // the source-claim window enqueues and waits; BeginFirstTurnAsync unseals it.
            sealedAtStart: deferFirstTurn);
        _mapper = new CodexNotificationMapper(() => _resolvedModel, logger);
        _forwardBuffer = new CodexForwardBuffer(
            ForwardBufferCapacity,
            forwardStallTimeout ?? TimeSpan.FromSeconds(DefaultForwardStallSeconds),
            _cts.Token, OnForwardStall);
    }

    // ── IHostedAgentRuntime: identity / lifecycle observables ──────────────────────────────────

    public string Vendor              => "codex";
    public int    Pid                 => _process?.Pid ?? 0;
    public bool   HasExited           => _process?.HasExited ?? false;
    public int?   ExitCode            => _process?.ExitCode;
    public bool   EmitsTerminalOutput => false;

    // The one runtime that reports app-server; every other runtime keeps the interface's "pty" default.
    public string RuntimeTransport    => CodexTransportDecision.AppServer;

    /// <summary>Resolved model from the <c>thread/start</c> response (never the requested one);
    /// null until the handshake completes. Feeds the existing launch-attempt reporting.</summary>
    public string? ResolvedModel => _resolvedModel;

    /// <summary>Daemon-held thread id — the deterministic session-id correlation that replaces the
    /// <c>CodexSessionRolloutLocator</c> timestamp race.</summary>
    public string? ThreadId => _threadId;

    /// <summary>Latest cumulative token usage reported over <c>thread/tokenUsage/updated</c>, or null
    /// if none has arrived yet.</summary>
    public CodexTokenUsage? Usage => _usage;

    // ── IAcpTranscriptSource (§2.4 envelope transcript) ──────────────────────────────────────────
    // The canonical session id is the app-server thread id (== the rollout filename id and the hook
    // payload session_id, established by the app-server protocol spike), read only after the
    // thread/start handshake sets it.

    /// <inheritdoc cref="IAcpTranscriptSource.AcpSessionId"/>
    public string AcpSessionId => _threadId ?? "";

    /// <inheritdoc cref="IAcpTranscriptSource.Cwd"/>
    string IAcpTranscriptSource.Cwd => _launch.Cwd;

    /// <inheritdoc cref="IAcpTranscriptSource.Envelopes"/>
    public ChannelReader<AcpEventEnvelope> Envelopes => _forwardBuffer.Reader;

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

        if (!string.IsNullOrEmpty(_launch.InitialPrompt)) {
            if (_deferFirstTurn) {
                // DEFERRED first turn: the dispatcher is sealed, so this enqueue parks the prompt at the
                // head of the queue and dispatches NOTHING now — we must NOT await it (the ack cannot
                // complete until BeginFirstTurnAsync unseals). Use CancellationToken.None as the per-input
                // token because StartAsync's `linked` is disposed on return, long before the deferred
                // send; the dispatcher's own runtime token (_cts.Token) still aborts it on teardown.
                _firstTurnDispatch = _dispatcher.EnqueueAsync(_launch.InitialPrompt, CancellationToken.None);
            } else {
                await _dispatcher.EnqueueAsync(_launch.InitialPrompt, linked.Token).ConfigureAwait(false);
            }
        }
    }

    /// <summary>
    /// When true, <see cref="StartAsync"/> has established the thread and HELD the first turn (sealed
    /// the input dispatcher) instead of dispatching it — the orchestrator must durably source-claim
    /// this session, then call <see cref="BeginFirstTurnAsync"/>, before any turn (and therefore any
    /// hook or rollout line) can exist. Driven by the deferred-first-turn flag (production flips it with
    /// the envelope-source activation); every single-phase launch keeps the immediate first turn.
    /// </summary>
    public bool RequiresSourceClaimBeforeFirstTurn => _deferFirstTurn;

    /// <summary>
    /// Unseals the input dispatcher — dispatching the held initial prompt as the first <c>turn/start</c>,
    /// then any input that arrived during the sealed claim window FIFO — and awaits the first turn's
    /// JSON-RPC RESPONSE (the signal the orchestrator confirms the launch on). Called once, only when
    /// <see cref="RequiresSourceClaimBeforeFirstTurn"/> is true and only after the source claim acks.
    /// With no initial prompt it simply unseals (a subsequent user input becomes the first turn).
    /// </summary>
    public async Task BeginFirstTurnAsync(CancellationToken ct = default) {
        // A launch cancelled during the source-claim / forwarder setup must NOT release the held first
        // turn — dispatching turn/start after finalization began races the teardown. Observe the token
        // BEFORE unsealing: the dispatch runs on CancellationToken.None and can't be recalled once it
        // leaves, so this is the only safe gate. (A cancel in the sliver between here and Unseal is the
        // same benign post-unseal race the comment below covers — teardown kills the process.)
        ct.ThrowIfCancellationRequested();
        _dispatcher.Unseal();
        // If ct fires after the unseal, the held turn/start may already have left (the dispatch used
        // CancellationToken.None) — cancelling this await does NOT recall it. That is fine: a cancel here
        // means the orchestrator is aborting the launch and will tear this runtime down, killing the
        // process; any dispatched turn dies with it, and its late response hits the dispatcher's faulted
        // guard and is discarded.
        if (_firstTurnDispatch is { } dispatch)
            await dispatch.WaitAsync(ct).ConfigureAwait(false);
    }

    async Task SpawnAndInitializeAsync(string? hookStateSeed, CancellationToken ct) {
        var (connection, process) = await _spawn(hookStateSeed, ct).ConfigureAwait(false);
        _connection = connection;
        _process    = process;

        connection.OnNotification  += HandleNotification;
        // Interactive launches forward requestApproval to the user (§2.3); reviewers (approvalPolicy:never)
        // keep declining, since a request there is a protocol violation, not a prompt.
        connection.OnServerRequest  = _approvalBridge is { } bridge ? bridge.HandleAsync : DeclineServerRequestAsync;

        _childCts = CancellationTokenSource.CreateLinkedTokenSource(_cts.Token);
        _runLoop  = RunConnectionAsync(connection, _childCts.Token);
        _clock?.SetLaunchStage("spawned");

        // With the envelope transcript ON, the mapper's ephemeral lane consumes exactly the delta streams
        // DeltaOptOut suppresses, so opting out would silence the live lane the gate is meant to enable —
        // receive everything. With it OFF (reviewer path), keep the perf opt-out.
        var optOut = _emitEnvelopeTranscript ? Array.Empty<string>() : DeltaOptOut;
        var initParams = new JsonObject {
            ["clientInfo"]   = new JsonObject { ["name"] = ClientName, ["version"] = _launch.ClientVersion },
            ["capabilities"] = new JsonObject {
                ["optOutNotificationMethods"] = new JsonArray(optOut.Select(m => (JsonNode?) m).ToArray()),
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
                _dispatcher.FaultAll(new ObjectDisposedException(nameof(CodexAppServerHostedAgentRuntime),
                    "codex app-server connection ended with input still in flight."));
            }
        }
    }

    async Task<IReadOnlyList<CodexHookEntry>> ListHooksAsync(CancellationToken ct) {
        var listParams = new JsonObject { ["cwds"] = new JsonArray((JsonNode?) _launch.Cwd) };
        var result     = await RequestAsync("hooks/list", listParams, ct).ConfigureAwait(false);

        var entries = new List<CodexHookEntry>();
        if (result.Arr("data") is { } data) {
            foreach (var group in data.EnumerateArray()) {
                if (group.Arr("hooks") is not { } hooks) continue;

                foreach (var h in hooks.EnumerateArray()) {
                    entries.Add(new CodexHookEntry(
                        Key:         h.Str("key") ?? "",
                        EventName:   h.Str("eventName") ?? "",
                        Command:     h.Str("command") ?? "",
                        CurrentHash: h.Str("currentHash"),
                        TrustStatus: h.Str("trustStatus") ?? ""));
                }
            }
        }
        return entries;
    }

    async Task StartThreadAsync(CancellationToken ct) {
        // §2.7 B4: a non-null ResumeSessionId REOPENS that thread via thread/resume (no round-1 replay, no
        // second SessionStarted); null ⇒ first launch via thread/start. A present-but-blank id is NOT a
        // valid "no resume" sentinel — treating it as absent would silently thread/start and FORK the
        // transcript, breaking the never-silently-fork invariant — so fail the launch loudly instead.
        var isResume = _launch.ResumeSessionId is not null;
        if (isResume && string.IsNullOrWhiteSpace(_launch.ResumeSessionId))
            throw new InvalidOperationException(
                "codex app-server: ResumeSessionId is present but blank; refusing to silently start a fresh thread.");
        var method = isResume ? "thread/resume" : "thread/start";

        var startParams = new JsonObject {
            ["cwd"]               = _launch.Cwd,
            // thread/{start,resume}.sandbox is the coarse SandboxMode STRING (read-only / workspace-write /
            // danger-full-access) — a different wire shape from turn/start.sandboxPolicy's object.
            // The resolved posture token already IS that string; the per-turn sandboxPolicy object
            // is the load-bearing containment.
            ["sandbox"]           = CodexAppServerPosture.RenderSandboxMode(_launch.Sandbox),
            ["approvalPolicy"]    = CodexAppServerPosture.RenderApprovalPolicy(_launch.Approval),
            ["approvalsReviewer"] = CodexAppServerPosture.ApprovalsReviewer,
        };
        if (!string.IsNullOrEmpty(_launch.Model))
            startParams["model"] = _launch.Model;
        if (isResume)
            startParams["threadId"] = _launch.ResumeSessionId; // resume-by-thread_id; response.thread.id == this id

        // A resume the app-server rejects is a clean JSON-RPC error out of RequestAsync -> the launch fails
        // coded; it NEVER silently starts a new thread (that would fork the transcript).
        var result = await RequestAsync(method, startParams, ct).ConfigureAwait(false);

        _threadId      = result.Obj("thread")?.Str("id"); // same read for both start and resume
        _resolvedModel = result.Str("model");
        _clock?.SetLaunchStage(isResume ? "thread_resumed" : "thread_started");

        if (string.IsNullOrEmpty(_threadId))
            throw new InvalidOperationException($"codex app-server: {method} returned no thread id.");

        // §2.7 B4 usage baseline: on resume the first thread/tokenUsage/updated carries the thread-CUMULATIVE
        // total, so baseline off the next snapshot (emits nothing) to avoid double-counting round 1 as the
        // first delta. Bounded fallback (an exact thread/read baseline is a follow-up); set before the
        // deferred first turn so it lands ahead of any post-resume usage notification.
        if (isResume)
            _mapper.UsageBaselineOnNextNotification();
    }

    // ── Turns / rounds ─────────────────────────────────────────────────────────────────────────

    // The dispatcher's turn/start sender: builds the per-turn posture params (§2.1 renders them here,
    // not argv) and issues turn/start, returning the server-assigned turn. A -32001 backpressure
    // rejection is retried inside RequestAsync.
    async Task<CodexTurnStarted> IssueTurnStartAsync(string text, CancellationToken ct) {
        var threadId = _threadId ?? throw new InvalidOperationException("codex app-server: no thread to start a turn on.");

        var turnParams = new JsonObject {
            ["threadId"]          = threadId,
            ["input"]             = new JsonArray(new JsonObject { ["type"] = "text", ["text"] = text }),
            ["sandboxPolicy"]     = CodexAppServerPosture.RenderSandboxPolicy(_launch.Sandbox, _launch.WritableRoots),
            ["approvalPolicy"]    = CodexAppServerPosture.RenderApprovalPolicy(_launch.Approval),
            ["approvalsReviewer"] = CodexAppServerPosture.ApprovalsReviewer,
        };
        if (!string.IsNullOrEmpty(_launch.Model))  turnParams["model"]  = _launch.Model;
        if (!string.IsNullOrEmpty(_launch.Effort)) turnParams["effort"] = MapEffort(_launch.Effort);

        var result = await RequestAsync("turn/start", turnParams, ct).ConfigureAwait(false);
        var turn   = result.Obj("turn");
        var turnId = turn?.Str("id");
        if (string.IsNullOrEmpty(turnId))
            throw new InvalidOperationException("codex app-server: turn/start returned no turn id.");
        return new CodexTurnStarted(turnId, turn?.Str("status"));
    }

    // The dispatcher's turn/steer sender: feeds an input onto the ACTIVE turn (posture is already
    // pinned by its turn/start). A stale/ended turn answers -32600 as a CodexAppServerRpcException,
    // which the dispatcher catches and retries once as a turn/start.
    async Task IssueTurnSteerAsync(string turnId, string text, CancellationToken ct) {
        var threadId = _threadId ?? throw new InvalidOperationException("codex app-server: no thread to steer.");
        var steerParams = new JsonObject {
            ["threadId"]       = threadId,
            ["expectedTurnId"] = turnId,
            ["input"]          = new JsonArray(new JsonObject { ["type"] = "text", ["text"] = text }),
        };
        await RequestAsync("turn/steer", steerParams, ct).ConfigureAwait(false);
    }

    public Task SendUserInputAsync(string text) => _dispatcher.EnqueueAsync(text);

    public Task SendUserInputAndWaitForWriteAsync(string text) => _dispatcher.EnqueueAsync(text);

    public async Task WaitForTurnIdleAsync(CancellationToken ct) {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, _cts.Token);
        var settled   = _dispatcher.WaitForSettledAsync();
        var completed = await Task.WhenAny(settled, _runtimeTerminal.Task,
            Task.Delay(Timeout.Infinite, linked.Token)).ConfigureAwait(false);

        if (completed == settled || completed == _runtimeTerminal.Task)
            return; // input drained + turn settled, or the child died — the round is no longer in flight

        await completed.ConfigureAwait(false); // propagate cancellation
    }

    // ── Notifications & the always-decline approval bridge ─────────────────────────────────────

    void HandleNotification(AcpNotification n) {
        switch (n.Method) {
            case "turn/completed": {
                var turn = n.Params is { } p ? p.Obj("turn") : null;
                if (turn?.Str("status") is "failed")
                    _logger.LogWarning("codex app-server: turn completed with status=failed.");
                _dispatcher.OnTurnCompleted(turn?.Str("id"));
                break;
            }
            case "thread/tokenUsage/updated":
                _usage = ParseUsage(n.Params);
                _clock?.Advance();
                break;
            case "model/rerouted":
                // The resolved model changed mid-thread; the mapper attributes each subsequent token
                // delta to the model-at-instant, so per-interval attribution across a reroute is correct.
                if (n.Params?.Str("model") is { } rerouted) _resolvedModel = rerouted;
                _clock?.Advance();
                break;
            default:
                _clock?.Advance();
                break;
        }

        // §2.4 envelope transcript: translate every notification into canonical/ephemeral envelopes and
        // drain them through the forward buffer. Runs on the read-loop thread, so a full buffer blocks
        // here (a canonical emit) — the intended lossless backpressure onto the app-server's stdout.
        // Gated off until §2.5 activates envelope-source ingestion (see _emitEnvelopeTranscript).
        if (_emitEnvelopeTranscript)
            foreach (var env in _mapper.Map(n.Method, n.Params))
                _forwardBuffer.Emit(env);
    }

    // §2.4 stall watchdog: the forward buffer stayed full of canonical envelopes past the stall timeout
    // (the SignalR leg is wedged). Fault the runtime deterministically rather than wedging the read loop
    // forever — the truncated canonical tail is reported here, never dropped silently.
    void OnForwardStall(TimeSpan stallTimeout) {
        _logger.LogError(
            "codex app-server: forward buffer stalled for {StallSeconds}s (SignalR forwarding wedged); faulting the session — canonical tail truncated.",
            stallTimeout.TotalSeconds);
        _runtimeTerminal.TrySetResult();
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
        var turnId     = _dispatcher.CurrentTurnId;
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
        _dispatcher.FaultAll(new ObjectDisposedException(nameof(CodexAppServerHostedAgentRuntime)));

        // A deferred first turn that was HELD but never begun (teardown before the source claim reached
        // BeginFirstTurnAsync) leaves _firstTurnDispatch faulted-but-unawaited by the FaultAll above.
        // Await it with SuppressThrowing to OBSERVE the fault (never an unobserved task exception) and to
        // let Dispose complete only once it settles — it is already faulted/completed here (FaultAll just
        // ran, or the normal path already awaited it), so this returns immediately.
        if (_firstTurnDispatch is { } firstTurn)
            await firstTurn.ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);

        _forwardBuffer.Complete(); // end the envelope stream so a draining forwarder's ReadAllAsync completes
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

    static CodexTokenUsage? ParseUsage(JsonElement? paramsEl) {
        if (paramsEl is not { } p || p.Obj("tokenUsage") is not { } u || u.Obj("total") is not { } total)
            return null;

        return CodexTokenUsage.FromTotal(total);
    }

    // JsonNode → JsonElement without reflection (AOT-safe): the node writes its own JSON.
    static JsonElement ToElement(JsonNode node) =>
        JsonDocument.Parse(node.ToJsonString()).RootElement.Clone();
}

/// <summary>Cumulative thread token usage from <c>thread/tokenUsage/updated.total</c> (the app-server
/// <c>TokenUsageBreakdown</c>). <see cref="CacheWriteInputTokens"/> is the cache-CREATION tier, billed
/// separately from <see cref="CachedInputTokens"/> reads — kept so the delta converter never silently
/// drops a billed bucket.</summary>
internal readonly record struct CodexTokenUsage(
    long InputTokens, long CachedInputTokens, long CacheWriteInputTokens,
    long OutputTokens, long ReasoningOutputTokens, long TotalTokens) {

    /// <summary>Reads a <c>TokenUsageBreakdown</c> object (<c>thread/tokenUsage/updated.total</c> or
    /// <c>.last</c>). Missing fields read as 0 (the schema defaults <c>cacheWriteInputTokens</c> to 0
    /// and the others are required).</summary>
    public static CodexTokenUsage FromTotal(JsonElement total) => new(
        InputTokens:           total.Num("inputTokens")           ?? 0,
        CachedInputTokens:     total.Num("cachedInputTokens")     ?? 0,
        CacheWriteInputTokens: total.Num("cacheWriteInputTokens") ?? 0,
        OutputTokens:          total.Num("outputTokens")          ?? 0,
        ReasoningOutputTokens: total.Num("reasoningOutputTokens") ?? 0,
        TotalTokens:           total.Num("totalTokens")           ?? 0);

    /// <summary>True when every additive bucket is zero — a delta carrying no information, which the
    /// mapper drops rather than stamping an empty <c>$usage</c>.</summary>
    public bool IsZero =>
        InputTokens == 0 && CachedInputTokens == 0 && CacheWriteInputTokens == 0
     && OutputTokens == 0 && ReasoningOutputTokens == 0 && TotalTokens == 0;
}
