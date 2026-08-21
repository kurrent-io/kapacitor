using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Threading.Channels;
using Capacitor.Cli.Core;
using Capacitor.Cli.Daemon.Harness.Antigravity;
using Capacitor.Cli.Daemon.Services;
using Microsoft.Extensions.Logging;

namespace Capacitor.Cli.Daemon.Harness.Pi;

/// <summary>
/// <see cref="IHostedAgentRuntime"/> for Pi's CLI (<c>pi --mode rpc</c>) as an interactive hosted
/// agent, speaking Pi's LF-framed JSONL-RPC over stdio (see <see cref="PiRpc"/> for the pure
/// protocol layer).
///
/// <para><b>Why this is much simpler than <see cref="AntigravityHostedAgentRuntime"/>.</b> agy has
/// no process between turns, which is why it needs an explicit phase machine to answer "is this
/// runtime alive". Pi's child is LONG-LIVED — ONE <c>pi</c> process backs the whole hosted session,
/// its stdin stays open for that session's whole life — so liveness is REAL:
/// <see cref="HasExited"/>/<see cref="ExitCode"/>/<see cref="Pid"/> all delegate straight to the
/// process, and there is no phase to keep in sync with it. What this class owns instead is the
/// stdio protocol: one read pump, command/response correlation by <c>id</c>, and the transcript
/// translation.</para>
///
/// <para><b>(a) The ready barrier is the load-bearing invariant.</b> The constructor sends
/// <c>get_state</c> (id <see cref="InitStateCommandId"/>) and
/// <see cref="WaitForSessionReadyAsync"/> completes when its response resolves
/// <see cref="AcpSessionId"/> and <see cref="ResolvedModel"/>. A factory MUST await it before
/// returning control to the orchestrator, which reads <see cref="AcpSessionId"/> synchronously the
/// moment a launch returns — binding a transcript to <c>""</c> is a silent, permanent correlation
/// break, not a flaky race (the same rule <see cref="AntigravityHostedAgentRuntime"/>'s rule (e)
/// documents at length). The barrier is only usable because it resolves on EVERY path: the
/// response arrives (success), the response says <c>success:false</c> or carries no
/// <c>sessionId</c> (fault — fail closed rather than bind <c>""</c>), the child exits or its stdout
/// EOFs (fault, via <see cref="EnterTerminal"/>), the write itself fails (fault), or the deadline
/// elapses (fault). A hanging barrier wedges the launch path; a faulted one merely fails a launch.</para>
///
/// <para>That last path is why the deadline is NOT optional. A caller may override it, but it
/// always applies — <see cref="DefaultReadyDeadline"/> backs an omitted one. Every other path
/// needs the child to DO something (answer, refuse, or die); an alive-but-silent child — spawned,
/// hung before its first read, never exiting — does none of them, so a null deadline would leave
/// that one shape with no resolver at all. A launch path that hangs only when a callsite forgot to
/// pass a timeout is the exact failure this barrier exists to prevent, arriving through the
/// barrier's own configuration.</para>
///
/// <para><b>(b) <see cref="ReadOutputAsync"/> parks on one signal, created once.</b> Same rule as
/// Antigravity's (c): the orchestrator drives <c>FinalizeAgentRunAsync</c> from that stream's
/// <c>await foreach</c> ending, so <see cref="_terminalTcs"/> is a single constructor-owned
/// <see cref="TaskCompletionSource"/> completed exactly once, on entry to terminal — never a
/// per-turn signal. Pi's stdout is protocol traffic, never terminal bytes, so
/// <see cref="EmitsTerminalOutput"/> is <see langword="false"/> and this stream yields no bytes at
/// all.</para>
///
/// <para><b>(c) The user-echo dedupe.</b> <see cref="SendUserInputAsync"/> emits the
/// <c>user_message</c> envelope itself, at send time, so the viewer sees their message immediately
/// rather than only once Pi echoes it back. Pi then ALSO reports that same text as a user-role
/// <c>message_end</c>, so without a dedupe every hosted message renders twice. Sent prompts are
/// remembered in a bounded (<see cref="SentPromptMemory"/>), most-recent-first, CONSUME-on-match
/// list, and the match is applied BEFORE the envelope reaches the channel. Consume-on-match (rather
/// than a contains-check) is what keeps a genuinely repeated message renderable: sending "again"
/// twice must show two user messages, and a non-consuming filter would swallow the second
/// forever.</para>
///
/// <para><b>Never emits <c>session_ended</c></b> — the server's <c>EndAgentSession</c> owns that
/// transition, exactly as for every other <see cref="IAcpTranscriptSource"/> runtime.</para>
/// </summary>
internal sealed class PiRpcHostedAgentRuntime : IHostedAgentRuntime, IAcpTranscriptSource {
    /// <summary>The correlation id of the constructor's handshake <c>get_state</c> — fixed rather
    /// than sequenced so a test (and a log reader) can name it.</summary>
    internal const string InitStateCommandId = "init-state";

    const int DefaultTranscriptCapacity = 2000;

    /// <summary>How many recently-sent prompts are held for echo matching. Bounded because the
    /// list only exists to absorb Pi's echo of a message we JUST sent — an unmatched entry is
    /// evidence Pi never echoed it, not something to keep forever. Small enough that a session's
    /// whole history never accumulates here, large enough to cover a burst of queued input that
    /// Pi echoes only when it finishes the current turn.</summary>
    const int SentPromptMemory = 16;

    static readonly TimeSpan DefaultStopGrace = TimeSpan.FromSeconds(3);

    /// <summary>Backs an omitted <c>readyDeadline</c> — see rule (a) on why the deadline is never
    /// allowed to be absent. Generous rather than tight: it bounds a PATHOLOGY (a child that
    /// spawned but never speaks), not normal startup, and a healthy <c>pi</c> answers
    /// <c>get_state</c> in milliseconds. The cost of it being too long is a slow failed launch; the
    /// cost of it being too short is a launch that fails on a cold, loaded machine that would have
    /// succeeded.</summary>
    internal static readonly TimeSpan DefaultReadyDeadline = TimeSpan.FromSeconds(30);

    readonly IPiRpcProcess _process;
    readonly ILogger       _logger;
    readonly string        _agentId;
    readonly string?       _requestedModel;
    readonly string        _cwd;
    readonly TimeSpan      _readyDeadline;
    readonly TimeSpan      _stopGrace;
    readonly Action?       _onDisposed;

    readonly Channel<AcpEventEnvelope> _transcript;

    /// <summary>Cancelled by <see cref="TerminateAsync"/>/<see cref="DisposeAsync"/>; the pump's
    /// read token and every command write rides it.
    ///
    /// <para>Deliberately NEVER disposed. Its token is handed to <see cref="IPiRpcProcess"/> calls
    /// that can still be in flight on other threads when disposal runs (a viewer's
    /// <see cref="SendUserInputAsync"/> racing a stop), and both reading <c>.Token</c> and
    /// registering on it throw once the source is disposed — turning a benign teardown race into a
    /// thrown <see cref="ObjectDisposedException"/> from a caller that did nothing wrong. It owns
    /// no timer and no unmanaged state, so leaving it undisposed costs nothing; the same reasoning
    /// <see cref="PiRpcProcess"/> applies to its stdin semaphore.</para></summary>
    readonly CancellationTokenSource _ownerCts = new();

    /// <summary>Captured once, in the constructor — see <see cref="_ownerCts"/>. Reading a
    /// previously-captured token is safe regardless of what happens to its source.</summary>
    readonly CancellationToken _ownerToken;

    /// <summary>Completed exactly once, on entry to terminal — see rule (b).</summary>
    readonly TaskCompletionSource _terminalTcs = new(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <summary>Rule (a)'s barrier. Resolved by a good <c>get_state</c> response; faulted on every
    /// other ending.</summary>
    readonly TaskCompletionSource _sessionReady = new(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <summary>In-flight commands awaiting their correlated <c>response</c> frame, keyed by the
    /// <c>id</c> this runtime stamped on them. Faulted wholesale by <see cref="EnterTerminal"/> —
    /// a response that can never arrive must never leave a waiter parked.
    ///
    /// <para><b>Bound:</b> one entry per in-flight user message, released on its response or at
    /// terminal — so this is bounded by USER INPUT RATE, not by wire traffic. Pi's event stream can
    /// be arbitrarily chatty without adding a single entry here, since only commands this runtime
    /// SENDS are ever registered.</para></summary>
    readonly ConcurrentDictionary<string, TaskCompletionSource<PiRpcFrame>> _pending = new();

    readonly Lock         _echoGate    = new();
    readonly List<string> _sentPrompts = [];

    readonly Lock                         _turnGate    = new();
    readonly List<TaskCompletionSource>   _idleWaiters = [];
    bool                                  _busy;

    readonly Task _pumpTask;
    readonly Task _handshakeTask;

    int _commandSeq;
    int _terminal;
    int _disposed;

    /// <summary>Resolved from the handshake and never changed after. <see langword="volatile"/>
    /// because the pump thread writes it and <see cref="AcpSessionId"/> is read cross-thread (the
    /// orchestrator reads it the instant a launch returns).</summary>
    volatile string? _sessionId;

    /// <summary>See <see cref="_sessionId"/> — same cross-thread reasoning. Null means the
    /// handshake's <c>get_state</c> did not carry a model (see <see cref="ExtractModelId"/>) — NEVER
    /// backfilled with <see cref="_requestedModel"/>, since <see cref="ResolvedModel"/> is read as a
    /// CONFIRMED-applied-model signal, and a requested-but-unconfirmed model would be an attribution
    /// lie.</summary>
    volatile string? _resolvedModel;

    /// <summary>Liveness-supervision clock, assigned by the factory BEFORE the first input. Every
    /// stamp is a no-op guard rather than a throw so a direct construction (tests, a caller that
    /// does not care about liveness) keeps working — which is exactly why a clock assigned LATE
    /// fails silently.</summary>
    internal AgentActivityClock? ActivityClock { get; set; }

    public PiRpcHostedAgentRuntime(
            IPiRpcProcess process,
            ILogger       logger,
            string        agentId,
            string?       requestedModel,
            string        cwd,
            TimeSpan?     readyDeadline = null,
            TimeSpan?     stopGrace     = null,
            Action?       onDisposed    = null) {
        _process        = process;
        _logger         = logger;
        _agentId        = agentId;
        _requestedModel = requestedModel;
        _cwd            = cwd;
        _readyDeadline  = readyDeadline ?? DefaultReadyDeadline;   // never absent — rule (a)
        _stopGrace      = stopGrace ?? DefaultStopGrace;
        _onDisposed     = onDisposed;
        _ownerToken     = _ownerCts.Token;

        // DropOldest with SingleWriter=false: the pump is the only writer that matters for
        // ordering, but EnterTerminal's TryComplete and a caller's own send-time user_message emit
        // both race it. Same shape as AntigravityHostedAgentRuntime's transcript channel.
        _transcript = Channel.CreateBounded<AcpEventEnvelope>(
            new BoundedChannelOptions(DefaultTranscriptCapacity)
                { SingleReader = true, SingleWriter = false, FullMode = BoundedChannelFullMode.DropOldest });

        // The pump starts FIRST: the handshake's response can only be observed by a running pump,
        // and Pi may answer before WriteLineAsync's continuation even resumes.
        _pumpTask      = RunPumpAsync();
        _handshakeTask = RunHandshakeAsync();
    }

    public string Vendor              => "pi";
    public int    Pid                 => _process.Pid;
    public bool   HasExited           => _process.HasExited;
    public int?   ExitCode            => _process.ExitCode;
    public bool   EmitsTerminalOutput => false;

    public string  AcpSessionId  => _sessionId ?? "";
    public string  Cwd           => _cwd;

    /// <summary>The CONFIRMED-applied model, per the <see cref="IAcpTranscriptSource"/> contract:
    /// null means the vendor's own default applies, never "we don't know yet". Sourced solely from
    /// <see cref="_resolvedModel"/> (set once, from the handshake's <c>get_state</c> — see
    /// <see cref="ApplyState"/>) — deliberately NOT backfilled with the merely-requested model, since
    /// the orchestrator reads this as confirmation Pi actually applied it.</summary>
    public string? ResolvedModel => _resolvedModel;

    public ChannelReader<AcpEventEnvelope> Envelopes => _transcript.Reader;

    /// <summary>Rule (a). A factory MUST await this after constructing the runtime and BEFORE
    /// returning control to the orchestrator. Faults — never hangs — on every path that cannot
    /// produce a session identity.</summary>
    internal Task WaitForSessionReadyAsync(CancellationToken ct) => _sessionReady.Task.WaitAsync(ct);

    // ---- Read pump ----

    /// <summary>The single reader of the child's stdout. Response frames complete their correlated
    /// pending command; event frames become transcript envelopes. Its ENDING — EOF, cancellation,
    /// or an unexpected fault — is this runtime's terminal signal, which is why
    /// <see cref="EnterTerminal"/> runs in a <c>finally</c> covering every one of them.</summary>
    async Task RunPumpAsync() {
        try {
            await foreach (var line in _process.ReadLinesAsync(_ownerToken).ConfigureAwait(false)) {
                var frame = PiRpc.TryParseLine(line);
                if (frame is null) continue;

                switch (frame.Kind) {
                    case PiRpcFrameKind.Response:
                        CompletePending(frame);
                        break;

                    case PiRpcFrameKind.Event:
                        HandleEvent(frame);
                        break;

                    default:
                        _logger.LogDebug("Pi: ignoring an unrecognized frame shape (agentId={AgentId}).", _agentId);
                        break;
                }
            }
        } catch (OperationCanceledException) when (_ownerToken.IsCancellationRequested) {
            // Normal teardown — EnterTerminal has already run (or runs in the finally below).
        } catch (Exception ex) {
            _logger.LogDebug(ex, "Pi: the read pump ended unexpectedly (agentId={AgentId}).", _agentId);
        } finally {
            EnterTerminal();
        }
    }

    void CompletePending(PiRpcFrame frame) {
        if (frame.Id is not { Length: > 0 } id) {
            _logger.LogDebug("Pi: dropped an uncorrelated response frame (agentId={AgentId}).", _agentId);
            return;
        }

        if (_pending.TryRemove(id, out var waiter)) waiter.TrySetResult(frame);
        else _logger.LogDebug("Pi: response {Id} matched no in-flight command (agentId={AgentId}).", id, _agentId);
    }

    /// <summary>
    /// Translates one event frame. Busy tracking rides <c>agent_start</c>/<c>agent_settled</c> —
    /// neither produces an envelope (see the plan's translation table), they only drive
    /// <see cref="WaitForTurnIdleAsync"/>.
    ///
    /// <para>The user-echo filter (rule (c)) is applied HERE, before the channel write, and it
    /// consults <see cref="PiRpc.ToEnvelopes"/>'s own output for the text rather than
    /// re-implementing the content-part concatenation — a second copy of that logic would drift
    /// from the translator and silently stop matching.</para>
    /// </summary>
    void HandleEvent(PiRpcFrame frame) {
        switch (frame.Type) {
            case "agent_start":
                SetBusy(true);
                return;

            case "agent_settled":
                SetBusy(false);
                return;
        }

        // Display fallback only — NOT the authoritative ResolvedModel (see ApplyState's comment on
        // _resolvedModel = ExtractModelId(...)): this feeds envelope.Model for rendering, never the
        // confirmed-applied-model signal the orchestrator reads off ResolvedModel.
        var envelopes = PiRpc.ToEnvelopes(frame, _resolvedModel ?? _requestedModel);
        if (envelopes.Count == 0) return;

        if (IsOurOwnEcho(frame, envelopes)) {
            _logger.LogDebug("Pi: dropped the echo of a prompt this daemon sent (agentId={AgentId}).", _agentId);
            return;
        }

        foreach (var env in envelopes) EmitAgentEnvelope(env);
    }

    /// <summary>Rule (c): true when this frame is Pi echoing back a prompt we sent, in which case
    /// the whole frame is dropped (we already emitted the <c>user_message</c> at send time). Only
    /// ever true for a single-envelope user-message frame — an assistant frame, or a user frame
    /// whose text matches nothing we sent, is real transcript.
    ///
    /// <para><b>Known cosmetic hole, not worth solving in PR-1.</b> Pi expands a <c>/skill:name</c>
    /// or <c>/template</c> input before echoing it back (rpc.md ~73) — the echoed
    /// <c>message_end</c> carries the EXPANDED text, not what this daemon literally sent, so
    /// <see cref="TryConsumeSentPrompt"/> finds no match and the slash-command message renders
    /// twice (once from the send-time envelope, once from the un-deduped echo).</para></summary>
    bool IsOurOwnEcho(PiRpcFrame frame, IReadOnlyList<AcpEventEnvelope> envelopes) =>
        frame.Type == "message_end"
        && envelopes is [{ Kind: AcpEventKind.UserMessage, Text: { } text }]
        && TryConsumeSentPrompt(text);

    // ---- Command correlation ----

    /// <summary>
    /// Registers a command's response waiter — the ONLY way an entry enters <see cref="_pending"/>.
    ///
    /// <para><b>The post-registration re-check is the whole point.</b>
    /// <see cref="EnterTerminal"/>'s fault loop iterates a SNAPSHOT of the keys, so a waiter
    /// registered after that snapshot was taken is never faulted by it, and the terminal flag is
    /// already set so nothing will fault it later either — the entry parks forever. Two real
    /// callers hit that window: a viewer's <see cref="SendUserInputAsync"/> racing a stop (its
    /// <see cref="ObservePromptResponseAsync"/> would never complete), and the constructor's own
    /// handshake racing a child that died synchronously (which would then burn the whole
    /// <see cref="DisposeAsync"/> join budget on a task that can never finish). Re-checking here,
    /// AFTER the entry is visible, means the two orderings cover each other: either
    /// <see cref="EnterTerminal"/>'s snapshot sees this entry, or this check sees the flag it
    /// set.</para>
    /// </summary>
    TaskCompletionSource<PiRpcFrame> RegisterPending(string id) {
        var waiter = new TaskCompletionSource<PiRpcFrame>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pending[id] = waiter;

        if (Volatile.Read(ref _terminal) != 0) FaultPending(id);

        return waiter;
    }

    void FaultPending(string id) {
        if (_pending.TryRemove(id, out var waiter))
            waiter.TrySetException(new InvalidOperationException(
                $"Pi: the hosted session ended before command {id} was answered."));
    }

    /// <summary>Test/diagnostic view of the correlation table — see <see cref="RegisterPending"/>
    /// for the race this makes observable.</summary>
    internal int PendingCommandCount => _pending.Count;

    // ---- Handshake ----

    async Task RunHandshakeAsync() {
        var waiter = RegisterPending(InitStateCommandId);

        try {
            await _process.WriteLineAsync(PiRpc.GetStateCommand(InitStateCommandId), _ownerToken).ConfigureAwait(false);

            var response = await waiter.Task.WaitAsync(_readyDeadline).ConfigureAwait(false);

            ApplyState(response);
        } catch (Exception ex) {
            // EVERY failure lands here — a failed write, the deadline, the child exiting (which
            // faults `waiter` via EnterTerminal), or a malformed/refused state response. Rule (a):
            // fault, never hang.
            FaultSessionReadyIfUnresolved(
                $"Pi: the hosted session's identity handshake failed (agentId={_agentId}).", ex);
        } finally {
            _pending.TryRemove(InitStateCommandId, out _);
        }
    }

    /// <summary>Applies a <c>get_state</c> response. Fails CLOSED on a refused response or a
    /// missing <c>sessionId</c>: reporting <c>""</c> as the session identity would bind the
    /// transcript to nothing, permanently and silently.</summary>
    void ApplyState(PiRpcFrame response) {
        if (response.Success == false) {
            FaultSessionReadyIfUnresolved($"Pi: get_state was refused (agentId={_agentId}).", inner: null);
            return;
        }

        var data = response.Root.Obj("data");

        if (data?.Str("sessionId") is not { Length: > 0 } sessionId) {
            FaultSessionReadyIfUnresolved(
                $"Pi: get_state answered without a sessionId (agentId={_agentId}).", inner: null);
            return;
        }

        _sessionId = sessionId;

        // NOT `?? _requestedModel`: ResolvedModel is the orchestrator's CONFIRMED-applied-model
        // signal (see the ResolvedModel property doc), so when get_state omits a model, null is the
        // only truthful value — the IAcpTranscriptSource contract's "null ⇒ vendor default applies".
        // Reporting the merely-requested model here would be an attribution lie: it claims Pi
        // confirmed a model it never mentioned. ResolvedModel is deliberately null in that case — it
        // is never backfilled from _requestedModel. The transcript ENVELOPE fallback below (see
        // HandleEvent's `_resolvedModel ?? _requestedModel`) is a SEPARATE, intentional concern: a
        // display fallback for envelope.Model, not the authoritative ResolvedModel this field feeds.
        _resolvedModel = ExtractModelId(data.Value);

        // Pi may already be mid-turn when we attach (a resumed session), so the initial busy state
        // comes from the handshake, not from having observed an agent_start we were never running
        // to see.
        if (data.Value.Bool("isStreaming") == true)
            SetBusy(true);

        ActivityClock?.SetLaunchStage("session_created");

        _sessionReady.TrySetResult();
    }

    /// <summary>Pi's <c>model</c> is a full object, not a string — <c>id</c> is its identity. A
    /// bare string is tolerated as schema drift rather than dropped, and anything else — missing
    /// entirely, or an unrecognized shape — reads as absent (null): <see cref="ApplyState"/> assigns
    /// this straight to <see cref="_resolvedModel"/> with NO requested-model fallback, since a null
    /// here means Pi never confirmed a model at all.</summary>
    static string? ExtractModelId(JsonElement data) {
        if (!data.TryGetProperty("model", out var model)) return null;
        if (model.IsString) return model.GetString();
        if (model.IsObject) return model.Str("id") ?? model.Str("name");

        return null;
    }

    void FaultSessionReadyIfUnresolved(string message, Exception? inner) {
        // TrySetException is a no-op against an already-resolved barrier, so this can never clobber
        // a real identity that arrived first.
        if (!_sessionReady.TrySetException(
                inner is null ? new InvalidOperationException(message)
                              : new InvalidOperationException(message, inner)))
            return;

        _logger.LogWarning(inner, "{Message}", message);

        // Observe the fault we just caused: nothing requires a caller to await this barrier (only a
        // factory does), and an unobserved faulted Task risks TaskScheduler.UnobservedTaskException
        // once the GC finalizes it. A real awaiter still sees the exception through its own await.
        _sessionReady.Task.ContinueWith(
            static t => _ = t.Exception,
            CancellationToken.None,
            TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
    }

    // ---- Input ----

    public async Task SendUserInputAsync(string text) {
        // Ordering is load-bearing on both halves. The echo memory is written BEFORE the command
        // goes on the wire, because Pi's echo can be read back by the pump before this method's
        // own continuation resumes. The envelope is emitted first so the viewer sees their message
        // immediately, rather than only when Pi gets round to echoing it.
        RememberSentPrompt(text);
        EmitLocalEnvelope(new AcpEventEnvelope(Kind: AcpEventKind.UserMessage, Text: text));

        var id     = NextCommandId();
        var waiter = RegisterPending(id);

        try {
            await _process.WriteLineAsync(PiRpc.PromptCommand(id, text), _ownerToken).ConfigureAwait(false);
        } catch {
            _pending.TryRemove(id, out _);

            // Undo the echo memory: this message never reached Pi, so its echo can never arrive —
            // and a stale entry is not inert. It would silently swallow the NEXT genuine user
            // message with identical text (a retry of exactly what just failed is the likeliest
            // thing to happen next), which is the dedupe misfiring on real content.
            TryConsumeSentPrompt(text);

            // Best-effort: dropped when the channel is already completed (the common case if the
            // write failed BECAUSE the session is ending), which is fine — the session ending is
            // itself the user-visible signal. Worth emitting for the case the write failed while
            // the session is otherwise healthy, where this note is the only feedback the person who
            // typed the message gets; the rethrow below only reaches the daemon log.
            EmitLocalEnvelope(new AcpEventEnvelope(
                Kind: AcpEventKind.SystemNote,
                Text: "Your message could not be delivered to the agent."));

            throw;
        }

        // Observed in the background: Pi's response to `prompt` acknowledges acceptance, and a
        // refusal (e.g. a prompt that raced an in-flight turn) is only ever visible to the person
        // who typed the message as a transcript note. Never awaited here — a prompt that queues
        // behind a long turn must not block the daemon's command lane. No lifetime leak: this
        // fire-and-forget task always completes because its pending waiter is faulted by
        // EnterTerminal, and _pumpTask (whose finally calls EnterTerminal) is created in the
        // constructor before it returns, so DisposeAsync always has it to join.
        _ = ObservePromptResponseAsync(waiter.Task);
    }

    async Task ObservePromptResponseAsync(Task<PiRpcFrame> response) {
        try {
            var frame = await response.ConfigureAwait(false);
            if (frame.Success != false) return;

            var reason = frame.Root.Str("error") ?? "pi rejected the message";
            EmitLocalEnvelope(new AcpEventEnvelope(
                Kind: AcpEventKind.SystemNote,
                Text: $"Your message was not accepted: {reason}"));
        } catch (Exception ex) {
            // The runtime went terminal before Pi answered — the session ending is itself the
            // user-visible signal, and the transcript channel is already completed.
            _logger.LogDebug(ex, "Pi: a prompt's response never arrived (agentId={AgentId}).", _agentId);
        }
    }

    public Task SendUserInputAndWaitForWriteAsync(string text) => SendUserInputAsync(text);

    /// <summary>Pi has no key surface — the ONE mapping that means anything is escape, which is
    /// Pi's <c>abort</c> (interrupt the current turn). Everything else is logged and dropped
    /// rather than guessed at.</summary>
    public async Task SendSpecialKeyAsync(string key) {
        if (!string.Equals(key, "escape", StringComparison.OrdinalIgnoreCase)) {
            _logger.LogDebug("Pi runtime ignoring SendSpecialKeyAsync({Key}) — no equivalent command.", key);
            return;
        }

        try {
            await _process.WriteLineAsync(PiRpc.AbortCommand(NextCommandId()), _ownerToken).ConfigureAwait(false);
        } catch (Exception ex) {
            _logger.LogDebug(ex, "Pi: failed to send an abort command (agentId={AgentId}).", _agentId);
        }
    }

    public Task SendRawInputAsync(byte[] data) =>
        throw new NotSupportedException(
            "Local-attach raw input is a PTY-only surface; the Pi runtime has no equivalent channel.");

    public void Resize(ushort cols, ushort rows) {
        // No terminal capability — pi --mode rpc's stdout is protocol traffic, not a terminal.
    }

    string NextCommandId() => $"kcap-{Interlocked.Increment(ref _commandSeq)}";

    // ---- Echo memory (rule (c)) ----

    void RememberSentPrompt(string text) {
        var evicted = false;

        lock (_echoGate) {
            _sentPrompts.Add(text);

            // Oldest-first eviction: an entry that has waited this long was never echoed.
            while (_sentPrompts.Count > SentPromptMemory) {
                _sentPrompts.RemoveAt(0);
                evicted = true;
            }
        }

        // Debug, not Warning: unreachable in normal turn-based interactive use (a caller waits for
        // idle between sends), so this is detectability for the live cert, not an operational
        // alarm — logged outside the lock, and only when eviction actually dropped an entry, to
        // avoid flooding.
        if (evicted)
            _logger.LogDebug("Pi: evicted an un-echoed sent-prompt at the cap (agentId={AgentId}).", _agentId);
    }

    /// <summary>Most-recent-first, consume-on-match — see rule (c) on why consuming (rather than
    /// merely testing membership) is what keeps a genuinely repeated message renderable.</summary>
    bool TryConsumeSentPrompt(string text) {
        lock (_echoGate) {
            for (var i = _sentPrompts.Count - 1; i >= 0; i--) {
                if (!string.Equals(_sentPrompts[i], text, StringComparison.Ordinal)) continue;

                _sentPrompts.RemoveAt(i);
                return true;
            }

            return false;
        }
    }

    // ---- Turn idleness ----

    /// <summary>Completes immediately when Pi is not streaming, else on the next
    /// <c>agent_settled</c>. Entering terminal releases every waiter (via
    /// <see cref="EnterTerminal"/>'s <c>SetBusy(false)</c>) — a waiter parked on a settle that can
    /// never arrive is a hang, not a stop.
    ///
    /// <para>A caller that may need to stop waiting on a wedged turn (e.g. a child blocked on an
    /// <c>extension_ui_request</c> that never settles — see <see cref="PiRpc"/>'s
    /// <c>TranslateExtensionUiRequest</c>) MUST pass a cancellable token: with
    /// <see cref="CancellationToken.None"/> the waiter only completes on the next
    /// <c>agent_settled</c> or on <see cref="EnterTerminal"/>.</para></summary>
    public Task WaitForTurnIdleAsync(CancellationToken ct) {
        TaskCompletionSource waiter;

        lock (_turnGate) {
            if (!_busy) return Task.CompletedTask;

            waiter = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            _idleWaiters.Add(waiter);
        }

        return ct.CanBeCanceled ? AwaitTurnIdleAsync(waiter, ct) : waiter.Task;
    }

    /// <summary>Waits, and on CANCELLATION removes its own waiter from
    /// <see cref="_idleWaiters"/> — the list is the one unbounded collection in this runtime, and a
    /// cancelled caller that leaves its entry behind leaks until the next settle (which may never
    /// come). The live caller that makes this real rather than theoretical is the orchestrator's
    /// periodic borrowed-snapshot refresh, which waits under a timeout every cycle: a slow leak,
    /// one entry per cancelled cycle, for as long as a turn stays in flight.</summary>
    async Task AwaitTurnIdleAsync(TaskCompletionSource waiter, CancellationToken ct) {
        try {
            await waiter.Task.WaitAsync(ct).ConfigureAwait(false);
        } catch (OperationCanceledException) {
            // Harmless if a concurrent SetBusy(false) already drained it — Remove on an absent
            // item is a no-op, and the waiter itself is unreferenced after this.
            lock (_turnGate) _idleWaiters.Remove(waiter);
            throw;
        }
    }

    /// <summary>Test/diagnostic view — see <see cref="AwaitTurnIdleAsync"/> for the leak this makes
    /// observable.</summary>
    internal int TurnIdleWaiterCount {
        get { lock (_turnGate) return _idleWaiters.Count; }
    }

    void SetBusy(bool busy) {
        TaskCompletionSource[]? release = null;

        lock (_turnGate) {
            _busy = busy;

            if (!busy && _idleWaiters.Count > 0) {
                release = [.. _idleWaiters];
                _idleWaiters.Clear();
            }
        }

        // Both effects OUTSIDE the lock: the clock takes its own lock, and a waiter's continuation
        // can run inline. Neither belongs under a lock this runtime's own pump re-enters.
        ActivityClock?.SetTurnInFlight(busy);

        if (release is null) return;

        foreach (var waiter in release) waiter.TrySetResult();
    }

    // ---- Transcript ----

    /// <summary>An envelope the AGENT produced — advances the activity clock, which is what makes
    /// this runtime's output the liveness signal a supervisor reads.</summary>
    void EmitAgentEnvelope(AcpEventEnvelope env) => Write(env, agentActivity: true);

    /// <summary>An envelope this daemon authored: the send-time user echo and the rejected-prompt
    /// note. Reaches the transcript identically but does NOT advance the clock — neither is
    /// evidence the agent produced anything, and counting a user's retries against a wedged agent
    /// as activity is exactly how an agent becomes un-reapable.</summary>
    void EmitLocalEnvelope(AcpEventEnvelope env) => Write(env, agentActivity: false);

    void Write(AcpEventEnvelope env, bool agentActivity) {
        // Advance BEFORE the channel write: a reader can wake the instant TryWrite makes the item
        // visible, on another thread, so the reverse order is a race a fast reader wins.
        if (agentActivity) ActivityClock?.Advance();

        if (_transcript.Writer.TryWrite(env)) return;

        // Debug, not Warning: an envelope arriving after the channel closed is the ORDINARY shape
        // of teardown (the pump is draining Pi's last lines while terminal is entered).
        _logger.LogDebug("Pi: dropped a transcript envelope — the channel is already completed.");
    }

    /// <summary>Rule (b): parks on the single constructor-owned <see cref="_terminalTcs"/> and
    /// yields no bytes ever. Its ENDING is what drives the orchestrator's finalize.</summary>
    public async IAsyncEnumerable<byte[]> ReadOutputAsync(
            [EnumeratorCancellation] CancellationToken ct = default) {
        await _terminalTcs.Task.WaitAsync(ct).ConfigureAwait(false);
        yield break;
    }

    // ---- Terminal / teardown ----

    /// <summary>Idempotent. Completes the park, faults every waiter that can no longer be answered
    /// (pending commands, the ready barrier, turn-idle waiters), and closes the transcript —
    /// deliberately LAST, so a note written by one of those faults still has somewhere to go.
    /// Never emits <c>session_ended</c>.</summary>
    bool EnterTerminal() {
        if (Interlocked.Exchange(ref _terminal, 1) != 0) return false;

        _terminalTcs.TrySetResult();

        // A snapshot of the keys, deliberately — see RegisterPending, which covers the entries
        // registered after this loop reads them (the flag set above is what that check observes).
        foreach (var id in _pending.Keys) FaultPending(id);

        FaultSessionReadyIfUnresolved(
            $"Pi: the hosted session ended before its identity handshake completed (agentId={_agentId}).",
            inner: null);

        // Releasing idle waiters here can wake a caller that then attempts SendUserInputAsync during
        // teardown; that write fails fast (dead pipe → caught → "could not be delivered" note) — i.e.
        // "idle after EnterTerminal" does NOT mean the agent is accepting input. Accepted fail-open
        // behavior, not a bug.
        SetBusy(false);

        _transcript.Writer.TryComplete();

        return true;
    }

    /// <summary>Best-effort <c>abort</c> so Pi can wind the current turn down itself, then a
    /// bounded wait, then terminate — the interface's documented "the orchestrator falls through
    /// to terminate" contract, performed here so a clean stop is attempted first.</summary>
    public async Task RequestGracefulStopAsync() {
        try {
            await _process.WriteLineAsync(PiRpc.AbortCommand(NextCommandId()), _ownerToken).ConfigureAwait(false);
        } catch (Exception ex) {
            _logger.LogDebug(ex, "Pi: failed to send the graceful-stop abort (agentId={AgentId}).", _agentId);
        }

        await _process.WaitForExitAsync(_stopGrace).ConfigureAwait(false);

        if (_process.HasExited) return;

        await TerminateAsync(_stopGrace).ConfigureAwait(false);
    }

    /// <summary>Delegates to the child — unlike Antigravity, "exited" here is a real process fact,
    /// not a logical phase. Returns silently on timeout, per the interface contract.</summary>
    public Task WaitForExitAsync(TimeSpan? timeout = null) => _process.WaitForExitAsync(timeout);

    public async Task TerminateAsync(TimeSpan? timeout = null) {
        EnterTerminal();

        await _ownerCts.CancelAsync().ConfigureAwait(false);

        try {
            await _process.TerminateAsync(timeout).ConfigureAwait(false);
        } catch (Exception ex) {
            _logger.LogDebug(ex, "Pi: failed to terminate the hosted child (agentId={AgentId}).", _agentId);
        }
    }

    public async ValueTask DisposeAsync() {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;   // idempotent; _onDisposed fires once

        EnterTerminal();

        await _ownerCts.CancelAsync().ConfigureAwait(false);

        // Both bounded and both swallowed — a stuck pump or an unanswered handshake must never hang
        // a dispose. The handshake is joined too so its own `finally` cannot run against a disposed
        // process.
        try {
            await Task.WhenAll(_pumpTask, _handshakeTask).WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
        } catch (Exception ex) {
            _logger.LogDebug(ex, "Pi: the read pump did not join within the dispose budget (agentId={AgentId}).", _agentId);
        }

        try {
            await _process.DisposeAsync().ConfigureAwait(false);
        } catch (Exception ex) {
            _logger.LogDebug(ex, "Pi: failed to dispose the hosted child (agentId={AgentId}).", _agentId);
        }

        // _ownerCts is deliberately not disposed — see its field remarks.

        if (_onDisposed is null) return;

        try {
            _onDisposed();
        } catch (Exception ex) {
            _logger.LogWarning(ex, "Pi: the runtime's disposal callback failed (agentId={AgentId}).", _agentId);
        }
    }
}
