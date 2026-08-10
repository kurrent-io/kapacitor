// test/Capacitor.Cli.Tests.Unit/Acp/FakeAcpAgent.cs
using System.Collections.Concurrent;
using System.IO.Pipelines;
using System.Runtime.ExceptionServices;
using System.Text;
using System.Text.Json;

namespace Capacitor.Cli.Tests.Unit.Acp;

/// <summary>
/// Reusable, scriptable in-process stand-in for a real <c>cursor-agent acp</c> child process.
/// Plays the "agent side" of the ACP stdio JSON-RPC wire protocol against a real
/// <see cref="Capacitor.Cli.Daemon.Acp.AcpConnection"/> under test, so higher-level code (Task 9's
/// <c>AcpHostedAgentRuntime</c>) can be exercised end-to-end without spawning the real binary.
///
/// Topology (mirrors the <c>Harness</c> in <c>AcpConnectionTests.cs</c>, generalized): two
/// independent <see cref="Pipe"/>s stand in for the agent process's stdin/stdout.
/// <list type="bullet">
/// <item><description><see cref="ClientWriteStream"/> is the pipe the <em>connection under test</em>
/// writes into (its <c>writeStream</c> ctor arg) — i.e. this is the agent's simulated STDIN. This
/// fake reads inbound frames from the other end of that same pipe.</description></item>
/// <item><description><see cref="ClientReadStream"/> is the pipe the <em>connection under test</em>
/// reads from (its <c>readStream</c> ctor arg) — i.e. this is the agent's simulated STDOUT. This
/// fake writes outbound frames (responses/notifications) into the other end of that same
/// pipe.</description></item>
/// </list>
/// Construct the connection under test as:
/// <code>new AcpConnection(writeStream: fake.ClientWriteStream, readStream: fake.ClientReadStream, logger)</code>
///
/// Lifecycle: <see cref="RunAsync"/> must be started explicitly (e.g. alongside the connection's own
/// <c>RunAsync</c>) — it is the fake's read loop and does nothing until awaited/started as a
/// background task. It runs until the token is cancelled or the simulated stdin stream ends.
/// </summary>
public sealed class FakeAcpAgent : IAsyncDisposable {
    /// <summary>Fixed, deterministic session id returned by the fake's <c>session/new</c> handler.</summary>
    public const string FixedSessionId = "fc2e09cf-f4b0-4463-9dc1-bda11268896b";

    static readonly JsonElement DefaultPromptResult =
        JsonDocument.Parse("""{"stopReason":"end_turn"}""").RootElement.Clone();

    readonly Pipe   _toAgent;            // connection writes here; fake reads here (simulated stdin)
    readonly Pipe   _toClient = new();   // fake writes here; connection reads here (simulated stdout)
    readonly Stream _agentReadsFromConnection;
    readonly Stream _agentWritesToConnection;

    readonly ConcurrentQueue<(IReadOnlyList<JsonElement> Updates, JsonElement Result)> _promptScripts = new();
    readonly List<(string Method, JsonElement? Params)> _receivedCalls = new();
    readonly object _receivedCallsLock = new();

    string? _pendingServerRequestToolCallJson;
    string? _pendingServerRequestOptionsJson;
    long    _nextServerRequestId = 1000; // disjoint range from the connection's own outbound ids

    JsonElement _sessionNewResult = ProbeConfirmedSessionNewResult;
    bool        _failNextSetConfigOption;
    bool        _failNextSetModel;

    JsonElement                  _initializeResult = ProbeConfirmedInitializeResult;
    (int Code, string Message)? _failNextInitialize;

    readonly List<(string Method, JsonElement? Params)>          _sentServerRequests = new();
    readonly object                                              _sentServerRequestsLock = new();
    JsonElement?                                                 _lastServerRequestResponse;
    JsonElement?                                                 _lastServerRequestError;

    /// <summary>
    /// Every server→client request (e.g. <c>session/request_permission</c>) this fake has SENT to
    /// the connection under test, in send order. Populated by
    /// <see cref="EnqueuePermissionRequestDuringNextPrompt"/>'s injection into
    /// <see cref="RunPromptScriptAsync"/>.
    /// </summary>
    public IReadOnlyList<(string Method, JsonElement? Params)> SentServerRequests {
        get { lock (_sentServerRequestsLock) return _sentServerRequests.ToArray(); }
    }

    /// <summary>The connection's JSON-RPC <c>result</c> for the most recent server→client request this fake sent, or null if not yet answered.</summary>
    public JsonElement? LastServerRequestResponse => _lastServerRequestResponse;

    /// <summary>The connection's JSON-RPC <c>error</c> for the most recent server→client request this fake sent, or null if not answered with an error.</summary>
    public JsonElement? LastServerRequestError => _lastServerRequestError;

    /// <summary>
    /// Qodo daemon-review Q3: the FIRST exception thrown by a fire-and-forget
    /// <c>DispatchLineAsync</c> dispatch (see <see cref="RunAsync"/>'s remarks on why dispatch must
    /// stay untracked-by-the-loop rather than awaited in-line), or <see langword="null"/> if none has
    /// faulted yet. PRE-FIX, a dispatch fault was only <c>Debug.WriteLine</c>'d — invisible to a test
    /// assertion — so a faulted dispatch manifested as a hang/timeout on whatever the test was
    /// awaiting from the connection, rather than a clear failure. Captured via
    /// <see cref="ExceptionDispatchInfo"/> (not the bare <see cref="Exception"/>) so
    /// <see cref="DisposeAsync"/> can rethrow it with its ORIGINAL stack trace preserved. Only the
    /// first fault is kept — later ones are logged the same way the pre-fix code always did, since a
    /// second fault on an already-faulted fake is rarely independently interesting and keeping "the
    /// first thing that went wrong" is the more useful diagnostic.
    /// </summary>
    public Exception? DispatchFault => Volatile.Read(ref _dispatchFault)?.SourceException;

    ExceptionDispatchInfo? _dispatchFault;

    /// <summary>
    /// Arranges the fake to send a real <c>session/request_permission</c> server→client request
    /// (built via <see cref="BuildRequestPermissionFrame"/>) as part of the NEXT
    /// <c>session/prompt</c> turn's response, awaiting and recording the connection's reply before
    /// answering the prompt itself with the default <c>end_turn</c> result. This finally wires the
    /// builder helpers <see cref="BuildRequestPermissionFrame"/>/<see cref="PermissionOutcomeSelected"/>/
    /// <see cref="PermissionOutcomeCancelled"/> into the fake's active dispatch loop — Task 8
    /// built them but deliberately left them unwired (see this file's original remarks), since the
    /// permission bridge itself was Task 9's job.
    /// </summary>
    public void EnqueuePermissionRequestDuringNextPrompt(string toolCallJson, string optionsJson) {
        _pendingServerRequestToolCallJson = toolCallJson;
        _pendingServerRequestOptionsJson  = optionsJson;
    }

    /// <summary>
    /// The stream a NEW <see cref="Capacitor.Cli.Daemon.Acp.AcpConnection"/> under test should be
    /// constructed with as its <c>writeStream</c> — the connection's outbound frames land here, and
    /// this fake reads them from the other end of the same pipe (the fake's simulated stdin).
    /// </summary>
    public Stream ClientWriteStream { get; }

    /// <summary>
    /// The stream a NEW <see cref="Capacitor.Cli.Daemon.Acp.AcpConnection"/> under test should be
    /// constructed with as its <c>readStream</c> — this fake's outbound frames (responses,
    /// <c>session/update</c> notifications) land here, and the connection reads them from the other
    /// end of the same pipe (the fake's simulated stdout).
    /// </summary>
    public Stream ClientReadStream { get; }

    /// <summary>
    /// Every inbound method the fake has dispatched so far, in arrival order, with a verbatim clone
    /// of its (possibly absent) params. Safe to read concurrently with an in-flight
    /// <see cref="RunAsync"/> loop — appended to under a lock, snapshotted here as an
    /// <see cref="IReadOnlyList{T}"/> copy so callers never see a collection mutated mid-enumeration.
    /// </summary>
    public IReadOnlyList<(string Method, JsonElement? Params)> ReceivedCalls {
        get {
            lock (_receivedCallsLock)
                return _receivedCalls.ToArray();
        }
    }

    readonly TaskCompletionSource _initializeReceived = new(TaskCreationOptions.RunContinuationsAsynchronously);
    readonly TaskCompletionSource _sessionNewReceived = new(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <summary>Completes the instant the fake RECORDS an <c>initialize</c> call. A reap test awaits
    /// this before triggering its reap/disposal, so the reap can never preempt the handshake's
    /// <c>initialize</c> — the deterministic replacement for a wall-clock poll that a slower CI runner
    /// races (windows). Pair with <c>inlineAgentReads: true</c> so recording is synchronous with the
    /// connection's write (see the constructor), removing the pool-scheduling window entirely.</summary>
    public Task InitializeReceived => _initializeReceived.Task;

    /// <summary>As <see cref="InitializeReceived"/>, for the <c>session/new</c> handshake stage.</summary>
    public Task SessionNewReceived => _sessionNewReceived.Task;

    /// <param name="inlineAgentReads">Run the fake's READ continuations INLINE on the connection's
    /// write/flush thread rather than the thread pool. The connection writes <c>initialize</c>
    /// synchronously inside StartAsync, so inline reads make the fake RECORD it synchronously too —
    /// closing the pool-scheduling window in which a launch-deadline/disposal on a slow CI runner could
    /// tear down the transport before <c>initialize</c> is ever read (the windows ordering race).
    /// Opt-in: only the reap tests that need determinism pass true; every other fixture keeps the
    /// default thread-pool scheduler and its established behaviour.</param>
    public FakeAcpAgent(bool inlineAgentReads = false) {
        _toAgent = inlineAgentReads
            ? new Pipe(new PipeOptions(readerScheduler: PipeScheduler.Inline, useSynchronizationContext: false))
            : new Pipe();

        _agentReadsFromConnection = _toAgent.Reader.AsStream();
        _agentWritesToConnection  = _toClient.Writer.AsStream();

        ClientWriteStream = _toAgent.Writer.AsStream();
        ClientReadStream  = _toClient.Reader.AsStream();
    }

    /// <summary>
    /// Overrides the script the NEXT <c>session/prompt</c> request will run: the fake emits
    /// <paramref name="updates"/> (each already a full <c>session/update</c> notification envelope —
    /// build them with <see cref="BuildSessionUpdateNotification"/> or one of the
    /// <c>Build*Update</c> helpers) as raw frames, in order, then answers the request with
    /// <paramref name="result"/>. Call this before sending the request whose behavior you want to
    /// override. Scripts are consumed one-per-prompt and queued (FIFO) — if you never call this, the
    /// fake falls back to its built-in default script (one <c>agent_message_chunk</c> then
    /// <c>{"stopReason":"end_turn"}</c>) for every <c>session/prompt</c>.
    /// </summary>
    public void EnqueuePromptScript(IReadOnlyList<JsonElement> updateNotifications, JsonElement result) =>
        _promptScripts.Enqueue((updateNotifications, result));

    /// <summary>
    /// Overrides the <c>session/new</c> response this fake returns for every subsequent
    /// <c>session/new</c> request (default: <see cref="ProbeConfirmedSessionNewResult"/>, a single
    /// <c>composer-2.5[fast=true]</c> model) — gap 1 model-selection tests use this to script
    /// a richer <c>models.availableModels</c> list + <c>currentModelId</c>.
    /// <see cref="BuildSessionNewResult"/> is the easiest way to build a well-formed override.
    /// </summary>
    public void SetSessionNewResult(JsonElement result) => _sessionNewResult = result;

    /// <summary>
    /// Arranges the NEXT <c>session/set_config_option</c> request to be answered with a JSON-RPC
    /// error instead of a success result — models a real agent rejecting the
    /// resolved model id, exercising <c>AcpHostedAgentRuntime</c>'s non-fatal "log a warning and
    /// continue with Cursor's default model" handling.
    /// </summary>
    public void FailNextSetConfigOption() => _failNextSetConfigOption = true;

    /// <summary>
    /// Arranges the NEXT <c>session/set_model</c> request to be answered with a JSON-RPC error
    /// instead of a success result — models a real agent rejecting the resolved model id,
    /// exercising <see cref="Capacitor.Cli.Daemon.Acp.SetModelSelector"/>'s non-fatal "log a
    /// warning and continue with the vendor's default model" handling.
    /// </summary>
    public void FailNextSetModel() => _failNextSetModel = true;

    /// <summary>
    /// Overrides the <c>initialize</c> response this fake returns for every subsequent
    /// <c>initialize</c> request (default: <see cref="ProbeConfirmedInitializeResult"/>, protocol
    /// version 1 with <c>loadSession: true</c>) — protocol-negotiation tests use this to
    /// script a mismatched <c>protocolVersion</c> or a missing/false <c>agentCapabilities</c>.
    /// <see cref="BuildInitializeResult"/> is the easiest way to build a well-formed override.
    /// </summary>
    public void SetInitializeResult(JsonElement result) => _initializeResult = result;

    // ── Reconnect/resume support (session/load + crash simulation) ──────────────────────────────

    readonly List<JsonElement> _loadReplayUpdates = [];
    (int Code, string Message)? _failSessionLoad;

    /// <summary>When set, one conversation update is written AFTER the <c>session/load</c>
    /// response — the deliberately barrier-violating variant (reconnect spec §11.13): the ACP spec
    /// says the response comes only after all replayed entries, so this models a vendor breaking
    /// that MUST.</summary>
    public bool EmitConversationUpdateAfterLoadResponse { get; set; }

    /// <summary>When set, the <c>initialize</c> RESPONSE is held until the gate completes (the
    /// request is still recorded immediately) — used to park a reconnect candidate mid-handshake so
    /// a test can crash it there.</summary>
    public TaskCompletionSource? HoldInitializeResponse { get; set; }

    /// <summary>When set, the <c>session/new</c> RESPONSE is held until the gate completes (the
    /// request is still recorded immediately) — mirrors <see cref="HoldInitializeResponse"/> exactly.
    /// Added for Task 13's per-stage ACP launch handshake cap: lets a test wedge or slow-but-complete
    /// the "session_created" stage independently of "initialized"/"model_set".</summary>
    public TaskCompletionSource? HoldSessionNewResponse { get; set; }

    /// <summary>Scripts the <c>session/update</c> notifications replayed by the NEXT (and every
    /// later) <c>session/load</c>, in order, before the response — models the coalesced history
    /// replay both capable vendors produce.</summary>
    public void SetSessionLoadReplay(IReadOnlyList<JsonElement> updateNotifications) {
        _loadReplayUpdates.Clear();
        _loadReplayUpdates.AddRange(updateNotifications);
    }

    /// <summary>Makes every <c>session/load</c> fail with a JSON-RPC error — persistent, not
    /// one-shot, modeling the two measured refusal classes (Kiro's durable stale-owner lock,
    /// Gemini's unpersisted session), which do not clear with retries.</summary>
    public void FailSessionLoad(int code, string message) => _failSessionLoad = (code, message);

    /// <summary>
    /// Simulates the agent process crashing: completes the fake's outbound pipe, so the connection
    /// under test reads EOF and its loop ends — the primary crash signal. Bytes the connection
    /// writes afterwards buffer into a pipe nobody will ever read, exactly the
    /// written-but-never-read ambiguity a real dead child produces. The paired
    /// <c>FakeAcpProcess</c>'s exit must be signalled separately by the test, mirroring reality:
    /// pipe teardown and process exit are distinct events.
    /// </summary>
    public void SimulateCrash() => _toClient.Writer.Complete();

    /// <summary>
    /// Arranges the NEXT <c>initialize</c> request to be answered with a JSON-RPC error instead of
    /// a success result — models a logged-out/unsubscribed <c>cursor-agent</c> rejecting
    /// the handshake, exercising <c>AcpHostedAgentRuntime.StartAsync</c>'s auth/subscription hint.
    /// </summary>
    public void FailNextInitialize(int code, string message) => _failNextInitialize = (code, message);

    /// <summary>
    /// Convenience builder for a caller-controlled <c>initialize</c> result — narrower than
    /// the full <see cref="ProbeConfirmedInitializeResult"/> fixture, carrying only the two fields
    /// <c>AcpHostedAgentRuntime.StartAsync</c> actually reads: <paramref name="protocolVersion"/>
    /// and, when non-null, an <c>agentCapabilities.loadSession</c> flag. Passing
    /// <paramref name="loadSession"/> as <see langword="null"/> omits <c>agentCapabilities</c>
    /// entirely, exercising the "missing agentCapabilities" defensive-default path.
    /// </summary>
    public static JsonElement BuildInitializeResult(int protocolVersion, bool? loadSession = null) {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream)) {
            writer.WriteStartObject();
            writer.WriteNumber("protocolVersion", protocolVersion);
            if (loadSession is { } ls) {
                writer.WriteStartObject("agentCapabilities");
                writer.WriteBoolean("loadSession", ls);
                writer.WriteEndObject();
            }
            writer.WriteEndObject();
        }

        return JsonDocument.Parse(stream.ToArray()).RootElement.Clone();
    }

    /// <summary>
    /// Convenience builder for a probe-shaped <c>session/new</c> result with a
    /// caller-controlled <c>models.availableModels</c> list + <c>currentModelId</c>.
    /// <paramref name="availableModels"/> entries are <c>(modelId, name)</c> pairs.
    /// </summary>
    public static JsonElement BuildSessionNewResult(
            string sessionId,
            string currentModelId,
            IEnumerable<(string ModelId, string Name)> availableModels) {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream)) {
            writer.WriteStartObject();
            writer.WriteString("sessionId", sessionId);
            writer.WriteStartObject("models");
            writer.WriteString("currentModelId", currentModelId);
            writer.WriteStartArray("availableModels");
            foreach (var (modelId, name) in availableModels) {
                writer.WriteStartObject();
                writer.WriteString("modelId", modelId);
                writer.WriteString("name", name);
                writer.WriteEndObject();
            }
            writer.WriteEndArray();
            writer.WriteEndObject();
            writer.WriteStartArray("configOptions");
            writer.WriteEndArray();
            writer.WriteEndObject();
        }

        return JsonDocument.Parse(stream.ToArray()).RootElement.Clone();
    }

    /// <summary>
    /// When set, every <c>session/prompt</c> request's RESPONSE (not the queued updates, if any) is
    /// held back until <paramref name="gate"/> completes — the fake still records the call
    /// immediately (so <see cref="ReceivedCalls"/> observes it right away), it just doesn't answer.
    /// Models a real agent mid-turn: used by Fix E tests to prove
    /// <c>AcpHostedAgentRuntime.StartAsync</c>/<c>SendUserInputAsync</c> return promptly WITHOUT
    /// waiting for the turn's <c>stopReason</c> response, instead of the pre-fix behavior where
    /// both awaited the full round trip. Set to <see langword="null"/> (the default) to answer
    /// immediately as usual.
    /// </summary>
    public TaskCompletionSource? HoldPromptResponses { get; set; }

    /// <summary>
    /// Test plan item 9: when set, a <c>session/set_config_option</c> request's RESPONSE is
    /// held back until <paramref name="gate"/>'s task completes — mirrors
    /// <see cref="HoldPromptResponses"/> exactly. The fake still records the call immediately (so
    /// <see cref="ReceivedCalls"/> observes it right away via <c>DispatchLineAsync</c>'s
    /// <c>Record</c> call, which runs before this method is ever reached), it just doesn't answer
    /// until released. Used to prove a <c>ct</c> canceled WHILE this RPC is in flight aborts
    /// <c>StartAsync</c> via a propagated <see cref="OperationCanceledException"/>, rather than the
    /// selector silently swallowing it and returning <see langword="null"/> the way it does for a
    /// resolution failure.
    /// </summary>
    public TaskCompletionSource? HoldSetConfigOptionResponse { get; set; }

    /// <summary>When set, a <c>session/set_model</c> request's RESPONSE is held back until the gate
    /// completes — mirrors <see cref="HoldSetConfigOptionResponse"/> exactly (the call is still
    /// recorded immediately), proving a <c>ct</c> canceled while THIS RPC is in flight propagates
    /// <see cref="OperationCanceledException"/> out of the selector.</summary>
    public TaskCompletionSource? HoldSetModelResponse { get; set; }

    /// <summary>
    /// The fake's read loop: parses newline-delimited JSON-RPC frames arriving from the connection
    /// under test (its simulated stdin) and dispatches <c>initialize</c> / <c>session/new</c> /
    /// <c>session/prompt</c> / <c>session/cancel</c>. Must be started explicitly by the test (e.g.
    /// <c>var fakeRunTask = fake.RunAsync(cts.Token);</c> run concurrently with the connection's own
    /// <c>RunAsync</c>) — nothing happens until this is running. Returns when <paramref name="ct"/>
    /// is cancelled or the simulated stdin stream ends.
    /// </summary>
    public async Task RunAsync(CancellationToken ct) {
        using var reader = new StreamReader(_agentReadsFromConnection, Encoding.UTF8, detectEncodingFromByteOrderMarks: false, leaveOpen: true);

        try {
            while (!ct.IsCancellationRequested) {
                var line = await reader.ReadLineAsync(ct).ConfigureAwait(false);
                if (line is null)
                    break;

                if (string.IsNullOrWhiteSpace(line))
                    continue;

                // dispatched as untracked background work rather than awaited in-loop.
                // EnqueuePermissionRequestDuringNextPrompt's session/request_permission send-and-await
                // (SendServerRequestAndAwaitResponseAsync) is itself triggered from a session/prompt's
                // dispatch — its TaskCompletionSource can only be completed by THIS SAME loop reading
                // the connection's reply line. Awaiting the dispatch here would therefore deadlock:
                // the loop would be blocked inside the prompt's dispatch waiting for a line that only
                // the loop itself can read. Firing dispatch as background work keeps the loop free to
                // read subsequent lines (including that reply) while a single dispatch is in flight.
                // Record() (called synchronously at the top of DispatchLineAsync, before any await)
                // still runs before this method returns to the loop for lines processed one at a
                // time by ReadLineAsync, so ReceivedCalls order is unaffected for the existing tests
                // that assert strict ordering (FakeAcpAgentTests) — those never have two lines
                // in flight at once.
                var dispatchTask = DispatchLineAsync(line, ct);
                _ = dispatchTask.ContinueWith(t => {
                    if (!t.IsFaulted)
                        return;

                    System.Diagnostics.Debug.WriteLine($"FakeAcpAgent: DispatchLineAsync faulted: {t.Exception}");

                    // Qodo daemon-review Q3: capture the FIRST fault (thread-safe — multiple
                    // dispatches can be in flight at once, see this method's own remarks above) so
                    // it surfaces via DispatchFault / DisposeAsync instead of being visible only in
                    // Debug output. t.Exception is an AggregateException; unwrap to the single inner
                    // exception a faulted Task always carries here (DispatchLineAsync never throws
                    // an AggregateException itself).
                    var captured = ExceptionDispatchInfo.Capture(t.Exception!.InnerException ?? t.Exception);
                    Interlocked.CompareExchange(ref _dispatchFault, captured, null);
                }, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
            }
        } catch (OperationCanceledException) when (ct.IsCancellationRequested) {
            // normal shutdown
        }
    }

    async Task DispatchLineAsync(string line, CancellationToken ct) {
        using var doc  = JsonDocument.Parse(line);
        var       root = doc.RootElement;

        var hasId     = root.TryGetProperty("id", out var idElement);
        var hasMethod = root.TryGetProperty("method", out var methodElement);
        if (!hasMethod) {
            // A frame with no "method" is either the connection's reply to one of THIS fake's own
            // session/request_permission sends (id in _pendingFakeRequests), or an unrelated
            // response the fake doesn't care about — never expected in this fixture's scripts
            // other than this new path, but guarded defensively.
            if (hasId && idElement.TryGetInt64(out var replyId)) {
                TaskCompletionSource<(JsonElement?, JsonElement?)>? pending;
                lock (_sentServerRequestsLock) _pendingFakeRequests.Remove(replyId, out pending);

                if (pending is not null) {
                    var hasResult = root.TryGetProperty("result", out var resultEl);
                    var hasError  = root.TryGetProperty("error", out var errorEl);
                    pending.TrySetResult((hasResult ? resultEl.Clone() : null, hasError ? errorEl.Clone() : null));
                }
            }

            return;
        }

        var method        = methodElement.GetString() ?? "";
        var paramsElement = root.TryGetProperty("params", out var p) ? p.Clone() : (JsonElement?) null;

        Record(method, paramsElement);

        if (!hasId) {
            // Notification (e.g. session/cancel) — recorded above, no response frame is written.
            return;
        }

        var id = idElement.Clone();

        switch (method) {
            case "initialize":
                if (HoldInitializeResponse is { } initGate)
                    await initGate.Task.ConfigureAwait(false);

                if (_failNextInitialize is { } failInit) {
                    _failNextInitialize = null;
                    await WriteErrorResponseAsync(id, failInit.Code, failInit.Message, ct).ConfigureAwait(false);
                } else {
                    await WriteResponseAsync(id, _initializeResult, ct).ConfigureAwait(false);
                }
                break;

            case "session/new":
                if (HoldSessionNewResponse is { } sessionNewGate)
                    await sessionNewGate.Task.ConfigureAwait(false);

                await WriteResponseAsync(id, _sessionNewResult, ct).ConfigureAwait(false);
                break;

            case "session/load":
                if (_failSessionLoad is { } failLoad) {
                    await WriteErrorResponseAsync(id, failLoad.Code, failLoad.Message, ct).ConfigureAwait(false);
                    break;
                }

                // Replay first, then the response — the ACP MUST this fixture models (and the
                // reconnect suppression window's far edge). The response reuses the session/new
                // shape: the probe confirmed a real load returns the same modes/models structure.
                foreach (var update in _loadReplayUpdates)
                    await WriteRawFrameAsync(update, ct).ConfigureAwait(false);

                await WriteResponseAsync(id, _sessionNewResult, ct).ConfigureAwait(false);

                if (EmitConversationUpdateAfterLoadResponse)
                    await WriteRawFrameAsync(
                        DefaultAgentMessageChunkUpdate(FixedSessionId, "late-after-barrier"), ct).ConfigureAwait(false);
                break;

            case "session/set_config_option":
                await HandleSetConfigOptionAsync(id, paramsElement, ct).ConfigureAwait(false);
                break;

            case "session/set_model":
                await HandleSetModelAsync(id, ct).ConfigureAwait(false);
                break;

            case "session/prompt":
                await RunPromptScriptAsync(id, ct).ConfigureAwait(false);
                break;

            default:
                // Unrecognized request method: still recorded above; answer with a method-not-found
                // error so a caller awaiting the response doesn't hang forever.
                await WriteErrorResponseAsync(id, -32601, $"Method not found: {method}", ct).ConfigureAwait(false);
                break;
        }
    }

    async Task RunPromptScriptAsync(JsonElement id, CancellationToken ct) {
        if (_pendingServerRequestToolCallJson is not null && _pendingServerRequestOptionsJson is not null) {
            var toolCallJson = _pendingServerRequestToolCallJson;
            var optionsJson  = _pendingServerRequestOptionsJson;
            _pendingServerRequestToolCallJson = null;
            _pendingServerRequestOptionsJson  = null;

            await SendServerRequestAndAwaitResponseAsync("session/request_permission", toolCallJson, optionsJson, ct).ConfigureAwait(false);
        }

        var (updates, result) = _promptScripts.TryDequeue(out var script)
            ? script
            : (new[] { DefaultAgentMessageChunkUpdate(FixedSessionId, "hello from FakeAcpAgent") }, DefaultPromptResult);

        foreach (var update in updates)
            await WriteRawFrameAsync(update, ct).ConfigureAwait(false);

        if (HoldPromptResponses is { } gate)
            await gate.Task.ConfigureAwait(false);

        await WriteResponseAsync(id, result, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Model-selection gap 1: answers a <c>session/set_config_option</c> request — either the scripted
    /// JSON-RPC error (see <see cref="FailNextSetConfigOption"/>) or a probe-shaped success result
    /// echoing back the requested <c>value</c> as the <c>model</c> config option's
    /// <c>currentValue</c>, mirroring the real agent's confirmed response shape
    /// (<c>docs/ai-688-cursor-prototype-findings.md</c>). The request itself is already captured in
    /// <see cref="ReceivedCalls"/> by <see cref="DispatchLineAsync"/> before this runs.
    /// </summary>
    async Task HandleSetConfigOptionAsync(JsonElement id, JsonElement? @params, CancellationToken ct) {
        if (_failNextSetConfigOption) {
            _failNextSetConfigOption = false;
            await WriteErrorResponseAsync(id, -32602, "Invalid params: unknown model", ct).ConfigureAwait(false);
            return;
        }

        if (HoldSetConfigOptionResponse is { } gate)
            await gate.Task.ConfigureAwait(false);

        var value = @params is { } p && p.TryGetProperty("value", out var valueEl) ? valueEl.GetString() ?? "" : "";

        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream)) {
            writer.WriteStartObject();
            writer.WriteStartArray("configOptions");
            writer.WriteStartObject();
            writer.WriteString("id", "model");
            writer.WriteString("currentValue", value);
            writer.WriteEndObject();
            writer.WriteEndArray();
            writer.WriteEndObject();
        }

        var result = JsonDocument.Parse(stream.ToArray()).RootElement.Clone();
        await WriteResponseAsync(id, result, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Answers a <c>session/set_model</c> request — either the scripted JSON-RPC error (see
    /// <see cref="FailNextSetModel"/>) or the probe-confirmed success shape: an EMPTY object
    /// (<c>docs/probes/2026-08-05-kiro-model-override/</c> measured Kiro answering
    /// <c>{"result":{}}</c>), unlike <c>session/set_config_option</c>'s configOptions echo. The
    /// request itself is already captured in <see cref="ReceivedCalls"/> by
    /// <c>DispatchLineAsync</c> before this runs.
    /// </summary>
    async Task HandleSetModelAsync(JsonElement id, CancellationToken ct) {
        if (_failNextSetModel) {
            _failNextSetModel = false;
            await WriteErrorResponseAsync(id, -32602, "Invalid params: unknown model", ct).ConfigureAwait(false);
            return;
        }

        if (HoldSetModelResponse is { } gate)
            await gate.Task.ConfigureAwait(false);

        var result = JsonDocument.Parse("{}").RootElement.Clone();
        await WriteResponseAsync(id, result, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Sends a server→client request frame (currently only <c>session/request_permission</c>) with
    /// a fake-allocated id disjoint from the connection's own outbound id space, waits for the
    /// connection's response frame, and records it on <see cref="LastServerRequestResponse"/>/
    /// <see cref="LastServerRequestError"/>. This is a SEPARATE read loop concern from
    /// <see cref="RunAsync"/>'s main dispatch (which handles requests ARRIVING from the connection
    /// under test) — the fake's OWN read loop, already running via <see cref="RunAsync"/>, also
    /// observes the connection's reply to THIS request as an ordinary incoming "response" frame
    /// (has <c>id</c> + <c>result</c>/<c>error</c>, no <c>method</c>) and records it here via a
    /// short-lived local completion source correlated by id.
    /// </summary>
    readonly Dictionary<long, TaskCompletionSource<(JsonElement? Result, JsonElement? Error)>> _pendingFakeRequests = new();

    async Task SendServerRequestAndAwaitResponseAsync(string method, string toolCallJson, string optionsJson, CancellationToken ct) {
        var id    = Interlocked.Increment(ref _nextServerRequestId);
        var frame = BuildRequestPermissionFrame(id, FixedSessionId, toolCallJson, optionsJson);

        var tcs = new TaskCompletionSource<(JsonElement?, JsonElement?)>(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (_sentServerRequestsLock) {
            _pendingFakeRequests[id] = tcs;
            _sentServerRequests.Add((method, frame.GetProperty("params")));
        }

        await WriteRawFrameAsync(frame, ct).ConfigureAwait(false);

        var (result, error) = await tcs.Task.ConfigureAwait(false);
        _lastServerRequestResponse = result;
        _lastServerRequestError    = error;
    }

    void Record(string method, JsonElement? @params) {
        lock (_receivedCallsLock)
            _receivedCalls.Add((method, @params));

        // Fire the per-stage handshake signals so a test can await a stage being provably RECEIVED
        // instead of polling ReceivedCalls on a wall-clock bound (deterministic; windows-race-free).
        if (method == "initialize")       _initializeReceived.TrySetResult();
        else if (method == "session/new") _sessionNewReceived.TrySetResult();
    }

    // ---- probe-confirmed canned response shapes (docs/acp-probe-findings.md) ----

    static readonly JsonElement ProbeConfirmedInitializeResult = JsonDocument.Parse("""
        {
          "protocolVersion": 1,
          "agentCapabilities": {
            "loadSession": true,
            "mcpCapabilities": { "http": true, "sse": true },
            "promptCapabilities": { "audio": false, "embeddedContext": false, "image": true },
            "sessionCapabilities": { "list": {} }
          },
          "authMethods": [
            {
              "id": "cursor_login",
              "name": "Cursor Login",
              "description": "Authenticate using existing Cursor login credentials. Run 'agent login' first if not logged in."
            }
          ]
        }
        """).RootElement.Clone();

    static readonly JsonElement ProbeConfirmedSessionNewResult = JsonDocument.Parse($$"""
        {
          "sessionId": "{{FixedSessionId}}",
          "modes": {
            "currentModeId": "agent",
            "availableModes": [
              { "id": "agent", "name": "Agent", "description": "Full agent capabilities with tool access" },
              { "id": "plan", "name": "Plan", "description": "Read-only mode for planning and designing before implementation" },
              { "id": "ask", "name": "Ask", "description": "Q&A mode - no edits or command execution" }
            ]
          },
          "models": {
            "currentModelId": "composer-2.5[fast=true]",
            "availableModels": [
              { "modelId": "composer-2.5[fast=true]", "name": "composer-2.5" }
            ]
          },
          "configOptions": []
        }
        """).RootElement.Clone();

    /// <summary>
    /// Builds a full <c>session/update</c> notification frame (probe-confirmed envelope shape):
    /// <c>{"jsonrpc":"2.0","method":"session/update","params":{"sessionId":"...","update":{...}}}</c>.
    /// <paramref name="update"/> is the inner <c>update</c> object (e.g. from
    /// <see cref="BuildAgentMessageChunkUpdate"/>).
    /// </summary>
    public static JsonElement BuildSessionUpdateNotification(string sessionId, JsonElement update) {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream)) {
            writer.WriteStartObject();
            writer.WriteString("jsonrpc", "2.0");
            writer.WriteString("method", "session/update");
            writer.WriteStartObject("params");
            writer.WriteString("sessionId", sessionId);
            writer.WritePropertyName("update");
            update.WriteTo(writer);
            writer.WriteEndObject();
            writer.WriteEndObject();
        }

        return JsonDocument.Parse(stream.ToArray()).RootElement.Clone();
    }

    /// <summary>
    /// Probe-confirmed <c>agent_message_chunk</c> update variant:
    /// <c>{"sessionUpdate":"agent_message_chunk","content":{"type":"text","text":"..."}}</c>.
    /// </summary>
    public static JsonElement BuildAgentMessageChunkUpdate(string text) {
        var escaped = JsonEncodedText.Encode(text);
        return JsonDocument.Parse($$$"""{"sessionUpdate":"agent_message_chunk","content":{"type":"text","text":"{{{escaped}}}"}}""")
            .RootElement.Clone();
    }

    /// <summary>Convenience: a full default <c>session/update</c> notification frame carrying one
    /// probe-confirmed <c>agent_message_chunk</c> for <paramref name="sessionId"/>.</summary>
    public static JsonElement DefaultAgentMessageChunkUpdate(string sessionId, string text) =>
        BuildSessionUpdateNotification(sessionId, BuildAgentMessageChunkUpdate(text));

    /// <summary>
    /// Probe-confirmed <c>available_commands_update</c> variant:
    /// <c>{"sessionUpdate":"available_commands_update","availableCommands":[{"name":"...","description":"..."}]}</c>.
    /// <paramref name="commands"/> entries are <c>(name, description)</c> pairs.
    /// </summary>
    public static JsonElement BuildAvailableCommandsUpdate(IEnumerable<(string Name, string Description)> commands) {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream)) {
            writer.WriteStartObject();
            writer.WriteString("sessionUpdate", "available_commands_update");
            writer.WriteStartArray("availableCommands");
            foreach (var (name, description) in commands) {
                writer.WriteStartObject();
                writer.WriteString("name", name);
                writer.WriteString("description", description);
                writer.WriteEndObject();
            }
            writer.WriteEndArray();
            writer.WriteEndObject();
        }

        return JsonDocument.Parse(stream.ToArray()).RootElement.Clone();
    }

    /// <summary>
    /// Probe-confirmed <c>session_info_update</c> variant (agent session auto-titling):
    /// <c>{"sessionUpdate":"session_info_update","title":"..."}</c> — the shape captured verbatim
    /// from a real <c>cursor-agent acp</c> session in the Cursor prototype findings doc.
    /// </summary>
    public static JsonElement BuildSessionInfoUpdate(string title) {
        var escaped = JsonEncodedText.Encode(title);
        return JsonDocument.Parse($$$"""{"sessionUpdate":"session_info_update","title":"{{{escaped}}}"}""")
            .RootElement.Clone();
    }

    // ---- spec-derived (NOT probe-confirmed) helper builders ----
    //
    // The probe account was plan-gated before any tool-call turn completed, so none of the
    // variants below were ever observed on the wire (see docs/acp-probe-findings.md, "sessionUpdate
    // variants observed" and "Recommended follow-up"). They are built from the published ACP spec
    // only. Re-verify each against docs/acp-probe-findings.md's "Recommended follow-up" once a
    // non-plan-gated probe run is available, before relying on their exact field shapes in
    // production mapping code.

    /// <summary>
    /// Spec-derived, NOT yet verified against cursor-agent (the probe account was plan-gated before
    /// any tool-call turn completed) — re-verify against docs/acp-probe-findings.md "Recommended
    /// follow-up" once available. Shape: <c>{"sessionUpdate":"agent_thought_chunk","content":{"type":"text","text":"..."}}</c>.
    /// </summary>
    public static JsonElement BuildAgentThoughtChunkUpdate(string text) {
        var escaped = JsonEncodedText.Encode(text);
        return JsonDocument.Parse($$$"""{"sessionUpdate":"agent_thought_chunk","content":{"type":"text","text":"{{{escaped}}}"}}""")
            .RootElement.Clone();
    }

    /// <summary>
    /// Spec-derived, NOT yet verified against cursor-agent (the probe account was plan-gated before
    /// any tool-call turn completed) — re-verify against docs/acp-probe-findings.md "Recommended
    /// follow-up" once available. Shape: <c>{"sessionUpdate":"tool_call","toolCallId":"...","title":"...","kind":"...","status":"...","rawInput":{...}}</c>
    /// (<c>rawInput</c> only when <paramref name="rawInputJson"/> is non-null — task 1's
    /// <c>AcpSessionUpdate.Reduce()</c> extraction target for <c>ToolInputJson</c>).
    /// <paramref name="status"/> is one of <c>pending</c> / <c>in_progress</c> / <c>completed</c> / <c>failed</c>.
    /// </summary>
    public static JsonElement BuildToolCallUpdate(string toolCallId, string title, string kind, string status, string? rawInputJson = null) {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream)) {
            writer.WriteStartObject();
            writer.WriteString("sessionUpdate", "tool_call");
            writer.WriteString("toolCallId", toolCallId);
            writer.WriteString("title", title);
            writer.WriteString("kind", kind);
            writer.WriteString("status", status);
            if (rawInputJson is not null) {
                writer.WritePropertyName("rawInput");
                using var doc = JsonDocument.Parse(rawInputJson);
                doc.RootElement.WriteTo(writer);
            }
            writer.WriteEndObject();
        }

        return JsonDocument.Parse(stream.ToArray()).RootElement.Clone();
    }

    /// <summary>
    /// Spec-derived, NOT yet verified against cursor-agent (the probe account was plan-gated before
    /// any tool-call turn completed) — re-verify against docs/acp-probe-findings.md "Recommended
    /// follow-up" once available. Shape: <c>{"sessionUpdate":"tool_call_update","toolCallId":"...","status":"...","content":[{"type":"content","content":{"type":"text","text":"..."}}],"rawOutput":{...}}</c>
    /// — <c>content</c>/<c>rawOutput</c> are included only when <paramref name="resultText"/>/
    /// <paramref name="rawOutputJson"/> are non-null (task 1's
    /// <c>AcpSessionUpdate.Reduce()</c> extraction targets for <c>ToolResultText</c>: the ACP-spec
    /// <c>ToolCallContent</c> text-block shape, falling back to <c>rawOutput</c> verbatim).
    /// </summary>
    public static JsonElement BuildToolCallStatusUpdate(string toolCallId, string status, string? resultText = null, string? rawOutputJson = null) {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream)) {
            writer.WriteStartObject();
            writer.WriteString("sessionUpdate", "tool_call_update");
            writer.WriteString("toolCallId", toolCallId);
            writer.WriteString("status", status);
            if (resultText is not null) {
                writer.WriteStartArray("content");
                writer.WriteStartObject();
                writer.WriteString("type", "content");
                writer.WriteStartObject("content");
                writer.WriteString("type", "text");
                writer.WriteString("text", resultText);
                writer.WriteEndObject();
                writer.WriteEndObject();
                writer.WriteEndArray();
            }
            if (rawOutputJson is not null) {
                writer.WritePropertyName("rawOutput");
                using var doc = JsonDocument.Parse(rawOutputJson);
                doc.RootElement.WriteTo(writer);
            }
            writer.WriteEndObject();
        }

        return JsonDocument.Parse(stream.ToArray()).RootElement.Clone();
    }

    /// <summary>
    /// Spec-derived, NOT yet verified against cursor-agent (the probe account was plan-gated before
    /// any tool-call turn completed) — re-verify against docs/acp-probe-findings.md "Recommended
    /// follow-up" once available. Shape: <c>{"sessionUpdate":"plan","entries":[...]}</c>, where
    /// <paramref name="entriesJson"/> is the raw JSON text of the entries array (kept as a raw string
    /// since the ACP spec's plan-entry shape is not yet pinned down by this probe).
    /// </summary>
    public static JsonElement BuildPlanUpdate(string entriesJson) {
        var json = $$"""{"sessionUpdate":"plan","entries":{{entriesJson}}}""";
        return JsonDocument.Parse(json).RootElement.Clone();
    }

    /// <summary>
    /// Spec-derived, NOT yet verified against cursor-agent — <c>session/request_permission</c> was
    /// never observed in the probe (see docs/acp-probe-findings.md "Permission / elicitation
    /// requests"); re-verify once a non-plan-gated probe run is available. Builds the FULL
    /// server→client request frame (has its own <paramref name="id"/> and method, unlike the
    /// <c>update</c>-variant helpers above): params shape
    /// <c>{"sessionId":"...","toolCall":{...},"options":[{"optionId":"...","name":"...","kind":"..."}]}</c>.
    /// <paramref name="toolCallJson"/> and <paramref name="optionsJson"/> are raw JSON text for the
    /// nested objects, left as caller-supplied strings since their exact shape is unconfirmed.
    /// Task 8 does not wire this into <see cref="RunAsync"/>'s dispatch loop — a documented builder
    /// is sufficient for this fixture's scope; Task 9 wires the actual permission bridge.
    /// The two possible client response shapes are documented on
    /// <see cref="PermissionOutcomeSelected"/> / <see cref="PermissionOutcomeCancelled"/>.
    /// </summary>
    public static JsonElement BuildRequestPermissionFrame(long id, string sessionId, string toolCallJson, string optionsJson) {
        var json = $$$"""
            {"jsonrpc":"2.0","id":{{{id}}},"method":"session/request_permission","params":{"sessionId":"{{{sessionId}}}","toolCall":{{{toolCallJson}}},"options":{{{optionsJson}}}}}
            """;
        return JsonDocument.Parse(json).RootElement.Clone();
    }

    /// <summary>
    /// Spec-derived client response shape for an ALLOWED <c>session/request_permission</c>:
    /// <c>{"outcome":{"outcome":"selected","optionId":"..."}}</c>. NOT probe-confirmed — see
    /// <see cref="BuildRequestPermissionFrame"/> remarks.
    /// </summary>
    public static JsonElement PermissionOutcomeSelected(string optionId) =>
        JsonDocument.Parse($$$"""{"outcome":{"outcome":"selected","optionId":"{{{optionId}}}"}}""").RootElement.Clone();

    /// <summary>
    /// Spec-derived client response shape for a DENIED/cancelled <c>session/request_permission</c>:
    /// <c>{"outcome":{"outcome":"cancelled"}}</c>. NOT probe-confirmed — see
    /// <see cref="BuildRequestPermissionFrame"/> remarks.
    /// </summary>
    public static JsonElement PermissionOutcomeCancelled() =>
        JsonDocument.Parse("""{"outcome":{"outcome":"cancelled"}}""").RootElement.Clone();

    // ---- wire plumbing ----

    async Task WriteResponseAsync(JsonElement id, JsonElement result, CancellationToken ct) {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream)) {
            writer.WriteStartObject();
            writer.WriteString("jsonrpc", "2.0");
            writer.WritePropertyName("id");
            id.WriteTo(writer);
            writer.WritePropertyName("result");
            result.WriteTo(writer);
            writer.WriteEndObject();
        }

        await WriteLineAsync(Encoding.UTF8.GetString(stream.ToArray()), ct).ConfigureAwait(false);
    }

    async Task WriteErrorResponseAsync(JsonElement id, int code, string message, CancellationToken ct) {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream)) {
            writer.WriteStartObject();
            writer.WriteString("jsonrpc", "2.0");
            writer.WritePropertyName("id");
            id.WriteTo(writer);
            writer.WriteStartObject("error");
            writer.WriteNumber("code", code);
            writer.WriteString("message", message);
            writer.WriteEndObject();
            writer.WriteEndObject();
        }

        await WriteLineAsync(Encoding.UTF8.GetString(stream.ToArray()), ct).ConfigureAwait(false);
    }

    /// <summary>Sends a raw frame (e.g. an unsolicited <c>session/update</c> notification) directly,
    /// bypassing every scripted dispatch path — <c>internal</c> (not <c>private</c>) so a test can
    /// drive a notification with no accompanying turn/prompt at all.</summary>
    internal async Task WriteRawFrameAsync(JsonElement frame, CancellationToken ct) =>
        await WriteLineAsync(frame.GetRawText(), ct).ConfigureAwait(false);

    async Task WriteLineAsync(string json, CancellationToken ct) {
        var bytes = Encoding.UTF8.GetBytes(json + "\n");
        await _agentWritesToConnection.WriteAsync(bytes, ct).ConfigureAwait(false);
        await _agentWritesToConnection.FlushAsync(ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Qodo daemon-review Q3: disposes the fixture's streams as before, then — if a fire-and-forget
    /// <c>DispatchLineAsync</c> dispatch ever faulted (see <see cref="DispatchFault"/>) — rethrows
    /// that FIRST captured fault with its original stack trace, so a test that tore this fixture
    /// down via <c>await using</c> without explicitly checking <see cref="DispatchFault"/> still
    /// fails loudly instead of the fault being silently dropped. Stream disposal always runs first
    /// (best-effort cleanup must not be skipped just because a fault will be rethrown after).
    /// </summary>
    public async ValueTask DisposeAsync() {
        await _agentReadsFromConnection.DisposeAsync().ConfigureAwait(false);
        await _agentWritesToConnection.DisposeAsync().ConfigureAwait(false);
        await ClientWriteStream.DisposeAsync().ConfigureAwait(false);
        await ClientReadStream.DisposeAsync().ConfigureAwait(false);

        Volatile.Read(ref _dispatchFault)?.Throw();
    }
}
