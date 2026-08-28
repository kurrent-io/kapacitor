using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;
using Capacitor.Cli.Core;
using Capacitor.Cli.Core.Acp;
using Capacitor.Cli.Daemon.Acp;
using Microsoft.Extensions.Logging;

namespace Capacitor.Cli.Daemon.Harness.Codex;

/// <summary>
/// JSON-RPC 2.0 error surfaced from a <c>codex app-server</c> response's <c>error</c> object.
/// Thrown by <see cref="CodexAppServerConnection.RequestAsync"/> when the app-server answers a
/// request with an error instead of a result (e.g. an unsupported method, or an invalid turn on a
/// dead thread) so the runtime can classify the failure rather than see a bare result.
/// </summary>
internal sealed class CodexAppServerRpcException : Exception {
    public int          Code      { get; }
    public JsonElement? ErrorData { get; }

    public CodexAppServerRpcException(int code, string message, JsonElement? data = null) : base(message) {
        Code      = code;
        ErrorData = data;
    }
}

/// <summary>
/// Newline-delimited JSON-RPC 2.0 stdio transport for <c>codex app-server</c>. Owns framing (one
/// JSON object per line, UTF-8), outbound request/response correlation, and routing of inbound
/// notifications and server→client requests. Decoupled from
/// <see cref="System.Diagnostics.Process"/> — the ctor takes plain <see cref="Stream"/>s so tests
/// drive it over in-memory pipes; the runtime passes the child process's stdin/stdout.
///
/// The app-server speaks the SAME JSON-RPC 2.0 envelope as ACP, so this reuses the shared
/// <see cref="AcpRequest"/> / <see cref="AcpNotification"/> / <see cref="AcpError"/> wire records
/// (their names are historical — the layer is protocol-generic and AOT/trim-safe) rather than
/// defining byte-identical copies. It is a deliberately leaner sibling of
/// <see cref="AcpConnection"/>: the hosted Codex reviewer is a one-shot unattended run with no
/// reconnect design, so the ACP connection's reconnect latch / pre-fault hook are omitted — a dead
/// app-server simply ends the read loop, faults the pending requests, and the orchestrator reaps
/// the reviewer through the normal death path.
///
/// Concurrency model (identical to <see cref="AcpConnection"/>): an <see cref="Interlocked"/> id
/// counter, a <see cref="ConcurrentDictionary{TKey,TValue}"/> of pending requests each a
/// <see cref="TaskCompletionSource{TResult}"/> created
/// <see cref="TaskCreationOptions.RunContinuationsAsynchronously"/>, a single
/// <see cref="SemaphoreSlim"/> write-gate so concurrent callers never interleave partial frames,
/// and exactly one read loop (<see cref="RunAsync"/>) that must never exit early on a single bad
/// line.
/// </summary>
internal sealed partial class CodexAppServerConnection : IAsyncDisposable {
    readonly Stream                                                        _writeStream;
    readonly Stream                                                        _readStream;
    readonly ILogger                                                       _logger;
    readonly bool                                                          _debugFrames;
    readonly ConcurrentDictionary<long, TaskCompletionSource<JsonElement>> _pending = new();
    readonly SemaphoreSlim                                                 _writeGate = new(1, 1);

    long _nextId;
    int  _disposed;

    /// <param name="debugFrames">
    /// <see cref="DaemonConfig.DebugFrames"/> (<c>KCAP_ACP_DEBUG_FRAMES</c>) — off by default. When
    /// on, every inbound/outbound JSON-RPC frame is ALSO logged in full (length-capped) at Debug;
    /// the shape-only Debug logging in <see cref="DispatchLineAsync"/> is unchanged either way.
    /// </param>
    public CodexAppServerConnection(Stream writeStream, Stream readStream, ILogger logger, bool debugFrames = false) {
        _writeStream = writeStream;
        _readStream  = readStream;
        _logger      = logger;
        _debugFrames = debugFrames;
    }

    /// <summary>Raised for inbound app-server→client notifications (e.g. turn/item events,
    /// <c>codex/event/*</c>, <c>turn.completed</c>).</summary>
    public event Action<AcpNotification>? OnNotification;

    /// <summary>
    /// Handler for inbound app-server→client REQUESTS (e.g. approval requests, or hook-related
    /// prompts). The read loop echoes the request's id verbatim in the response, with this
    /// delegate's return value as the JSON-RPC <c>result</c>. If unset — or if the handler returns
    /// <see langword="null"/> — the connection answers <c>-32601 Method not found</c>, a safe
    /// default-decline posture (never a null-result success, which would falsely claim we performed
    /// an operation we never served). A handler must build its own <see cref="JsonElement"/> so the
    /// shape is AOT-safe and can't fail to serialize at the write site.
    /// </summary>
    public Func<AcpRequest, CancellationToken, Task<JsonElement?>>? OnServerRequest { get; set; }

    /// <summary>
    /// Sends a request and awaits its correlated response. Throws
    /// <see cref="CodexAppServerRpcException"/> if the app-server answers with an error. If
    /// <paramref name="ct"/> is cancelled first, the pending correlation is removed and this throws
    /// <see cref="OperationCanceledException"/>; a running turn is cancelled separately via the
    /// app-server's own interrupt method (<see cref="NotifyAsync"/> / <see cref="RequestAsync"/>).
    /// </summary>
    public async Task<JsonElement> RequestAsync(
            string method, JsonElement? @params, CancellationToken ct, Action? onWritten = null) {
        var id  = Interlocked.Increment(ref _nextId);
        var tcs = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);

        if (!_pending.TryAdd(id, tcs))
            throw new InvalidOperationException($"Duplicate app-server request id {id} — id allocation is broken.");

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

    /// <summary>Fire-and-forget notification (no id, no response expected).</summary>
    public async Task NotifyAsync(string method, JsonElement? @params) {
        var notification = new AcpNotification(method, @params);
        var json         = JsonSerializer.Serialize(notification, CapacitorJsonContext.Default.AcpNotification);

        await WriteLineAsync(json, CancellationToken.None).ConfigureAwait(false);
    }

    /// <summary>
    /// The read loop: parses newline-delimited JSON frames from the app-server and dispatches by
    /// shape. Runs until <paramref name="ct"/> is cancelled or the stream ends. A single malformed
    /// or unrecognized line is logged at debug and skipped — it must never take down the loop, since
    /// every pending <see cref="RequestAsync"/> call depends on this loop staying alive.
    /// </summary>
    public async Task RunAsync(CancellationToken ct) {
        using var reader = new StreamReader(_readStream, Encoding.UTF8, detectEncodingFromByteOrderMarks: false, leaveOpen: true);

        try {
            while (!ct.IsCancellationRequested) {
                var line = await reader.ReadLineAsync(ct).ConfigureAwait(false);
                if (line is null)
                    break; // stream ended (app-server process exited / pipe closed)

                if (string.IsNullOrWhiteSpace(line))
                    continue;

                await DispatchLineAsync(line, ct).ConfigureAwait(false);
            }
        } catch (OperationCanceledException) when (ct.IsCancellationRequested) {
            // normal shutdown
        } finally {
            FaultAllPending(new ObjectDisposedException(
                nameof(CodexAppServerConnection), "codex app-server connection read loop ended with requests still pending."));
        }
    }

    async Task DispatchLineAsync(string line, CancellationToken ct) {
        // KCAP_ACP_DEBUG_FRAMES gate (Off by default) — a frame can carry prompt/tool/file content,
        // so the FULL frame is only ever logged when the operator has explicitly opted in. The
        // shape-only Debug logging below is unchanged regardless of this flag.
        if (_debugFrames)
            LogInboundFrame(AcpDebugFrameLog.Cap(line));

        JsonDocument doc;

        try {
            doc = JsonDocument.Parse(line);
        } catch (JsonException ex) {
            _logger.LogDebug(ex, "app-server: skipping unparseable line ({Length} chars)", line.Length);
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
                    _logger.LogDebug("app-server: skipping non-object frame (kind={Kind})", root.ValueKind);
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

                _logger.LogDebug("app-server: skipping frame with unrecognized shape");
            }
        } catch (Exception ex) when (ex is not OperationCanceledException) {
            // A well-formed JSON frame can still have a field of the wrong JSON type (e.g. a numeric
            // `method` or a string `error.code`), which throws out of GetString()/GetInt32() after
            // the parse already succeeded. This must not take down the read loop — every pending
            // RequestAsync call depends on it staying alive — so we log the frame's shape only
            // (never params/result/content, which may carry sensitive payloads) and skip the frame.
            _logger.LogDebug(ex, "app-server: skipping frame with wrong-typed field (method.kind={MethodKind}, id.kind={IdKind})", methodKind, idKind);
        }
    }

    void HandleResponse(JsonElement idElement, JsonElement? resultElement, JsonElement? errorElement) {
        if (!idElement.TryGetInt64(out var id)) {
            _logger.LogDebug("app-server: response id is not a numeric value we issued; ignoring");
            return;
        }

        if (!_pending.TryRemove(id, out var tcs)) {
            _logger.LogDebug("app-server: no pending request for response id={Id}; ignoring", id);
            return;
        }

        if (errorElement is { } error) {
            // TryRemove already took the pending TCS — from here on we MUST complete it, no matter
            // how malformed `error` turns out to be. Every read below is TryGetProperty +
            // ValueKind-gated so this block can never throw and orphan the caller.
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

            tcs.TrySetException(new CodexAppServerRpcException(code, message, data));
            return;
        }

        var result = resultElement?.Clone() ?? default;
        tcs.TrySetResult(result);
    }

    /// <summary>
    /// Handles one inbound app-server→client request. MUST write exactly one response frame for
    /// <paramref name="idElement"/> no matter what happens — a missing response wedges the
    /// app-server's wait on this id forever. The handler-invoke try/catch and the outer try/catch
    /// around response serialization+write together guarantee this: any failure falls back to a
    /// JSON-RPC "Internal error" response keyed on the ORIGINAL id, rather than letting the
    /// exception escape to <see cref="DispatchLineAsync"/>'s log-and-skip catch.
    /// </summary>
    async Task HandleServerRequestAsync(JsonElement root, JsonElement idElement, JsonElement methodElement, CancellationToken ct) {
        // Preserve the raw id JsonElement verbatim — inbound ids are not guaranteed to fit our own
        // `long` id space (JSON-RPC allows string/number), so we must not force a parse that could
        // throw and kill the read loop.
        var idClone = idElement.Clone();

        JsonElement? result = null;
        AcpError?    error;

        // A wrong-typed `method` (a number, say) must NOT strand the id: the "always one response"
        // invariant covers this dispatch/validation path too, so answer -32600 with the original id
        // rather than letting GetString() throw into DispatchLineAsync's log-and-skip catch.
        if (methodElement.ValueKind != JsonValueKind.String) {
            _logger.LogDebug("app-server: inbound server request with non-string method (kind={Kind}); responding -32600",
                methodElement.ValueKind);
            error = new AcpError(-32600, "Invalid request: method must be a string", null);
        } else {
            var method        = methodElement.GetString() ?? "";
            var paramsElement = root.TryGetProperty("params", out var p) ? p.Clone() : (JsonElement?) null;
            error = null;

            var handler = OnServerRequest;
            if (handler is null) {
                error  = new AcpError(-32601, $"Method not found: {method}", null);
            } else {
                // The handler contract only needs Method/Params — it never reads Id back — so a
                // placeholder 0 is safe. The response written back uses `idClone`, the ORIGINAL raw
                // JsonElement, never this placeholder.
                var request = new AcpRequest(0, method, paramsElement);

                try {
                    result = await handler(request, ct).ConfigureAwait(false);
                } catch (Exception ex) {
                    _logger.LogDebug(ex, "app-server: OnServerRequest handler threw for method={Method}", method);
                    error  = new AcpError(-32603, "Internal error", null);
                    result = null;
                }

                // A handler that ran without throwing but returned null means "I don't handle this
                // method" — treat it exactly like the no-handler branch, never a null-result success.
                if (result is null && error is null) {
                    _logger.LogDebug("app-server: OnServerRequest handler declined method={Method}; responding -32601 Method not found", method);
                    error = new AcpError(-32601, $"Method not found: {method}", null);
                }
            }
        }

        try {
            // The final response write deliberately uses CancellationToken.None: this method
            // guarantees exactly one response frame "no matter what happens", and `ct` is the same
            // token DisposeAsync cancels — gating the write on it would make the write-gate wait
            // throw before a byte goes out, silently violating that invariant.
            await WriteServerResponseAsync(idClone, result, error, CancellationToken.None).ConfigureAwait(false);
        } catch (Exception ex) {
            _logger.LogDebug(ex, "app-server: failed to write server-request response (method.kind={Kind}); sending internal-error fallback", methodElement.ValueKind);

            try {
                await WriteServerResponseAsync(idClone, null, new AcpError(-32603, "Internal error", null), CancellationToken.None).ConfigureAwait(false);
            } catch (Exception fallbackEx) {
                _logger.LogDebug(fallbackEx, "app-server: internal-error fallback response also failed to write (method.kind={Kind})", methodElement.ValueKind);
            }
        }
    }

    void HandleNotification(JsonElement root, JsonElement methodElement) {
        var method        = methodElement.GetString() ?? "";
        var paramsElement = root.TryGetProperty("params", out var p) ? p.Clone() : (JsonElement?) null;

        OnNotification?.Invoke(new AcpNotification(method, paramsElement));
    }

    async Task WriteServerResponseAsync(JsonElement idElement, JsonElement? result, AcpError? error, CancellationToken ct) {
        var json = SerializeRawIdResponse(idElement, result, error);
        await WriteLineAsync(json, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Builds a response frame manually so an inbound server-request id of any JSON shape (string,
    /// number, etc.) round-trips byte-for-byte instead of being forced through a
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
            await _writeStream.WriteAsync(bytes, ct).ConfigureAwait(false);
            await _writeStream.FlushAsync(ct).ConfigureAwait(false);

            // Log the outbound frame only after it is actually on the wire — and still under the
            // gate, so the debug log order matches wire order.
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

    /// <summary>Signal a clean shutdown to the peer by closing the WRITE side (its stdin) —
    /// <c>codex app-server</c> exits 0 on stdin EOF. Serializes with an in-flight write via the write
    /// gate (bounded, so a stuck write never delays the EOF that shuts the peer down), then disposes
    /// ONLY the write stream — the read loop still observes the peer's own exit via stdout EOF. Idempotent
    /// and best-effort: a prior/concurrent <see cref="DisposeAsync"/> or an already-broken stream is
    /// swallowed, so the caller can fall through to a hard terminate. Does NOT dispose the connection.</summary>
    public async Task CloseInputAsync() {
        if (Volatile.Read(ref _disposed) != 0) return;

        bool gated;
        try { gated = await _writeGate.WaitAsync(TimeSpan.FromSeconds(1)).ConfigureAwait(false); }
        catch (ObjectDisposedException) { return; } // DisposeAsync won the race — stdin is already closed.

        try {
            await _writeStream.DisposeAsync().ConfigureAwait(false);
        } finally {
            if (gated) { try { _writeGate.Release(); } catch (ObjectDisposedException) { /* raced DisposeAsync */ } }
        }
    }

    public async ValueTask DisposeAsync() {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return;

        FaultAllPending(new ObjectDisposedException(nameof(CodexAppServerConnection)));

        _writeGate.Dispose();

        await _writeStream.DisposeAsync().ConfigureAwait(false);
        await _readStream.DisposeAsync().ConfigureAwait(false);
    }

    [LoggerMessage(Level = LogLevel.Debug, Message = "app-server <<< {Frame}")]
    partial void LogInboundFrame(string frame);

    [LoggerMessage(Level = LogLevel.Debug, Message = "app-server >>> {Frame}")]
    partial void LogOutboundFrame(string frame);
}
