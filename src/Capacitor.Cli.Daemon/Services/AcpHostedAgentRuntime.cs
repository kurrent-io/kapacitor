// src/Capacitor.Cli.Daemon/Services/AcpHostedAgentRuntime.cs
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using Capacitor.Cli.Core;
using Capacitor.Cli.Core.Acp;
using Capacitor.Cli.Daemon.Acp;
using Microsoft.Extensions.Logging;

namespace Capacitor.Cli.Daemon.Services;

/// <summary>
/// <see cref="IHostedAgentRuntime"/> that drives an ACP (Agent Client Protocol) session over
/// <see cref="AcpConnection"/> for any ACP-speaking vendor (Cursor today, more via
/// <c>AcpVendorDescriptor</c>). Owns the <c>initialize</c> →
/// <c>session/new</c> → <c>session/prompt</c> handshake and reduces inbound <c>session/update</c>
/// notifications to <see cref="AcpSessionUpdate"/> DTOs, surfaced via <see cref="Updates"/> for
/// the mapper to turn into canonical events. Scope stops there — no canonical events, no
/// permission bridge (<c>OnServerRequest</c> stays unset, so the connection's default-decline
/// posture answers any inbound server request with a method-not-found error; a follow-up wires
/// the real bridge). Local-attach (raw byte input) and terminal output are PTY-only surfaces the
/// ACP runtime does not support until a follow-up adds a terminal capability.
///
/// Also owns a serialized, single-flight prompt-turn worker (<see cref="RunTurnWorkerAsync"/>/
/// <see cref="ProcessTurnAsync"/>) and a chunk aggregator (<see cref="AggregateUpdate"/>) that
/// together turn the raw <c>session/update</c> stream into an ORDERED, per-turn-aggregated
/// <see cref="AcpEventEnvelope"/> transcript, exposed via <see cref="IAcpTranscriptSource"/> for the
/// orchestrator to bind and forward. Prompt turns (the initial launch prompt, and every
/// <see cref="SendUserInputAsync"/>) are enqueued onto a FIFO
/// queue rather than fired as independent background work, so exactly one turn's chunks are ever
/// aggregating at a time — a `stopReason` always flushes the buffer belonging to ITS OWN turn, never
/// a concurrently-fired one. See <see cref="_aggregationLock"/>'s remarks for the thread-safety
/// mechanism between the worker's turn-end flush and the connection read-loop's kind-transition
/// flush.
/// </summary>
internal sealed partial class AcpHostedAgentRuntime : IHostedAgentRuntime, IAcpTranscriptSource {
    static readonly AcpMcpServerSpec[] NoMcpServers = [];

    readonly AcpConnection _connection;
    readonly IAcpProcess   _process;
    readonly ILogger       _logger;
    readonly TimeProvider  _timeProvider;
    readonly string        _agentId;
    readonly bool          _debugFrames;
    readonly string        _vendor;
    readonly IAcpModelSelector _modelSelector;
    readonly AcpInteractionBridge? _interactionBridge;
    readonly CancellationTokenSource _cts = new();
    // Raw reduced-update surface, used only for test/live-inspection — the production transcript
    // pipeline reads Envelopes, not this. Bounded + DropOldest so a long session with no reader
    // can't grow it without bound (the transcript channel is bounded for the same reason).
    readonly Channel<AcpSessionUpdate> _updates = Channel.CreateBounded<AcpSessionUpdate>(
        new BoundedChannelOptions(2000) { SingleReader = false, SingleWriter = true, FullMode = BoundedChannelFullMode.DropOldest });

    /// <summary>
    /// Default cap for <see cref="_transcript"/> — generous relative to a realistic session's
    /// envelope volume (aggregation already collapses chunk runs into one envelope each; even a long
    /// turn with dozens of tool calls stays well under this), so it only ever bites during a
    /// genuinely stalled/outaged forwarder. Overridable via the constructor so tests can exercise the
    /// drop path with a small cap.
    /// </summary>
    const int DefaultTranscriptCapacity = 2000;

    /// <summary>
    /// Default cap for <see cref="_pendingTurns"/> — user inputs are low-volume (a human typing
    /// follow-ups), so a modest cap is enough to absorb a burst without ever realistically filling up
    /// in normal use.
    /// </summary>
    const int DefaultPendingTurnsCapacity = 50;

    readonly int _transcriptCapacity;
    readonly int _pendingTurnsCapacity;
    int          _droppedTranscriptEnvelopes;
    int          _droppedPendingTurns;

    /// <summary>
    /// The ordered, aggregated <see cref="AcpEventEnvelope"/> transcript — every write goes through
    /// <see cref="EmitEnvelope"/>, which always holds <see cref="_aggregationLock"/>, so this
    /// channel's FIFO write order matches lock-acquisition order across the two call sites that can
    /// write to it (the turn worker, and the connection's notification handler). SingleReader: the
    /// forwarder is the only intended consumer.
    ///
    /// Bounded (see <see cref="DefaultTranscriptCapacity"/>) with
    /// <see cref="BoundedChannelFullMode.DropOldest"/> — NEVER <see cref="BoundedChannelFullMode.Wait"/>.
    /// The sole writer (<see cref="EmitEnvelope"/>) always uses the non-blocking <c>TryWrite</c>, which
    /// never blocks regardless of <see cref="BoundedChannelFullMode"/> — but <c>EmitEnvelope</c> runs
    /// SYNCHRONOUSLY on the ACP connection's own read loop (via <see cref="HandleNotification"/>), so
    /// a future change toward an awaited <c>WriteAsync</c> under <c>Wait</c> would stall that read
    /// loop, not just this session's transcript, if the forwarder (the reader) is itself stalled —
    /// <c>DropOldest</c> forecloses that class of bug entirely: a stalled forwarder degrades to
    /// "lost some trailing transcript", never a blocked connection or unbounded memory growth.
    /// </summary>
    readonly Channel<AcpEventEnvelope> _transcript;

    /// <summary>
    /// FIFO queue of pending prompt-turn texts. Public entry points (<see cref="StartAsync"/>'s
    /// initial prompt, <see cref="SendUserInputAsync"/>) call <see cref="EnqueueTurn"/> and return
    /// immediately; the single <see cref="RunTurnWorkerAsync"/> worker task drains this strictly in
    /// order, one turn fully at a time (see <see cref="ProcessTurnAsync"/>). SingleReader: only the
    /// worker reads; SingleWriter=false since both StartAsync and (potentially concurrent)
    /// SendUserInputAsync calls enqueue.
    ///
    /// Bounded (see <see cref="DefaultPendingTurnsCapacity"/>) with
    /// <see cref="BoundedChannelFullMode.DropWrite"/> — a burst of input while the worker is
    /// stuck on a stalled turn drops the NEW input rather than evicting an earlier, still-pending one
    /// (either is a pathological case; this queue realistically never gets anywhere near the cap).
    /// </summary>
    readonly Channel<PendingTurn> _pendingTurns;
    readonly SemaphoreSlim _turnExecutionGate = new(1, 1);

    /// <summary>
    /// Guards the open aggregation run (<see cref="_openRunKind"/>/<see cref="_openRunText"/>) AND
    /// every write to <see cref="_transcript"/> (via <see cref="EmitEnvelope"/>). Two call sites can
    /// mutate/flush the run: the connection's read loop (synchronously, via
    /// <see cref="HandleNotification"/> → <see cref="AggregateUpdate"/>, on a kind transition) and the
    /// turn worker (via <see cref="ProcessTurnAsync"/>'s turn-end flush, which runs as the
    /// continuation of an awaited <c>session/prompt</c> response and so is NOT guaranteed to run on
    /// the read-loop's own thread — <see cref="AcpConnection.RequestAsync"/>'s
    /// <c>TaskCompletionSource</c> is created with <c>RunContinuationsAsynchronously</c> specifically
    /// so completing it never runs the awaiter's continuation inline). Because turns are serialized,
    /// these two call sites are never ACTUALLY contending in practice — the worker only sends turn
    /// N+1's <c>session/prompt</c> (and so only that turn's updates can start arriving) after turn
    /// N's flush has already completed — but a plain <c>lock</c> (reentrant on the same thread, so
    /// <see cref="FlushOpenRunLocked"/> calling back into <see cref="EmitEnvelope"/> cannot
    /// self-deadlock) is a cheap, simple guarantee against that invariant ever silently breaking (a
    /// future change to the worker, an agent that violates the one-turn-at-a-time assumption, etc.)
    /// rather than relying solely on the timing argument. A single loop reading both the update
    /// stream and the turn-boundary signal was the other option considered — rejected because it
    /// would require plumbing the connection's notification callback through a channel too, adding a
    /// second unbounded channel + consumer loop for no additional safety over a lock given the
    /// happens-before analysis above.
    /// </summary>
    readonly object _aggregationLock = new();

    AcpUpdateKind?  _openRunKind;
    StringBuilder?  _openRunText;

    Task    _connectionRunTask = Task.CompletedTask;
    Task    _turnWorkerTask    = Task.CompletedTask;
    string? _sessionId;
    string? _cwd;
    string? _resolvedModel;
    int     _disposed;
    int     _unexpectedUnattendedInteractionSeen;

    /// <summary>Agent capabilities negotiated by <see cref="StartAsync"/>'s <c>initialize</c> call; null before that.</summary>
    AgentCapabilities? _negotiatedCapabilities;

    /// <summary>The <c>protocolVersion</c> negotiated by <see cref="StartAsync"/>'s <c>initialize</c> call — captured purely for the <see cref="LogHandshakeOk"/> lifecycle log; 0 before that.</summary>
    int _negotiatedProtocolVersion;

    /// <summary>
    /// A deliberate, already-actionable handshake error (unsupported ACP protocol version) — NOT an
    /// auth/connection failure. The handshake catch rethrows it unwrapped so the auth/subscription
    /// hint isn't misapplied to it. Derives from <see cref="InvalidOperationException"/> so callers
    /// that catch that still see it as one.
    /// </summary>
    sealed class AcpProtocolVersionException(string message) : InvalidOperationException(message);

    /// <summary>
    /// Makes an error message safe to fold into a forwarded launch-failure string: collapses line
    /// breaks to spaces and caps the length. The source can be an agent-controlled JSON-RPC
    /// <c>error.message</c> (via <see cref="AcpRpcException"/>), which could otherwise be arbitrarily
    /// long or multi-line and degrade logs/UI downstream. The full original exception is retained as
    /// the thrown exception's <c>InnerException</c>, so nothing is lost for daemon-side diagnostics.
    /// </summary>
    static string SanitizeForForward(string message, int maxLength = 500) {
        var oneLine = message.ReplaceLineEndings(" ").Trim();
        return oneLine.Length <= maxLength ? oneLine : oneLine[..maxLength] + "…";
    }

    /// <summary>
    /// <paramref name="requestInteraction"/> is optional — when null, matches the
    /// original behavior exactly: <see cref="AcpConnection.OnServerRequest"/> stays unset, and any
    /// <c>session/request_permission</c>/<c>elicitation/create</c> the agent sends gets the
    /// connection's default JSON-RPC "Method not found" response. When provided, it is forwarded
    /// into a new <see cref="AcpInteractionBridge"/> and wired as <see cref="AcpConnection.OnServerRequest"/>.
    ///
    /// <b>Qodo daemon-review Q2:</b> this wiring no longer closes over this runtime's
    /// <see cref="_sessionId"/> — <see cref="AcpInteractionBridge.HandleAsync"/> now sources the ACP
    /// session id solely from the inbound request's OWN params. The prior shape passed
    /// <c>_sessionId ?? ""</c>, which was correct ONLY because a permission/elicitation request can
    /// normally arrive no earlier than a <c>session/prompt</c> turn, by which point
    /// <see cref="StartAsync"/>'s <c>session/new</c> has already resolved <see cref="_sessionId"/> —
    /// but <see cref="AcpConnection"/>'s read loop is started (via <see cref="RunConnectionLoopAsync"/>)
    /// BEFORE that handshake completes, so a server request arriving out of turn (a buggy or
    /// malicious agent) would have forwarded an <see cref="AcpInteractionRequest"/> with
    /// <c>AcpSessionId == ""</c>, silently breaking server-side correlation instead of failing loud
    /// or safe. Trusting the request's own params removes this whole class of bug and this runtime
    /// no longer needs to expose <see cref="_sessionId"/> to the bridge at all.
    /// </summary>
    public AcpHostedAgentRuntime(
            AcpConnection                                                                  connection,
            IAcpProcess                                                                    process,
            ILogger                                                                        logger,
            string                                                                         agentId = "",
            Func<AcpInteractionRequest, CancellationToken, Task<AcpInteractionDecision>>?   requestInteraction = null,
            TimeProvider?                                                                  timeProvider = null,
            int?                                                                           transcriptCapacity = null,
            int?                                                                           pendingTurnsCapacity = null,
            bool                                                                           debugFrames = false,
            string                                                                         vendor = "cursor",
            IAcpModelSelector?                                                             modelSelector = null,
            AcpUnattendedInteractionPolicy                                                  unattendedInteractionPolicy = AcpUnattendedInteractionPolicy.Disabled
        ) {
        _connection    = connection;
        _process       = process;
        _logger        = logger;
        _timeProvider  = timeProvider ?? TimeProvider.System;
        _agentId       = agentId;
        _debugFrames   = debugFrames;
        _vendor        = vendor;
        // LEGACY/COMPAT default (spec-review Finding 3a): null here means
        // ConfigOptionModelSelector.Instance, NOT NoOpModelSelector — every existing call site
        // that constructs this runtime directly (without a descriptor) keeps today's exact Cursor
        // model-selection behavior. The generalized path (AcpHostedAgentRuntimeFactory.StartAsync)
        // always passes descriptor.ModelSelector explicitly and never relies on this default.
        _modelSelector = modelSelector ?? ConfigOptionModelSelector.Instance;

        // Bounded, not unbounded — see the fields' own remarks for the FullMode rationale.
        // transcriptCapacity/pendingTurnsCapacity are production-null (defaults apply); tests
        // override them to exercise the drop path with a small cap instead of writing thousands of
        // envelopes.
        _transcriptCapacity  = transcriptCapacity ?? DefaultTranscriptCapacity;
        _pendingTurnsCapacity = pendingTurnsCapacity ?? DefaultPendingTurnsCapacity;

        _transcript = Channel.CreateBounded<AcpEventEnvelope>(
            new BoundedChannelOptions(_transcriptCapacity) { SingleReader = true, SingleWriter = false, FullMode = BoundedChannelFullMode.DropOldest });

        _pendingTurns = Channel.CreateBounded<PendingTurn>(
            new BoundedChannelOptions(_pendingTurnsCapacity) { SingleReader = true, SingleWriter = false, FullMode = BoundedChannelFullMode.DropWrite });

        _connection.OnNotification += HandleNotification;

        if (requestInteraction is not null) {
            _interactionBridge = new AcpInteractionBridge(
                requestInteraction,
                agentId,
                logger,
                unattendedInteractionPolicy,
                HandleUnexpectedUnattendedInteraction);
            _connection.OnServerRequest = (request, ct) => _interactionBridge.HandleAsync(request, ct);
        }
    }

    void HandleUnexpectedUnattendedInteraction(string method) {
        if (Interlocked.Exchange(ref _unexpectedUnattendedInteractionSeen, 1) != 0)
            return;

        // Do not await process termination on AcpConnection's read loop: that loop is currently
        // handling the offending request, and the child may wait for its response before exiting.
        // Reap out-of-band and cancel both runtime workers immediately.
        _cts.Cancel();
        _ = ReapUnexpectedInteractionAsync(method);
    }

    async Task ReapUnexpectedInteractionAsync(string method) {
        try {
            await _process.TerminateAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false);
        } catch (Exception ex) {
            _logger.LogDebug(ex, "ACP: failed to reap unattended reviewer after forbidden {Method} interaction.", method);
        }
    }

    public string Vendor              => _vendor;
    public int    Pid                 => _process.Pid;

    // Vendor-aware handshake/auth diagnostic labels. `_vendor` is the vendor KEY (e.g. "cursor"),
    // not the binary name — so Cursor keeps its exact prior strings (binary "cursor-agent" +
    // Team-tier hint) via an explicit branch, while any other vendor gets vendor-named generic text.
    // Kiro needs the same explicit branch as Cursor and for the same reason: its vendor key ("kiro")
    // is NOT its binary name ("kiro-cli"), so the generic fallback would tell an operator to check a
    // command that does not exist on a correct install — the least helpful possible text on exactly the
    // launch-failure path this diagnostic exists to serve.
    string DiagnosticBinary =>
        _vendor == AcpVendorDescriptors.Cursor.Vendor ? "cursor-agent"
            : _vendor == AcpVendorDescriptors.Kiro.Vendor ? "kiro-cli"
            : _vendor;

    // Kiro deliberately gets a hint that names NO command. Cursor and Copilot name a verified login
    // subcommand; for Kiro no auth requirement, auth method, or login command was ever observed
    // (`authMethods` came back empty and a prompt completed with no API key set), so inventing a
    // `kiro-cli login` would be fabricated guidance. The generic fallback is also wrong here for the
    // same reason DiagnosticBinary needed a branch: it interpolates the vendor KEY and would tell an
    // operator to authenticate `kiro`, a command absent from a correct install.
    string DiagnosticAuthHint =>
        _vendor == AcpVendorDescriptors.Cursor.Vendor
            ? "run `cursor-agent login` and verify a Team-tier subscription"
            : _vendor == AcpVendorDescriptors.Copilot.Vendor
                ? "run `copilot login` and verify GitHub Copilot access for your enterprise"
                : _vendor == AcpVendorDescriptors.Kiro.Vendor
                    ? "verify Kiro CLI is signed in and your subscription/entitlement is active"
                    : _vendor == AcpVendorDescriptors.Gemini.Vendor
                        ? GeminiAuthHint
                        : $"authenticate `{_vendor}` and verify your subscription/entitlement";

    /// <summary>
    /// Gemini's hint, worded as a POSSIBILITY rather than a diagnosis — and that is a requirement, not
    /// politeness.
    ///
    /// <para>Gemini reports a missing project as
    /// <c>IneligibleTierError: … no longer supported for Gemini Code Assist for individuals</c>, thrown by
    /// a function named <c>throwIneligibleOrProjectIdError</c> — the SAME message for a missing project id
    /// as for a genuine tier problem. That confidently-wrong attribution cost two retracted conclusions
    /// while this vendor was being specced. Reproducing the pattern here would be worse than saying
    /// nothing, so this text claims nothing: two hedges plus an explicit "or it may be unrelated",
    /// because the failure this is attached to often IS unrelated.</para>
    ///
    /// <para>The daemon-not-your-shell clause is the actual content. A supervised daemon inherits nothing
    /// from an interactive shell, so a project variable exported in a shell profile is invisible to it —
    /// which is precisely how the misdiagnosis happened.</para>
    ///
    /// <para>Kept verbatim in the exception message rather than carried as a structured field: the message
    /// is a wire shape an older server consumes, and the honesty requirement is carried by the wording.
    /// Pinned by a golden test.</para>
    /// </summary>
    internal const string GeminiAuthHint =
        "this may be an authentication or project-configuration problem, or it may be unrelated — if "
      + "hosted Gemini has not worked on this machine before, check `gemini` is logged in and that "
      + "GOOGLE_CLOUD_PROJECT (or GOOGLE_CLOUD_PROJECT_ID) is set where the DAEMON can see it (the "
      + "service unit, not your shell profile), then re-run `kcap daemon service install` and restart "
      + "the daemon";

    public bool   HasExited           => _process.HasExited;
    public int?   ExitCode            => _process.ExitCode;
    public bool   EmitsTerminalOutput => false;

    /// <summary>The ACP <c>sessionId</c> once <see cref="StartAsync"/>'s <c>session/new</c> has resolved; null before that.</summary>
    public string? SessionId => _sessionId;

    /// <summary>
    /// Reduced <c>session/update</c> notifications, in arrival order. Unbounded so a mapper that
    /// attaches slightly late (or is momentarily busy) never misses an update — the alternative
    /// (a plain event) would drop anything raised before a subscriber attaches. Every update written
    /// here is ALSO fed into <see cref="AggregateUpdate"/> to build the aggregated
    /// <see cref="Envelopes"/> transcript — the two are independent sinks of the same reduced update,
    /// not a producer/consumer pair.
    /// </summary>
    public ChannelReader<AcpSessionUpdate> Updates => _updates.Reader;

    // ── IAcpTranscriptSource ─────────────────────────────────────────────────────────────────────
    // Exposed here for the orchestrator to pick up; not wired onto HostedRuntimeStart/the
    // orchestrator here (see IAcpTranscriptSource's remarks).

    /// <inheritdoc cref="IAcpTranscriptSource.AcpSessionId"/>
    /// <remarks>Only meaningful once <see cref="StartAsync"/> has resolved <see cref="_sessionId"/> —
    /// callers (the orchestrator, post-registration) only ever see this runtime after that.</remarks>
    public string AcpSessionId => _sessionId!;

    /// <inheritdoc cref="IAcpTranscriptSource.Cwd"/>
    public string Cwd => _cwd!;

    /// <inheritdoc cref="IAcpTranscriptSource.ResolvedModel"/>
    public string? ResolvedModel => _resolvedModel;

    /// <summary>
    /// Agent capabilities negotiated by <see cref="StartAsync"/>'s <c>initialize</c> call; null
    /// before <see cref="StartAsync"/> resolves. Captured for a later reconnect path (gated on
    /// <see cref="AgentCapabilities.LoadSession"/>) — this workstream only captures and exposes it.
    /// </summary>
    public AgentCapabilities? NegotiatedCapabilities => _negotiatedCapabilities;

    /// <summary>Convenience projection of <see cref="NegotiatedCapabilities"/>'s <c>loadSession</c> flag — false before <see cref="StartAsync"/> resolves, or if the agent didn't advertise it.</summary>
    public bool SupportsLoadSession => _negotiatedCapabilities?.LoadSession ?? false;

    /// <inheritdoc cref="IAcpTranscriptSource.Envelopes"/>
    public ChannelReader<AcpEventEnvelope> Envelopes => _transcript.Reader;

    /// <summary>
    /// Performs the ACP handshake: starts the connection's read loop, then
    /// <c>initialize</c> → <c>session/new</c> (with the absolute <paramref name="cwd"/>) → an optional
    /// model-selection step — resolves <paramref name="requestedModel"/> against
    /// <c>session/new</c>'s <c>availableModels</c> and, if it matches, sends
    /// <c>session/set_config_option</c> and awaits the response BEFORE the first turn fires (see
    /// <see cref="IAcpModelSelector.TrySelectAsync"/>). If <paramref name="initialPrompt"/> is non-empty,
    /// <see cref="EnqueueTurn"/>s it onto the serialized prompt-turn worker (see
    /// <see cref="RunTurnWorkerAsync"/>) and returns as soon as the session is established — it
    /// does NOT await that prompt turn to completion. Not part of
    /// <see cref="IHostedAgentRuntime"/> — called directly by the runtime factory (and by tests)
    /// once the connection/process are constructed. A failed handshake surfaces a clear exception
    /// (never hangs): the read loop is started before any request is sent, and every request goes
    /// through <see cref="AcpConnection.RequestAsync"/>, which itself never hangs past
    /// <paramref name="ct"/> cancellation. Model selection is NEVER part of that "failed handshake"
    /// exception path — an unresolved or rejected model just falls back to the vendor's own default
    /// (see <see cref="IAcpModelSelector.TrySelectAsync"/>'s remarks).
    /// </summary>
    public async Task StartAsync(
            string cwd, string? initialPrompt, CancellationToken ct, string? requestedModel = null,
            IReadOnlyList<AcpMcpServerSpec>? mcpServers = null) {
        _cwd = cwd;

        _connectionRunTask = RunConnectionLoopAsync(_cts.Token);
        _turnWorkerTask    = RunTurnWorkerAsync(_cts.Token);

        JsonElement sessionNewResult;

        try {
            // Advertise NO client fs/terminal: cursor-agent does file/shell ops itself and never asks
            // the client to serve them (rationale: docs/ai-687-fs-terminal-capability-decision-design.md).
            // Any unadvertised request is declined -32601 by AcpConnection, never falsely acknowledged.
            var initializeParams = JsonSerializer.SerializeToElement(
                new InitializeParams(
                    ProtocolVersion: 1,
                    ClientCapabilities: new ClientCapabilities(
                        Fs: new FsCapabilities(ReadTextFile: false, WriteTextFile: false),
                        Terminal: false)),
                CapacitorJsonContext.Default.InitializeParams);

            var initializeResultElement = await _connection.RequestAsync("initialize", initializeParams, ct).ConfigureAwait(false);

            // Defensive: a malformed initialize response (wrong-typed protocolVersion, etc.) must not
            // surface as a raw JsonException. We distinguish a parse failure from a real version
            // mismatch so the error doesn't misreport a malformed response as "negotiated version 0".
            InitializeResult? initializeResult;
            try {
                initializeResult = JsonSerializer.Deserialize(initializeResultElement.GetRawText(), CapacitorJsonContext.Default.InitializeResult);
            } catch (JsonException) {
                initializeResult = null;
            }

            // This build only ever speaks version 1 — fail loud and clearly BEFORE session/new,
            // distinguishing a malformed/parse-failed response from a real version mismatch.
            if (initializeResult is null) {
                AcpMetrics.RecordFailure("handshake");
                throw new AcpProtocolVersionException(
                    $"{DiagnosticBinary}'s initialize response was malformed or omitted protocolVersion; this build supports ACP protocol version 1 — update kcap or {DiagnosticBinary}.");
            }
            if (initializeResult.ProtocolVersion != 1) {
                AcpMetrics.RecordFailure("handshake");
                throw new AcpProtocolVersionException(
                    $"{DiagnosticBinary} negotiated ACP protocol version {initializeResult.ProtocolVersion}; this build supports version 1 — update kcap or {DiagnosticBinary}.");
            }

            _negotiatedProtocolVersion = initializeResult.ProtocolVersion;

            // Missing agentCapabilities defensively means "advertises nothing" (loadSession=false),
            // not a throw — captured for a later reconnect path (only exposed here; nothing acts on
            // it yet).
            _negotiatedCapabilities = initializeResult.AgentCapabilities ?? new AgentCapabilities(LoadSession: false);

            var sessionNewParams = JsonSerializer.SerializeToElement(
                new SessionNewParams(Cwd: cwd, McpServers: mcpServers?.ToArray() ?? NoMcpServers),
                CapacitorJsonContext.Default.SessionNewParams);

            sessionNewResult = await _connection.RequestAsync("session/new", sessionNewParams, ct).ConfigureAwait(false);

            if (!sessionNewResult.TryGetProperty("sessionId", out var sessionIdElement) || sessionIdElement.GetString() is not { Length: > 0 } sessionId)
                throw new InvalidOperationException("ACP session/new response did not contain a sessionId.");

            _sessionId = sessionId;

            LogSessionStarted(_agentId, sessionId);
            AcpMetrics.SessionsStarted.Add(1);
        } catch (AcpProtocolVersionException) {
            // Already actionable and NOT an auth issue — rethrow verbatim, without the auth hint.
            // Failure already recorded above, at the point the version mismatch was detected.
            throw;
        } catch (Exception ex) when (ex is not OperationCanceledException) {
            AcpMetrics.RecordFailure("handshake");

            // Fold the original error's message into this one (single-lined + length-capped, since an
            // AcpRpcException carries the agent's arbitrary JSON-RPC error.message and this text is
            // forwarded to the server/UI via LaunchFailedAsync) and append a generic, actionable hint.
            // The full original exception is kept as InnerException, so daemon logs retain everything.
            // Deliberately conservative: the exact wire shape of a logged-out/unsubscribed cursor-agent
            // failure is unverified, so this does NOT pattern-match specific error text (a live
            // logged-out probe is a follow-up). Never masks the original error, only annotates it.
            throw new InvalidOperationException(
                $"ACP handshake (initialize/session-new) failed: {SanitizeForForward(ex.Message)} — if this is an auth/subscription issue, {DiagnosticAuthHint}.",
                ex);
        }

        // Select the requested model (if any) BEFORE the first prompt fires. Awaited, but never
        // fatal for a resolution failure — see IAcpModelSelector's cancellation-contract remarks
        // for why a canceled ct is the one exception to that (it propagates, aborting StartAsync).
        _resolvedModel = await _modelSelector
            .TrySelectAsync(_connection, _sessionId!, sessionNewResult, requestedModel, _logger, ct)
            .ConfigureAwait(false);

        // Handshake is now fully complete (initialize + session/new + best-effort model selection) —
        // one consolidated Info log carrying the negotiated protocol version, loadSession, and the
        // resolved model (null if none was requested/matched).
        LogHandshakeOk(_agentId, _negotiatedProtocolVersion, _negotiatedCapabilities.LoadSession, _resolvedModel);

        // The session is established (initialize + session/new both completed) — the caller
        // (orchestrator) can now treat this agent as live. Enqueue the initial turn without
        // awaiting it: a real ACP turn can run arbitrarily long, and blocking StartAsync on it would
        // delay agent registration/stoppability for the whole turn. Completion is
        // observed via the Updates/Envelopes channels, not this method's return.
        if (!string.IsNullOrEmpty(initialPrompt))
            _ = EnqueueTurn(initialPrompt, acknowledgeWrite: false);
    }

    /// <summary>
    /// Enqueues a prompt-turn's text onto <see cref="_pendingTurns"/> and returns immediately — never
    /// blocks on, or observes, that turn's completion. Used by both
    /// <see cref="StartAsync"/> (initial prompt) and <see cref="SendUserInputAsync"/> (follow-up
    /// turns), preserving a non-blocking contract for both callers. The single
    /// <see cref="RunTurnWorkerAsync"/> worker drains this queue strictly in order; turn completion is
    /// observed via <see cref="Updates"/>/<see cref="Envelopes"/>, not this method's return.
    ///
    /// <see cref="_pendingTurns"/> is bounded (<see cref="BoundedChannelFullMode.DropWrite"/>) —
    /// checked explicitly BEFORE writing so a full queue logs a clear warning (with a running
    /// dropped-count) rather than silently discarding the input via the channel's own drop-write
    /// behavior.
    /// </summary>
    Task EnqueueTurn(string text, bool acknowledgeWrite) {
        var written = acknowledgeWrite
            ? new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously)
            : null;
        if (_pendingTurns.Reader.Count >= _pendingTurnsCapacity) {
            var dropped = Interlocked.Increment(ref _droppedPendingTurns);
            _logger.LogWarning(
                "ACP: pending-turns queue full (capacity={Capacity}) — dropping this input; {DroppedCount} dropped this session so far (the turn worker is likely stuck on a stalled turn).",
                _pendingTurnsCapacity, dropped);

            written?.TrySetException(new InvalidOperationException("ACP pending-turns queue is full."));
            return written?.Task ?? Task.CompletedTask;
        }

        if (!_pendingTurns.Writer.TryWrite(new PendingTurn(text, written))) {
            _logger.LogDebug("ACP: dropped a prompt turn — pending-turns channel already completed.");
            written?.TrySetException(new ObjectDisposedException(nameof(AcpHostedAgentRuntime)));
        }
        return written?.Task ?? Task.CompletedTask;
    }

    /// <summary>
    /// The single, long-running prompt-turn worker. Drains <see cref="_pendingTurns"/> strictly FIFO,
    /// processing exactly one turn (<see cref="ProcessTurnAsync"/>) fully to completion before
    /// starting the next — this single-flight serialization is what makes "the aggregation buffer
    /// unambiguously belongs to the active turn" true. Cancellable: <c>ChannelReader.ReadAllAsync(ct)</c> observes
    /// <paramref name="ct"/> both between turns and (via <see cref="ProcessTurnAsync"/>'s own use of
    /// <paramref name="ct"/> in <see cref="SendPromptAsync"/>) inside an in-flight turn, so a turn
    /// whose <c>stopReason</c> never arrives cannot pin <see cref="DisposeAsync"/>.
    /// </summary>
    async Task RunTurnWorkerAsync(CancellationToken ct) {
        try {
            await foreach (var turn in _pendingTurns.Reader.ReadAllAsync(ct).ConfigureAwait(false))
                await ProcessTurnAsync(turn, ct).ConfigureAwait(false);
        } catch (OperationCanceledException) {
            // normal shutdown — see this method's remarks.
        } catch (Exception ex) {
            _logger.LogDebug(ex, "ACP: prompt-turn worker ended unexpectedly.");
        }
    }

    /// <summary>
    /// Processes exactly one serialized prompt turn: (a) emits this turn's <c>UserMessage</c>
    /// envelope (written directly, not through the aggregation lock — it happens-before any update
    /// for this turn can possibly arrive, since it is written before <c>session/prompt</c> is even
    /// sent); (b) sends <c>session/prompt</c> and awaits its <c>stopReason</c> response (reusing
    /// <see cref="SendPromptAsync"/>); (c) performs this turn's end-of-turn flush of the aggregation
    /// buffer in a <c>finally</c> — this runs whether the turn completed normally, faulted (logged,
    /// non-fatal), or was cancelled (a courtesy flush of whatever partial text had accumulated; see
    /// <see cref="_aggregationLock"/>'s remarks on why
    /// this can never hang <see cref="DisposeAsync"/> — flushing is a pure in-memory operation, never
    /// I/O). A cancellation still propagates out of this method (the <c>when</c> filter below only
    /// catches non-cancellation faults) so <see cref="RunTurnWorkerAsync"/>'s loop stops promptly.
    /// </summary>
    async Task ProcessTurnAsync(PendingTurn turn, CancellationToken ct) {
        await _turnExecutionGate.WaitAsync(ct).ConfigureAwait(false);
        try {
            EmitEnvelope(AcpEventTranslator.BuildUserMessage(seq: 0, NowIso(), turn.Text));

            try {
                await SendPromptAsync(turn.Text, turn.Written, ct).ConfigureAwait(false);
            } catch (Exception ex) when (ex is not OperationCanceledException) {
                turn.Written?.TrySetException(ex);
                _logger.LogDebug(ex, "ACP: session/prompt turn faulted; flushing this turn's partial buffer.");
            } finally {
                FlushOpenRun();
            }
        } finally {
            _turnExecutionGate.Release();
        }
    }

    async Task RunConnectionLoopAsync(CancellationToken ct) {
        try {
            await _connection.RunAsync(ct).ConfigureAwait(false);
        } catch (OperationCanceledException) {
            // normal shutdown
        } catch (Exception ex) {
            _logger.LogDebug(ex, "ACP connection read loop ended unexpectedly.");
        } finally {
            _updates.Writer.TryComplete();
        }
    }

    /// <summary>
    /// Never yields a byte — ACP stdout is protocol traffic, never terminal output (no terminal
    /// capability yet). Crucially, this must NOT complete until the process exits or
    /// <paramref name="ct"/> cancels (Fix B/E): <see cref="AgentOrchestrator.ReadAgentOutputAsync"/>
    /// treats the enumerable ending as "the agent's output stream ended" and finalizes the agent —
    /// for a PTY that's a real signal (the CLI exited), but the old implementation here
    /// (<c>yield break</c> on the first call) made a LIVE ACP agent look like it had already
    /// finished, so the orchestrator immediately finalized it as failed. Staying open for the
    /// process's whole lifetime means the orchestrator's read loop parks harmlessly (yielding
    /// nothing) instead of ending prematurely; <see cref="IHostedAgentRuntime.EmitsTerminalOutput"/>
    /// tells the orchestrator not to use this stream for the Starting→Running/startup-failure
    /// signals it uses for PTY runtimes.
    /// </summary>
    public async IAsyncEnumerable<byte[]> ReadOutputAsync([EnumeratorCancellation] CancellationToken ct = default) {
        var exitTask = _process.WaitForExitAsync(); // completes on process exit (AcpChildProcess swallows faults)
        var ctTcs    = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        await using var reg = ct.Register(() => ctTcs.TrySetResult());

        await Task.WhenAny(exitTask, ctTcs.Task).ConfigureAwait(false);

        yield break;
    }

    /// <summary>
    /// Sends a follow-up <c>session/prompt</c> for hosted-UI text input (server <c>SendInput</c>).
    /// Returns as soon as the text is enqueued (see <see cref="EnqueueTurn"/>) — it does NOT await the
    /// turn's <c>stopReason</c> response: a real turn can run arbitrarily long, and the
    /// pre-fix behavior (awaiting the full round trip) blocked this call — and therefore the
    /// orchestrator's <c>HandleSendInput</c> — for the whole turn. If a prior turn is still in
    /// flight, this text is queued FIFO and the worker sends it only once that turn's own
    /// <c>stopReason</c> has been received and its buffer flushed — turn completion is
    /// observed via <see cref="Updates"/>/<see cref="Envelopes"/>, not this method's return.
    /// </summary>
    public Task SendUserInputAsync(string text) {
        RequireSessionId();
        return EnqueueTurn(text, acknowledgeWrite: false);
    }

    public Task SendUserInputAndWaitForWriteAsync(string text) {
        RequireSessionId();
        return EnqueueTurn(text, acknowledgeWrite: true);
    }

    public async Task WaitForTurnIdleAsync(CancellationToken ct) {
        await _turnExecutionGate.WaitAsync(ct).ConfigureAwait(false);
        _turnExecutionGate.Release();
    }

    async Task SendPromptAsync(string text, TaskCompletionSource? written, CancellationToken ct) {
        var promptParams = JsonSerializer.SerializeToElement(
            new SessionPromptParams(
                SessionId: _sessionId!,
                Prompt: [new PromptContentBlock(Type: "text", Text: text)]),
            CapacitorJsonContext.Default.SessionPromptParams);

        await _connection.RequestAsync(
            "session/prompt", promptParams, ct, () => written?.TrySetResult()).ConfigureAwait(false);
    }

    sealed record PendingTurn(string Text, TaskCompletionSource? Written);

    public Task SendSpecialKeyAsync(string key) {
        // ACP has no special-key channel — best-effort no-op.
        _logger.LogDebug("ACP runtime ignoring SendSpecialKeyAsync({Key}) — no special-key surface in ACP.", key);
        return Task.CompletedTask;
    }

    public Task SendRawInputAsync(byte[] data) =>
        throw new NotSupportedException("Local-attach raw input is a PTY-only surface; the ACP runtime has no equivalent channel.");

    public void Resize(ushort cols, ushort rows) {
        // No terminal capability until — no-op.
    }

    public async Task RequestGracefulStopAsync() {
        if (_sessionId is not { Length: > 0 } sessionId) {
            _logger.LogDebug("ACP runtime RequestGracefulStopAsync called before a session was established; nothing to cancel.");
            return;
        }

        var cancelParams = JsonSerializer.SerializeToElement(
            new SessionCancelParams(SessionId: sessionId),
            CapacitorJsonContext.Default.SessionCancelParams);

        await _connection.NotifyAsync("session/cancel", cancelParams).ConfigureAwait(false);
    }

    public Task WaitForExitAsync(TimeSpan? timeout = null) => _process.WaitForExitAsync(timeout);
    public Task TerminateAsync(TimeSpan?   timeout = null) => _process.TerminateAsync(timeout);

    void HandleNotification(AcpNotification notification) {
        if (notification.Method != "session/update")
            return;

        if (notification.Params is not { } @params || !@params.TryGetProperty("update", out var updateElement)) {
            _logger.LogDebug("ACP: session/update notification missing 'update' object; skipping.");
            return;
        }

        var reduced = Reduce(updateElement.Clone());
        if (!_updates.Writer.TryWrite(reduced))
            _logger.LogDebug("ACP: dropped a session/update — updates channel already completed.");

        // Fed synchronously, on THIS notification callback's own thread — AcpConnection.RunAsync's
        // single read loop calls HandleNotification directly (never concurrently with itself), so
        // every update this runtime ever sees is aggregated in strict arrival order without needing
        // its own queue/consumer loop.
        AggregateUpdate(reduced);
    }

    static AcpSessionUpdate Reduce(JsonElement update) {
        var kindText = update.TryGetProperty("sessionUpdate", out var kindEl) ? kindEl.GetString() : null;

        return kindText switch {
            "agent_message_chunk" => new AcpSessionUpdate(
                AcpUpdateKind.AgentMessageChunk,
                Text: ExtractContentText(update),
                Raw: update),

            "agent_thought_chunk" => new AcpSessionUpdate(
                AcpUpdateKind.AgentThoughtChunk,
                Text: ExtractContentText(update),
                Raw: update),

            "tool_call" => new AcpSessionUpdate(
                AcpUpdateKind.ToolCall,
                ToolCallId: GetStringOrNull(update, "toolCallId"),
                ToolTitle: GetStringOrNull(update, "title"),
                ToolKind: GetStringOrNull(update, "kind"),
                ToolStatus: GetStringOrNull(update, "status"),
                ToolInputJson: GetRawTextOrNull(update, "rawInput"),
                Raw: update),

            "tool_call_update" => new AcpSessionUpdate(
                AcpUpdateKind.ToolCallUpdate,
                ToolCallId: GetStringOrNull(update, "toolCallId"),
                ToolStatus: GetStringOrNull(update, "status"),
                ToolResultText: ExtractToolResultText(update),
                ToolIsError: GetStringOrNull(update, "status") == "failed",
                Raw: update),

            "plan" => new AcpSessionUpdate(AcpUpdateKind.Plan, Raw: update),

            "available_commands_update" => new AcpSessionUpdate(AcpUpdateKind.AvailableCommands, Raw: update),

            "session_info_update" => new AcpSessionUpdate(
                AcpUpdateKind.SessionInfo,
                Title: GetStringOrNull(update, "title"),
                Raw: update),

            // The ACP spec's Session Usage RFD. No vendor implements it yet, so this arm is
            // dormant; it exists so the context chip lights up for every ACP-hosted vendor the
            // moment any of them ships it. Content-free unless both fields are present and
            // sane - a partial reading is worse than none, since `size` is the chip's
            // denominator. `used > size` is kept: reported windows can be advisory. A zero
            // `used` is dropped to match the server's re-validation, where it would be
            // durably appended yet invisible to every consumer (the peak comparison they all
            // share ignores a zero candidate). Any `cost` or unknown siblings are ignored.
            "usage_update" => update.Num("used") is { } used and > 0 && update.Num("size") is { } size and > 0
                ? new AcpSessionUpdate(
                    AcpUpdateKind.UsageUpdate,
                    ContextUsedTokens: used,
                    ContextWindowTokens: size,
                    Raw: update)
                : new AcpSessionUpdate(AcpUpdateKind.Unknown, Raw: update),

            _ => new AcpSessionUpdate(AcpUpdateKind.Unknown, Raw: update),
        };
    }

    static string? ExtractContentText(JsonElement update) =>
        update.TryGetProperty("content", out var content) && content.TryGetProperty("text", out var textEl)
            ? textEl.GetString()
            : null;

    static string? GetStringOrNull(JsonElement element, string propertyName) =>
        element.TryGetProperty(propertyName, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null; // guard the ValueKind: GetString() THROWS on a non-string value (number/object/
                    // array), which would let a schema-drift frame bubble an exception up and skip
                    // the entire notification. A wrong-typed field is treated as absent instead.

    /// <summary>
    /// Verbatim JSON text of <paramref name="propertyName"/> when it's a JSON object (e.g. a
    /// <c>tool_call</c>'s <c>rawInput</c>), else <see langword="null"/> — used to populate
    /// <see cref="AcpSessionUpdate.ToolInputJson"/> without re-serializing/reshaping the tool's input
    /// args (the mapper on the server side parses this raw text itself; see
    /// <c>AcpSessionMapper.BuildToolCall</c>).
    /// </summary>
    static string? GetRawTextOrNull(JsonElement element, string propertyName) =>
        element.Obj(propertyName)?.GetRawText();

    /// <summary>
    /// Extracts a tool_call_update's RESULT text for <see cref="AcpSessionUpdate.ToolResultText"/>,
    /// mechanically and regardless of <c>status</c> (the terminal-status gate lives in
    /// <c>AcpEventTranslator</c>, not here). Prefers the ACP-spec <c>content</c> array's text-block
    /// shape (<c>ToolCallContent</c>: <c>{type:"content", content:{type:"text", text:"..."}}</c>) —
    /// concatenating every such block found (newline-joined); non-text content variants
    /// (<c>diff</c>/<c>terminal</c>) are not extracted here, degrading to "no result text from this
    /// block" rather than throwing. Falls back to the verbatim <c>rawOutput</c> JSON text when no
    /// text content block is present. Returns <see langword="null"/> when neither is
    /// present/extractable, so <c>AcpEventTranslator.Translate</c> never emits an empty
    /// <c>ToolResultReceived</c>. This shape is defensive/spec-derived, not yet probe-confirmed
    /// against real Cursor output (see docs/acp-probe-findings.md).
    /// </summary>
    static string? ExtractToolResultText(JsonElement update) {
        if (update.Arr("content") is { } contentEl) {
            List<string>? texts = null;

            foreach (var block in contentEl.EnumerateArray()) {
                if (block.Str("type") != "content") continue;
                if (block.Obj("content") is not { } inner) continue;
                if (inner.Str("text") is not { } text) continue;

                (texts ??= []).Add(text);
            }

            if (texts is { Count: > 0 })
                return string.Join("\n", texts);
        }

        return GetRawTextOrNull(update, "rawOutput");
    }

    // ── Chunk aggregation ────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Aggregates ONE reduced update: a same-kind
    /// <see cref="AcpUpdateKind.AgentMessageChunk"/>/<see cref="AcpUpdateKind.AgentThoughtChunk"/> run
    /// grows the open buffer; any other kind (or a kind transition between message/thought) flushes
    /// the open run first, then — for the non-aggregated kinds — translates <paramref name="update"/>
    /// 1:1 and emits it if non-null (tool_call/tool_call_update/plan/available_commands/unknown all
    /// take this path). Entirely under <see cref="_aggregationLock"/> so the
    /// check-open-run-kind-then-append-or-flush decision is atomic against a concurrent turn-end
    /// flush from the worker (see the lock field's remarks for why that matters even though the two
    /// call sites are serialized in practice).
    /// </summary>
    void AggregateUpdate(AcpSessionUpdate update) {
        lock (_aggregationLock) {
            switch (update.Kind) {
                case AcpUpdateKind.AgentMessageChunk:
                case AcpUpdateKind.AgentThoughtChunk:
                    if (_openRunKind == update.Kind) {
                        _openRunText!.Append(update.Text);
                    } else {
                        FlushOpenRunLocked();
                        _openRunKind = update.Kind;
                        _openRunText = new StringBuilder(update.Text ?? "");
                    }
                    break;

                case AcpUpdateKind.SessionInfo:
                case AcpUpdateKind.UsageUpdate:
                    // Neither is transcript content, so neither must close an open chunk run. One
                    // interleaved between two message/thought chunks would otherwise flush the run
                    // mid-stream and split one contiguous assistant message into two envelopes.
                    // Emit standalone, leaving _openRunKind/_openRunText untouched so the
                    // surrounding chunks still aggregate into one run — both are orderless
                    // metadata, so their relative seq is immaterial.
                    var metaEnvelope = AcpEventTranslator.Translate(
                        update, seq: 0, NowIso(), logger: _logger, debugFrames: _debugFrames,
                        resolvedModel: _resolvedModel);
                    if (metaEnvelope is { } m)
                        EmitEnvelope(m);
                    break;

                default:
                    FlushOpenRunLocked(); // kind-transition — the open run (if any) ends here
                    var envelope = AcpEventTranslator.Translate(update, seq: 0, NowIso(), logger: _logger, debugFrames: _debugFrames);
                    if (envelope is { } e)
                        EmitEnvelope(e);
                    break;
            }
        }
    }

    /// <summary>
    /// Turn-end / session-end flush entry point: flushes the open aggregation run, if any.
    /// Called by <see cref="ProcessTurnAsync"/> on its turn's <c>stopReason</c>/fault/cancellation,
    /// and defensively by <see cref="DisposeAsync"/> as a session-end safety net. Idempotent — a
    /// second call with no open run is a no-op.
    /// </summary>
    void FlushOpenRun() {
        lock (_aggregationLock) FlushOpenRunLocked();
    }

    /// <summary>
    /// Flushes the open run — MUST be called while already holding <see cref="_aggregationLock"/>.
    /// Builds a representative <see cref="AcpSessionUpdate"/> carrying only the run's
    /// <see cref="AcpUpdateKind"/> (the translator only needs the kind to pick
    /// <see cref="AcpEventKind.AssistantText"/> vs <see cref="AcpEventKind.AssistantThinking"/> when
    /// <c>aggregatedText</c> is supplied — see <c>AcpEventTranslator.Translate</c>'s remarks) and
    /// translates it with the accumulated buffer as <c>aggregatedText</c>, emitting exactly ONE
    /// envelope for the whole run.
    /// </summary>
    void FlushOpenRunLocked() {
        if (_openRunKind is not { } kind)
            return;

        var text = _openRunText!.ToString();
        _openRunKind = null;
        _openRunText = null;

        var representative = new AcpSessionUpdate(kind);
        var envelope        = AcpEventTranslator.Translate(representative, seq: 0, NowIso(), aggregatedText: text, logger: _logger, debugFrames: _debugFrames);
        if (envelope is { } e)
            EmitEnvelope(e);
    }

    /// <summary>
    /// The ONLY call site that writes to <see cref="_transcript"/> — always under
    /// <see cref="_aggregationLock"/> (reentrant, so callers already holding it from
    /// <see cref="AggregateUpdate"/>/<see cref="FlushOpenRunLocked"/> do not deadlock) so envelope
    /// order on the channel matches lock-acquisition order across every writer.
    ///
    /// <see cref="_transcript"/> is bounded (<see cref="BoundedChannelFullMode.DropOldest"/>) —
    /// checked explicitly BEFORE writing (under
    /// the same lock, so no other writer can race this check) so a full channel logs a clear warning
    /// (with a running dropped-count) at the exact write that triggers the eviction, rather than
    /// relying on <c>TryWrite</c>'s return value, which is <see langword="true"/> for BOTH a normal
    /// write and a drop-and-evict write under this FullMode — it cannot distinguish the two.
    /// </summary>
    void EmitEnvelope(AcpEventEnvelope envelope) {
        lock (_aggregationLock) {
            if (_transcript.Reader.Count >= _transcriptCapacity) {
                var dropped = Interlocked.Increment(ref _droppedTranscriptEnvelopes);
                _logger.LogWarning(
                    "ACP: transcript channel full (capacity={Capacity}) — dropping the oldest buffered envelope to make room for Kind={Kind}; {DroppedCount} dropped this session so far (the forwarder is likely stalled).",
                    _transcriptCapacity, envelope.Kind, dropped);
            }

            if (!_transcript.Writer.TryWrite(envelope))
                _logger.LogDebug("ACP: dropped an ACP transcript envelope (Kind={Kind}) — transcript channel already completed.", envelope.Kind);
        }
    }

    /// <summary>
    /// A real timestamp for every envelope this runtime emits (Seq itself stays a <c>0</c> placeholder
    /// — the forwarder assigns the real monotonic seq on dequeue). Uses <see cref="_timeProvider"/>
    /// (defaults to <see cref="TimeProvider.System"/>, overridable in tests for determinism) rather
    /// than <see cref="DateTimeOffset.UtcNow"/> directly.
    /// </summary>
    string NowIso() => _timeProvider.GetUtcNow().ToString("O");

    void RequireSessionId() {
        if (_sessionId is not { Length: > 0 })
            throw new InvalidOperationException("AcpHostedAgentRuntime.SendUserInputAsync called before StartAsync established a session.");
    }

    public async ValueTask DisposeAsync() {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return;

        // Only emit the session-ended lifecycle event when a session actually started — a startup
        // failure (bad protocol version / handshake / session/new) disposes the runtime before
        // _sessionId is assigned, and pairing "ended" with a session that never started would make
        // the lifecycle telemetry incoherent.
        if (_sessionId is { Length: > 0 } endedSessionId)
            LogSessionEnded(_agentId, endedSessionId);

        _connection.OnNotification -= HandleNotification;

        await _cts.CancelAsync().ConfigureAwait(false);
        _updates.Writer.TryComplete();
        _pendingTurns.Writer.TryComplete();

        // The turn worker's in-flight SendPromptAsync await is keyed off _cts.Token via
        // AcpConnection.RequestAsync's own cancellation registration, so cancelling _cts above
        // already unblocks it — ProcessTurnAsync's own `finally` still runs a courtesy flush of that
        // turn's partial buffer (see FlushOpenRun) before the worker loop observes the cancellation
        // and returns. This is just a bounded wait for that to actually happen.
        try {
            await _turnWorkerTask.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
        } catch {
            // Best-effort — a stuck turn worker must never hang dispose.
        }

        // Session-end flush: a belt-and-suspenders flush of any still-open aggregation run. In the
        // normal shutdown path ProcessTurnAsync's own finally already flushed the active turn's
        // buffer above, making this a no-op — it only matters for the (currently unreachable in
        // practice) case where the worker task itself never ran a turn to begin with.
        FlushOpenRun();
        _transcript.Writer.TryComplete();

        try {
            await _connectionRunTask.ConfigureAwait(false);
        } catch (OperationCanceledException) {
            // expected shutdown path
        }

        _cts.Dispose();

        await _connection.DisposeAsync().ConfigureAwait(false);
        await _process.DisposeAsync().ConfigureAwait(false);
    }

    // ── LoggerMessage source-generated methods ──────────────────────────────────────────────────
    // Payload-free by construction: ids, protocol/capability metadata, and the resolved model NAME
    // only — never prompt/assistant/tool content.

    [LoggerMessage(Level = LogLevel.Information, Message = "ACP session started: agentId={AgentId} acpSessionId={AcpSessionId}")]
    partial void LogSessionStarted(string agentId, string acpSessionId);

    [LoggerMessage(Level = LogLevel.Information, Message = "ACP handshake OK: agentId={AgentId} protocolVersion={ProtocolVersion} loadSession={LoadSession} resolvedModel={ResolvedModel}")]
    partial void LogHandshakeOk(string agentId, int protocolVersion, bool loadSession, string? resolvedModel);

    [LoggerMessage(Level = LogLevel.Information, Message = "ACP hosted agent session ended: agentId={AgentId} acpSessionId={AcpSessionId}")]
    partial void LogSessionEnded(string agentId, string acpSessionId);
}
