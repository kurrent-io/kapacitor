using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;
using Capacitor.Cli.Core;
using Capacitor.Cli.Core.Acp;
using Microsoft.Extensions.Logging;

namespace Capacitor.Cli.Daemon.Acp;

/// <summary>
/// JSON-RPC 2.0 error surfaced from an ACP response's <c>error</c> object (see
/// <see cref="AcpError"/>). Thrown by <see cref="AcpConnection.RequestAsync"/> when the agent
/// answers a request with an error instead of a result.
/// </summary>
internal sealed class AcpRpcException : Exception {
    public int          Code      { get; }
    public JsonElement? ErrorData { get; }

    public AcpRpcException(int code, string message, JsonElement? data = null) : base(message) {
        Code      = code;
        ErrorData = data;
    }
}

/// <summary>
/// Newline-delimited JSON-RPC 2.0 stdio transport for <c>cursor-agent acp</c>.
/// Owns framing (one JSON object per line, UTF-8), outbound request/response correlation, and
/// routing of inbound notifications and server→client requests. Decoupled from <see cref="System.Diagnostics.Process"/>
/// — the ctor takes plain <see cref="Stream"/>s so tests can drive it over in-memory pipes; the
/// real caller (<c>AcpHostedAgentRuntime</c>, Task 9) passes the child process's stdin/stdout.
///
/// Concurrency model:
/// - Outbound id allocation is an <see cref="Interlocked"/> counter.
/// - Pending requests live in a <see cref="ConcurrentDictionary{TKey,TValue}"/> keyed by that id,
///   each entry a <see cref="TaskCompletionSource{TResult}"/> created with
///   <see cref="TaskCreationOptions.RunContinuationsAsynchronously"/> so completing/faulting it from
///   the read loop never runs the awaiter's continuation inline on the read-loop thread.
/// - All outbound bytes (requests, notifications, and responses to server-initiated requests) go
///   through a single <see cref="SemaphoreSlim"/> so concurrent callers never interleave partial
///   frames on the wire.
/// - There is exactly one read loop (<see cref="RunAsync"/>); it dispatches by shape and must never
///   exit early on a single bad line (a wire hiccup would otherwise silently wedge every pending
///   and future <see cref="RequestAsync"/> call).
///
/// Per the probe (<c>docs/acp-probe-findings.md</c>), ACP has no per-request
/// <c>$/cancelRequest</c> frame — cancellation is session-level via the <c>session/cancel</c>
/// notification, which the runtime sends through <see cref="NotifyAsync"/>. Cancelling the
/// <see cref="CancellationToken"/> passed to <see cref="RequestAsync"/> only abandons the pending
/// call on the client side (removes the correlation entry and faults the awaiter); it does not by
/// itself tell the agent to stop working.
/// </summary>
internal sealed partial class AcpConnection : IAsyncDisposable {
    readonly Stream                                                  _writeStream;
    readonly Stream                                                  _readStream;
    readonly ILogger                                                 _logger;
    readonly bool                                                    _debugFrames;
    readonly ConcurrentDictionary<long, TaskCompletionSource<JsonElement>> _pending = new();
    readonly SemaphoreSlim                                           _writeGate = new(1, 1);

    long     _nextId;
    int      _disposed;
    volatile bool _transportEnded;

    /// <summary>
    /// Monotonic per-connection latch: true once this connection's transport is known unusable —
    /// set as the FIRST act of every trigger path (read loop ending, or a failure of the stream
    /// write/flush itself), strictly before <see cref="BeforeFaultingPending"/> is invoked. The
    /// reconnect commit reads this (not the read-loop task's completion state) as the authoritative
    /// death fact for a candidate whose only hook invocation fired while it was still uninstalled
    /// and was therefore rightly discarded: an uninstalled callback stays globally inert, but the
    /// connection itself remembers the terminal fact locally, forever.
    /// </summary>
    public bool TransportEnded => _transportEnded;

    /// <summary>
    /// Optional pre-fault hook invoked when this connection's transport ends — BEFORE the read
    /// loop's <c>finally</c> faults pending requests, and from a failed stream write. The runtime
    /// wires it (stamped with this connection's incarnation id via closure) so `Reconnecting` is
    /// set before any prompt-fault continuation can run. Contract: the callee must be synchronous
    /// and non-blocking (it may take only fast-state locks); this connection invokes it at most
    /// per trigger path, guarded by the <see cref="TransportEnded"/> latch so the write-failure
    /// path and the read loop's own end produce one latch flip and at most two invocations that
    /// the callee's own idempotence collapses.
    /// </summary>
    public Action? BeforeFaultingPending { get; set; }

    void MarkTransportEnded() {
        _transportEnded = true;

        try {
            BeforeFaultingPending?.Invoke();
        } catch (Exception ex) {
            // The hook is runtime code and must not take down the read loop or a writer; a throwing
            // hook is a bug, but the connection's own teardown must still proceed.
            _logger.LogDebug(ex, "ACP: BeforeFaultingPending hook threw; continuing teardown.");
        }
    }

    /// <param name="debugFrames">
    /// <c>KCAP_ACP_DEBUG_FRAMES</c> (<see cref="DaemonConfig.DebugFrames"/>) — off by default. When
    /// on, every inbound/outbound JSON-RPC frame is ALSO logged in full (length-capped) at Debug
    /// (<see cref="LogInboundFrame"/>/<see cref="LogOutboundFrame"/>); the existing shape-only Debug
    /// logging in <see cref="DispatchLineAsync"/> is unchanged either way.
    /// </param>
    public AcpConnection(Stream writeStream, Stream readStream, ILogger logger, bool debugFrames = false) {
        _writeStream = writeStream;
        _readStream  = readStream;
        _logger      = logger;
        _debugFrames = debugFrames;
    }

    /// <summary>Raised for inbound agent→client notifications (e.g. <c>session/update</c>).</summary>
    public event Action<AcpNotification>? OnNotification;

    /// <summary>
    /// Handler for inbound agent→client REQUESTS (e.g. <c>session/request_permission</c>,
    /// <c>fs/*</c>, <c>terminal/*</c>). The read loop echoes the request's id verbatim in the
    /// response it writes back, with this delegate's return value as the JSON-RPC <c>result</c>.
    /// If unset, every inbound server request is answered with a method-not-found error — a safe
    /// default-decline posture. This constructor leaves it unset; permission-bridge wiring assigns it.
    ///
    /// A handler returning <see langword="null"/> signals the method is unhandled: the connection
    /// answers <c>-32601 Method not found</c>, the SAME response a fully unset handler produces —
    /// never a null-result success, which would falsely claim we performed an operation (e.g. an
    /// <c>fs/*</c>/<c>terminal/*</c> request) we never actually served. A handler that intends a
    /// successful EMPTY result must return an explicit <see cref="JsonElement"/> (e.g. an empty
    /// object via <see cref="JsonSerializer.SerializeToElement{T}(T, System.Text.Json.Serialization.Metadata.JsonTypeInfo{T})"/>), never <see langword="null"/>.
    ///
    /// Typed <see cref="JsonElement"/>? rather than <c>object?</c> (PR #244 review, Fix #3): the
    /// old <c>object?</c> contract let a handler return an un-serialized CLR object that
    /// <see cref="WriteServerResponseAsync"/> could only reject with a thrown
    /// <see cref="InvalidOperationException"/> — which happened OUTSIDE any try/catch here, so it
    /// propagated up through <see cref="DispatchLineAsync"/>'s broad catch (log-and-skip) and the
    /// agent's request was left with NO response at all, wedging its wait on this id forever. A
    /// handler must now build its own <see cref="JsonElement"/> (typically via
    /// <see cref="JsonSerializer.SerializeToElement{T}(T, System.Text.Json.Serialization.Metadata.JsonTypeInfo{T})"/> against a registered
    /// <see cref="CapacitorJsonContext"/> type) so the shape is AOT-safe and can't fail to
    /// serialize at the write site.
    /// </summary>
    public Func<AcpRequest, CancellationToken, Task<JsonElement?>>? OnServerRequest { get; set; }

    /// <summary>
    /// Sends a request and awaits its correlated response. Throws <see cref="AcpRpcException"/> if
    /// the agent answers with an error. If <paramref name="ct"/> is cancelled first, the pending
    /// correlation is removed and this throws <see cref="OperationCanceledException"/> — ACP has no
    /// per-request cancel frame (see class remarks); session-level cancellation is a separate
    /// explicit <c>session/cancel</c> notification via <see cref="NotifyAsync"/>.
    /// </summary>
    public async Task<JsonElement> RequestAsync(
            string method, JsonElement? @params, CancellationToken ct, Action? onWritten = null) {
        var id  = Interlocked.Increment(ref _nextId);
        var tcs = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);

        if (!_pending.TryAdd(id, tcs))
            throw new InvalidOperationException($"Duplicate ACP request id {id} — id allocation is broken.");

        await using var registration = ct.Register(() => {
            _pending.TryRemove(id, out _);
            tcs.TrySetCanceled(ct);
        }).ConfigureAwait(false);

        var request = new AcpRequest(id, method, @params);
        var json    = JsonSerializer.Serialize(request, CapacitorJsonContext.Default.AcpRequest);

        try {
            await WriteLineAsync(json, ct).ConfigureAwait(false);
            onWritten?.Invoke();
        } catch {
            _pending.TryRemove(id, out _);
            tcs.TrySetCanceled(CancellationToken.None);
            throw;
        }

        return await tcs.Task.ConfigureAwait(false);
    }

    /// <summary>Fire-and-forget notification (no id, no response expected). Used by the runtime for
    /// <c>session/cancel</c> — the only client→agent cancellation surface ACP defines.</summary>
    public async Task NotifyAsync(string method, JsonElement? @params) {
        var notification = new AcpNotification(method, @params);
        var json         = JsonSerializer.Serialize(notification, CapacitorJsonContext.Default.AcpNotification);

        await WriteLineAsync(json, CancellationToken.None).ConfigureAwait(false);
    }

    /// <summary>
    /// The read loop: parses newline-delimited JSON frames from the agent and dispatches by shape.
    /// Runs until <paramref name="ct"/> is cancelled or the stream ends. A single malformed or
    /// unrecognized line is logged at debug and skipped — it must never take down the loop, since
    /// every other pending <see cref="RequestAsync"/> call depends on this loop staying alive.
    /// </summary>
    public async Task RunAsync(CancellationToken ct) {
        using var reader = new StreamReader(_readStream, Encoding.UTF8, detectEncodingFromByteOrderMarks: false, leaveOpen: true);

        try {
            while (!ct.IsCancellationRequested) {
                var line = await reader.ReadLineAsync(ct).ConfigureAwait(false);
                if (line is null)
                    break; // stream ended (agent process exited / pipe closed)

                if (string.IsNullOrWhiteSpace(line))
                    continue;

                await DispatchLineAsync(line, ct).ConfigureAwait(false);
            }
        } catch (OperationCanceledException) when (ct.IsCancellationRequested) {
            // normal shutdown
        } finally {
            // Latch + pre-fault hook run BEFORE pending requests fault: the runtime must observe
            // "reconnecting" before the faulted prompt's continuation (and its finally-flush) can
            // run, or the two race (design-of-record C6.2; reconnect spec §5.2).
            MarkTransportEnded();
            FaultAllPending(new ObjectDisposedException(nameof(AcpConnection), "ACP connection read loop ended with requests still pending."));
        }
    }

    async Task DispatchLineAsync(string line, CancellationToken ct) {
        // KCAP_ACP_DEBUG_FRAMES gate (Off by default) — an ACP frame can carry prompt/tool/file
        // content, so the FULL frame is only ever logged when the operator has explicitly opted in.
        // This is additive: the shape-only Debug logging further down (unparseable/unrecognized/
        // wrong-typed frames) is unchanged regardless of this flag.
        if (_debugFrames)
            LogInboundFrame(AcpDebugFrameLog.Cap(line));

        JsonDocument doc;

        try {
            doc = JsonDocument.Parse(line);
        } catch (JsonException ex) {
            _logger.LogDebug(ex, "ACP: skipping unparseable line ({Length} chars)", line.Length);
            return;
        }

        // Diagnostics captured up front (cheap ValueKind reads, never the actual method/id/params
        // VALUES) so the catch below can log them without touching `doc` after it may already have
        // been disposed by the `using` block.
        var methodKind = "<absent>";
        var idKind     = "<absent>";

        try {
            using (doc) {
                var root = doc.RootElement;

                if (root.ValueKind != JsonValueKind.Object) {
                    _logger.LogDebug("ACP: skipping non-object frame (kind={Kind})", root.ValueKind);
                    return;
                }

                var hasId     = root.TryGetProperty("id", out var idElement);
                var hasMethod = root.TryGetProperty("method", out var methodElement);
                var hasResult = root.TryGetProperty("result", out var resultElement);
                var hasError  = root.TryGetProperty("error", out var errorElement);

                methodKind = hasMethod ? methodElement.ValueKind.ToString() : "<absent>";
                idKind     = hasId ? idElement.ValueKind.ToString() : "<absent>";

                if (hasId && (hasResult || hasError) && !hasMethod) {
                    HandleResponse(idElement, hasResult ? resultElement : null, hasError ? errorElement : null);
                    return;
                }

                if (hasMethod && hasId) {
                    await HandleServerRequestAsync(root, idElement, methodElement, ct).ConfigureAwait(false);
                    return;
                }

                if (hasMethod && !hasId) {
                    HandleNotification(root, methodElement);
                    return;
                }

                _logger.LogDebug("ACP: skipping frame with unrecognized shape");
            }
        } catch (Exception ex) when (ex is not OperationCanceledException) {
            // A well-formed JSON frame can still have a field of the wrong JSON type (e.g. a numeric
            // `method` or a string `error.code`), which throws InvalidOperationException/FormatException
            // out of GetString()/GetInt32()/etc. after the parse already succeeded. This must not take
            // down the read loop — every pending and future RequestAsync call depends on it staying
            // alive — so we log the frame's shape only (never params/result/content, which may carry
            // sensitive payloads) and skip the bad frame. OperationCanceledException is rethrown so
            // loop-cancellation shutdown stays clean.
            _logger.LogDebug(ex, "ACP: skipping frame with wrong-typed field (method.kind={MethodKind}, id.kind={IdKind})", methodKind, idKind);
        }
    }

    void HandleResponse(JsonElement idElement, JsonElement? resultElement, JsonElement? errorElement) {
        if (!idElement.TryGetInt64(out var id)) {
            _logger.LogDebug("ACP: response id is not a numeric value we issued; ignoring");
            return;
        }

        if (!_pending.TryRemove(id, out var tcs)) {
            _logger.LogDebug("ACP: no pending request for response id={Id}; ignoring", id);
            return;
        }

        if (errorElement is { } error) {
            // TryRemove already took the pending TCS above — from here on we MUST complete it,
            // no matter how malformed `error` turns out to be. A well-formed JSON-RPC frame can
            // still carry a wrong-typed `error` (a non-object value, or an object whose `code`
            // isn't a number / whose `message` isn't a string): the old code used the throwing
            // GetInt32()/GetString() accessors here, and DispatchLineAsync's broad catch would
            // log+skip the frame — leaving this TCS orphaned and its caller hanging until the
            // connection disposed. Every read below is TryGetProperty + ValueKind-gated so this
            // block can never throw (PR #244 review, Fix D).
            var code = error.ValueKind == JsonValueKind.Object
                    && error.TryGetProperty("code", out var codeEl)
                    && codeEl.ValueKind == JsonValueKind.Number
                    && codeEl.TryGetInt32(out var codeValue)
                ? codeValue
                : 0;

            var message = error.ValueKind == JsonValueKind.Object
                    && error.TryGetProperty("message", out var msgEl)
                    && msgEl.ValueKind == JsonValueKind.String
                ? msgEl.GetString() ?? ""
                : "";

            JsonElement? data = error.ValueKind == JsonValueKind.Object
                    && error.TryGetProperty("data", out var dataEl)
                ? dataEl.Clone()
                : null;

            tcs.TrySetException(new AcpRpcException(code, message, data));
            return;
        }

        var result = resultElement?.Clone() ?? default;
        tcs.TrySetResult(result);
    }

    /// <summary>
    /// Handles one inbound agent→client request. MUST write exactly one response frame for
    /// <paramref name="idElement"/> no matter what happens — a missing response wedges the agent's
    /// wait on this id forever (PR #244 review, Fix #3). The handler-invoke try/catch (existing)
    /// and the outer try/catch around response serialization+write (new) together guarantee this:
    /// any failure — a thrown handler, or a result that fails to serialize/write — falls back to a
    /// JSON-RPC "Internal error" response keyed on the ORIGINAL request id, rather than letting the
    /// exception escape to <see cref="DispatchLineAsync"/>'s log-and-skip catch.
    /// </summary>
    async Task HandleServerRequestAsync(JsonElement root, JsonElement idElement, JsonElement methodElement, CancellationToken ct) {
        var method       = methodElement.GetString() ?? "";
        var paramsElement = root.TryGetProperty("params", out var p) ? p.Clone() : (JsonElement?) null;

        // Preserve the raw id JsonElement verbatim — inbound ids from the agent are not guaranteed
        // to fit our own `long` id space (spec allows string/number), so we must not force a parse
        // that could throw and kill the read loop.
        var idClone = idElement.Clone();

        JsonElement? result;
        AcpError?    error = null;

        var handler = OnServerRequest;
        if (handler is null) {
            error  = new AcpError(-32601, $"Method not found: {method}", null);
            result = null;
        } else {
            // AcpRequest.Id is `long`-typed but an inbound server-request id is an arbitrary JSON
            // value (string or number) that may not fit `long`. The handler contract only needs
            // Method/Params — it never reads Id back off this record — so a placeholder 0 here is
            // safe. The response written back to the wire uses `idClone`, the ORIGINAL raw
            // JsonElement, never this placeholder.
            var request = new AcpRequest(0, method, paramsElement);

            try {
                result = await handler(request, ct).ConfigureAwait(false);
            } catch (Exception ex) {
                _logger.LogDebug(ex, "ACP: OnServerRequest handler threw for method={Method}", method);
                error  = new AcpError(-32603, "Internal error", null);
                result = null;
            }

            // A handler that ran without throwing but returned null means "I don't handle this
            // method" (e.g. AcpInteractionBridge's `_ => null` default for fs/*, terminal/*) — treat
            // it exactly like the no-handler branch above, never a null-result success that would
            // falsely acknowledge an operation we never performed.
            if (result is null && error is null) {
                _logger.LogDebug("ACP: OnServerRequest handler declined method={Method}; responding -32601 Method not found", method);
                error = new AcpError(-32601, $"Method not found: {method}", null);
            }
        }

        try {
            // the final response write deliberately uses CancellationToken.None, not `ct` —
            // this method's own doc comment guarantees exactly one response frame is written "no
            // matter what happens", and `ct` is the SAME token AcpHostedAgentRuntime.DisposeAsync
            // cancels to unblock a pending OnServerRequest handler (e.g. AcpInteractionBridge
            // resolving to a well-formed `cancelled` result on disconnect — Task B3/B4). Gating the
            // WRITE itself on that already-cancelled token would make WriteLineAsync's write-gate
            // wait (`_writeGate.WaitAsync(ct)`) throw OperationCanceledException before a single byte
            // goes out, silently violating the "always exactly one response frame" invariant (the
            // exception propagates through this method's `when (ex is not OperationCanceledException)`
            // guard, then DispatchLineAsync's identical guard, and is swallowed by RunAsync's
            // "normal shutdown" catch) — the agent would see NO response at all instead of the
            // well-formed cancelled/error result the handler already computed. The write is a single
            // best-effort attempt against a stream that may itself be torn down (e.g. the process
            // already exited) — that failure mode is unrelated to `ct` and is exactly what the
            // fallback below still handles.
            await WriteServerResponseAsync(idClone, result, error, CancellationToken.None).ConfigureAwait(false);
        } catch (Exception ex) {
            // Serializing/writing the chosen response itself failed (e.g. a malformed JsonElement,
            // or the underlying stream already closed). The agent is still owed a response for this
            // id — never leave it log-and-skipped by DispatchLineAsync's outer catch — so fall back
            // to a minimal internal-error response. Best-effort: if even THIS write fails, there is
            // nothing left to try (the wire itself is broken and the read loop will observe that via
            // its own exception handling on the next read).
            _logger.LogDebug(ex, "ACP: failed to write server-request response for method={Method}; sending internal-error fallback", method);

            try {
                await WriteServerResponseAsync(idClone, null, new AcpError(-32603, "Internal error", null), CancellationToken.None).ConfigureAwait(false);
            } catch (Exception fallbackEx) {
                _logger.LogDebug(fallbackEx, "ACP: internal-error fallback response also failed to write for method={Method}", method);
            }
        }
    }

    void HandleNotification(JsonElement root, JsonElement methodElement) {
        var method        = methodElement.GetString() ?? "";
        var paramsElement = root.TryGetProperty("params", out var p) ? p.Clone() : (JsonElement?) null;

        OnNotification?.Invoke(new AcpNotification(method, paramsElement));
    }

    /// <summary>
    /// Writes the JSON-RPC response for one inbound server request. <paramref name="result"/> is
    /// already a <see cref="JsonElement"/>? by construction (the <see cref="OnServerRequest"/>
    /// delegate is typed <c>Task&lt;JsonElement?&gt;</c> — PR #244 review, Fix #3 — so there is no
    /// runtime shape to validate here, only to serialize+write; the caller
    /// (<see cref="HandleServerRequestAsync"/>) wraps this call so a failure here still yields an
    /// internal-error fallback response rather than none at all).
    /// </summary>
    async Task WriteServerResponseAsync(JsonElement idElement, JsonElement? result, AcpError? error, CancellationToken ct) {
        var json = SerializeRawIdResponse(idElement, result, error);
        await WriteLineAsync(json, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Builds a response frame manually (rather than via <see cref="AcpResponse"/>, which types
    /// <c>Id</c> as <see langword="long"/>) so an inbound server-request id of any JSON shape
    /// (string, number, etc.) round-trips byte-for-byte instead of being forced through a
    /// <see langword="long"/> parse that could throw.
    /// </summary>
    static string SerializeRawIdResponse(JsonElement idElement, JsonElement? result, AcpError? error) {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream)) {
            writer.WriteStartObject();
            writer.WriteString("jsonrpc", "2.0");
            writer.WritePropertyName("id");
            idElement.WriteTo(writer);

            if (error is not null) {
                writer.WritePropertyName("error");
                JsonSerializer.Serialize(writer, error, CapacitorJsonContext.Default.AcpError);
            } else {
                writer.WritePropertyName("result");
                if (result is { } r)
                    r.WriteTo(writer);
                else
                    writer.WriteNullValue();
            }

            writer.WriteEndObject();
        }

        return Encoding.UTF8.GetString(stream.ToArray());
    }

    async Task WriteLineAsync(string json, CancellationToken ct) {
        var bytes = Encoding.UTF8.GetBytes(json + "\n");

        await _writeGate.WaitAsync(ct).ConfigureAwait(false);
        try {
            try {
                await _writeStream.WriteAsync(bytes, ct).ConfigureAwait(false);
                await _writeStream.FlushAsync(ct).ConfigureAwait(false);
            } catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested) {
                // A failure of the stream write/flush ITSELF is transport evidence (broken pipe /
                // torn-down stream) and triggers the reconnect pre-fault path. Deliberately narrow
                // on the exclusion side too: only a cancellation the CALLER actually requested is a
                // local (non-transport) outcome — a stream throwing OperationCanceledException while
                // ct is un-cancelled is a dying transport wearing the wrong exception type
                // (code-review r1) and must latch like any other write failure. Gate-acquisition
                // cancellation and anything before this inner try (serialization happens in the
                // caller) never reach here.
                MarkTransportEnded();
                throw;
            }

            // Log the outbound frame only after it is actually on the wire — and still under the gate,
            // so the debug log order matches wire order. A cancelled/failed write skips this, never
            // leaving a line that implies a frame was sent when it wasn't.
            if (_debugFrames)
                LogOutboundFrame(AcpDebugFrameLog.Cap(json));
        } finally {
            _writeGate.Release();
        }
    }

    void FaultAllPending(Exception ex) {
        foreach (var id in _pending.Keys) {
            if (_pending.TryRemove(id, out var tcs))
                tcs.TrySetException(ex);
        }
    }

    public async ValueTask DisposeAsync() {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return;

        FaultAllPending(new ObjectDisposedException(nameof(AcpConnection)));

        _writeGate.Dispose();

        await _writeStream.DisposeAsync().ConfigureAwait(false);
        await _readStream.DisposeAsync().ConfigureAwait(false);
    }

    [LoggerMessage(Level = LogLevel.Debug, Message = "ACP <<< {Frame}")]
    partial void LogInboundFrame(string frame);

    [LoggerMessage(Level = LogLevel.Debug, Message = "ACP >>> {Frame}")]
    partial void LogOutboundFrame(string frame);
}
