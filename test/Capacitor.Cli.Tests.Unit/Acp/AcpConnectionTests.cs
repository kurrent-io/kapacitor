// test/Capacitor.Cli.Tests.Unit/Acp/AcpConnectionTests.cs
using System.IO.Pipelines;
using System.Text;
using System.Text.Json;
using Capacitor.Cli.Core;
using Capacitor.Cli.Core.Acp;
using Capacitor.Cli.Daemon.Acp;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Capacitor.Cli.Tests.Unit.Acp;

/// <summary>
/// Exercises <see cref="AcpConnection"/> over an in-memory duplex "wire" so no real process is
/// spawned. Two independent <see cref="Pipe"/>s stand in for the agent's stdin/stdout:
/// <c>toAgent</c> is written by the connection (via its <c>writeStream</c>) and read by the test's
/// "agent side" harness; <c>toClient</c> is written by the test's "agent side" harness and read by
/// the connection (via its <c>readStream</c>). <see cref="Pipe"/> gives us a real blocking/async
/// reader — unlike <see cref="MemoryStream"/>, a read against an empty <see cref="Pipe"/> awaits
/// until a writer produces more bytes or completes the writer side, which is what a real pipe to a
/// child process does. Task 8 formalizes a richer fake agent on top of this same primitive; this
/// harness stays intentionally minimal and local to this test file.
/// </summary>
public class AcpConnectionTests {
    static readonly TimeSpan HangGuard = TimeSpan.FromSeconds(5);

    sealed class Harness : IAsyncDisposable {
        readonly Pipe   _toAgent  = new();
        readonly Pipe   _toClient = new();
        readonly Stream _agentReadsFromClient;
        readonly Stream _agentWritesToClient;

        public AcpConnection Connection { get; }

        public Harness(ILogger? logger = null, bool debugFrames = false) {
            // Connection writes requests/notifications/responses into _toAgent; the "agent side"
            // (this harness) reads them from the same pipe.
            _agentReadsFromClient = _toAgent.Reader.AsStream();

            // The "agent side" writes frames into _toClient; the connection reads them as its
            // readStream.
            _agentWritesToClient = _toClient.Writer.AsStream();

            Connection = new AcpConnection(
                writeStream: _toAgent.Writer.AsStream(),
                readStream: _toClient.Reader.AsStream(),
                logger: logger ?? NullLogger<AcpConnection>.Instance,
                debugFrames: debugFrames
            );
        }

        /// <summary>Reads one newline-delimited frame the connection wrote, as raw text.</summary>
        public async Task<string> ReadFrameFromConnectionAsync() {
            var line = await ReadLineAsync(_agentReadsFromClient).WaitAsync(HangGuard);
            return line ?? throw new InvalidOperationException("stream completed before a frame arrived");
        }

        /// <summary>Writes one newline-delimited frame as if the agent process emitted it.</summary>
        public async Task WriteFrameToConnectionAsync(string json) {
            var bytes = Encoding.UTF8.GetBytes(json + "\n");
            await _agentWritesToClient.WriteAsync(bytes).AsTask().WaitAsync(HangGuard);
            await _agentWritesToClient.FlushAsync().WaitAsync(HangGuard);
        }

        static async Task<string?> ReadLineAsync(Stream stream) {
            var buffer = new List<byte>();
            var one    = new byte[1];

            while (true) {
                var n = await stream.ReadAsync(one);
                if (n == 0)
                    return buffer.Count == 0 ? null : Encoding.UTF8.GetString(buffer.ToArray());

                if (one[0] == (byte) '\n')
                    return Encoding.UTF8.GetString(buffer.ToArray());

                buffer.Add(one[0]);
            }
        }

        public ValueTask DisposeAsync() => Connection.DisposeAsync();
    }

    [Test]
    public async Task RequestAsync_resolves_with_result_on_matching_response() {
        await using var harness = new Harness();
        using var       cts     = new CancellationTokenSource();
        var              runTask = harness.Connection.RunAsync(cts.Token);

        var requestTask = harness.Connection.RequestAsync("initialize", null, CancellationToken.None);

        var frame = await harness.ReadFrameFromConnectionAsync();
        using var doc = JsonDocument.Parse(frame);
        var       id  = doc.RootElement.GetProperty("id").GetInt64();
        await Assert.That(doc.RootElement.GetProperty("method").GetString()).IsEqualTo("initialize");

        await harness.WriteFrameToConnectionAsync(
            $$$"""{"jsonrpc":"2.0","id":{{{id}}},"result":{"stopReason":"end_turn"}}"""
        );

        var result = await requestTask.WaitAsync(HangGuard);
        await Assert.That(result.GetProperty("stopReason").GetString()).IsEqualTo("end_turn");

        cts.Cancel();
        await SwallowCancellation(runTask);
    }

    [Test]
    public async Task Concurrent_requests_correlate_to_their_own_responses_when_interleaved() {
        await using var harness = new Harness();
        using var       cts     = new CancellationTokenSource();
        var              runTask = harness.Connection.RunAsync(cts.Token);

        var requestA = harness.Connection.RequestAsync("session/new", null, CancellationToken.None);
        var frameA   = await harness.ReadFrameFromConnectionAsync();
        var idA      = JsonDocument.Parse(frameA).RootElement.GetProperty("id").GetInt64();

        var requestB = harness.Connection.RequestAsync("session/prompt", null, CancellationToken.None);
        var frameB   = await harness.ReadFrameFromConnectionAsync();
        var idB      = JsonDocument.Parse(frameB).RootElement.GetProperty("id").GetInt64();

        await Assert.That(idA).IsNotEqualTo(idB);

        // Respond out of order: B's response arrives before A's.
        await harness.WriteFrameToConnectionAsync(
            $$$"""{"jsonrpc":"2.0","id":{{{idB}}},"result":{"marker":"B"}}"""
        );
        await harness.WriteFrameToConnectionAsync(
            $$$"""{"jsonrpc":"2.0","id":{{{idA}}},"result":{"marker":"A"}}"""
        );

        var resultA = await requestA.WaitAsync(HangGuard);
        var resultB = await requestB.WaitAsync(HangGuard);

        await Assert.That(resultA.GetProperty("marker").GetString()).IsEqualTo("A");
        await Assert.That(resultB.GetProperty("marker").GetString()).IsEqualTo("B");

        cts.Cancel();
        await SwallowCancellation(runTask);
    }

    [Test]
    public async Task Error_response_throws_AcpRpcException_with_code_and_message() {
        await using var harness = new Harness();
        using var       cts     = new CancellationTokenSource();
        var              runTask = harness.Connection.RunAsync(cts.Token);

        var requestTask = harness.Connection.RequestAsync("session/set_config_option", null, CancellationToken.None);
        var frame       = await harness.ReadFrameFromConnectionAsync();
        var id          = JsonDocument.Parse(frame).RootElement.GetProperty("id").GetInt64();

        await harness.WriteFrameToConnectionAsync(
            $$$"""{"jsonrpc":"2.0","id":{{{id}}},"error":{"code":-32603,"message":"Internal error"}}"""
        );

        var ex = await Assert.ThrowsAsync<AcpRpcException>(() => requestTask.WaitAsync(HangGuard));
        await Assert.That(ex!.Code).IsEqualTo(-32603);
        await Assert.That(ex.Message).IsEqualTo("Internal error");

        cts.Cancel();
        await SwallowCancellation(runTask);
    }

    [Test]
    public async Task Inbound_notification_raises_OnNotification_with_method_and_params() {
        await using var harness = new Harness();
        using var       cts     = new CancellationTokenSource();
        var              runTask = harness.Connection.RunAsync(cts.Token);

        var tcs = new TaskCompletionSource<AcpNotification>(TaskCreationOptions.RunContinuationsAsynchronously);
        harness.Connection.OnNotification += n => tcs.TrySetResult(n);

        await harness.WriteFrameToConnectionAsync(
            """{"jsonrpc":"2.0","method":"session/update","params":{"sessionId":"abc","update":{"sessionUpdate":"agent_message_chunk"}}}"""
        );

        var notification = await tcs.Task.WaitAsync(HangGuard);
        await Assert.That(notification.Method).IsEqualTo("session/update");
        await Assert.That(notification.Params).IsNotNull();
        await Assert.That(notification.Params!.Value.GetProperty("sessionId").GetString()).IsEqualTo("abc");

        cts.Cancel();
        await SwallowCancellation(runTask);
    }

    [Test]
    public async Task Inbound_server_request_with_handler_set_invokes_handler_and_echoes_id() {
        await using var harness = new Harness();
        using var       cts     = new CancellationTokenSource();
        var              runTask = harness.Connection.RunAsync(cts.Token);

        harness.Connection.OnServerRequest = (request, _) => {
            var result = JsonSerializer.SerializeToElement(new { content = "file contents" });
            return Task.FromResult<JsonElement?>(result);
        };

        await harness.WriteFrameToConnectionAsync(
            """{"jsonrpc":"2.0","id":99,"method":"fs/read_text_file","params":{"path":"/tmp/x"}}"""
        );

        var frame = await harness.ReadFrameFromConnectionAsync();
        using var doc = JsonDocument.Parse(frame);
        await Assert.That(doc.RootElement.GetProperty("id").GetInt64()).IsEqualTo(99L);
        await Assert.That(doc.RootElement.TryGetProperty("error", out _)).IsFalse();
        await Assert.That(doc.RootElement.GetProperty("result").GetProperty("content").GetString())
            .IsEqualTo("file contents");

        cts.Cancel();
        await SwallowCancellation(runTask);
    }

    // PR #244 review (Fix #3): OnServerRequest's contract is now Task<JsonElement?> — the handler
    // can no longer return an un-serializable CLR object that WriteServerResponseAsync could only
    // reject with a throw OUTSIDE any try/catch, silently orphaning the agent's request (no
    // response ever written, wedging its wait on this id forever). These tests assert the
    // "always answered" guarantee holds across the three shapes a handler can produce: a value
    // becomes the JSON-RPC `result`, a thrown exception becomes a `-32603 Internal error`, and a
    // null return (the method ran but declined to handle it) becomes a `-32601 Method not found` —
    // the same shape a fully unset handler produces, never a null-result success.
    [Test]
    public async Task Inbound_server_request_handler_throwing_still_writes_an_internal_error_response() {
        await using var harness = new Harness();
        using var       cts     = new CancellationTokenSource();
        var              runTask = harness.Connection.RunAsync(cts.Token);

        harness.Connection.OnServerRequest = (_, _) => throw new InvalidOperationException("boom");

        await harness.WriteFrameToConnectionAsync(
            """{"jsonrpc":"2.0","id":7,"method":"fs/read_text_file","params":{"path":"/tmp/x"}}"""
        );

        var frame = await harness.ReadFrameFromConnectionAsync();
        using var doc = JsonDocument.Parse(frame);
        await Assert.That(doc.RootElement.GetProperty("id").GetInt64()).IsEqualTo(7L);
        await Assert.That(doc.RootElement.TryGetProperty("result", out _)).IsFalse();
        var error = doc.RootElement.GetProperty("error");
        await Assert.That(error.GetProperty("code").GetInt32()).IsEqualTo(-32603);

        // Loop must still be alive afterward — a wedge would silently swallow this too.
        var tcs = new TaskCompletionSource<AcpNotification>(TaskCreationOptions.RunContinuationsAsynchronously);
        harness.Connection.OnNotification += n => tcs.TrySetResult(n);
        await harness.WriteFrameToConnectionAsync(
            """{"jsonrpc":"2.0","method":"session/update","params":{"sessionId":"still-alive"}}"""
        );
        var notification = await tcs.Task.WaitAsync(HangGuard);
        await Assert.That(notification.Params!.Value.GetProperty("sessionId").GetString()).IsEqualTo("still-alive");

        cts.Cancel();
        await SwallowCancellation(runTask);
    }

    [Test]
    public async Task Inbound_server_request_handler_returning_null_writes_method_not_found_error() {
        await using var harness = new Harness();
        using var       cts     = new CancellationTokenSource();
        var              runTask = harness.Connection.RunAsync(cts.Token);

        harness.Connection.OnServerRequest = (_, _) => Task.FromResult<JsonElement?>(null);

        // fs/read_text_file is unadvertised (the daemon sends fs.readTextFile=false in its
        // initialize params) — a wired handler declining it must never look like a successful
        // no-op file read.
        await harness.WriteFrameToConnectionAsync(
            """{"jsonrpc":"2.0","id":8,"method":"fs/read_text_file","params":{"path":"/tmp/x"}}"""
        );

        var frame = await harness.ReadFrameFromConnectionAsync();
        using var doc = JsonDocument.Parse(frame);
        await Assert.That(doc.RootElement.GetProperty("id").GetInt64()).IsEqualTo(8L);
        await Assert.That(doc.RootElement.TryGetProperty("result", out _)).IsFalse();
        var error = doc.RootElement.GetProperty("error");
        await Assert.That(error.GetProperty("code").GetInt32()).IsEqualTo(-32601);

        // Loop must still be alive afterward — mirrors the throw-test's liveness check, re-locking
        // the "always answered" guarantee at this new response shape.
        var tcs = new TaskCompletionSource<AcpNotification>(TaskCreationOptions.RunContinuationsAsynchronously);
        harness.Connection.OnNotification += n => tcs.TrySetResult(n);
        await harness.WriteFrameToConnectionAsync(
            """{"jsonrpc":"2.0","method":"session/update","params":{"sessionId":"still-alive"}}"""
        );
        var notification = await tcs.Task.WaitAsync(HangGuard);
        await Assert.That(notification.Params!.Value.GetProperty("sessionId").GetString()).IsEqualTo("still-alive");

        cts.Cancel();
        await SwallowCancellation(runTask);
    }

    // Proves the PRODUCTION wiring (a real AcpInteractionBridge, not a fake null-returning
    // delegate) declines an unadvertised method cleanly. The bridge's `requestInteraction` delegate
    // is never invoked here — terminal/create doesn't match either method the bridge recognizes, so
    // it never reaches that delegate; asserting `called` stays false pins that.
    [Test]
    public async Task Inbound_unadvertised_terminal_request_through_real_bridge_writes_method_not_found_error() {
        await using var harness = new Harness();
        using var       cts     = new CancellationTokenSource();
        var              runTask = harness.Connection.RunAsync(cts.Token);

        var called = false;
        var bridge = new AcpInteractionBridge(
            requestInteraction: (_, _) => {
                called = true;
                return Task.FromResult(new AcpInteractionDecision("allow", null, null, null, null, null));
            },
            agentId: "agent-1",
            logger: NullLogger.Instance);

        harness.Connection.OnServerRequest = bridge.HandleAsync;

        await harness.WriteFrameToConnectionAsync(
            """{"jsonrpc":"2.0","id":42,"method":"terminal/create","params":{"command":"ls"}}"""
        );

        var frame = await harness.ReadFrameFromConnectionAsync();
        using var doc = JsonDocument.Parse(frame);
        await Assert.That(doc.RootElement.GetProperty("id").GetInt64()).IsEqualTo(42L);
        await Assert.That(doc.RootElement.TryGetProperty("result", out _)).IsFalse();
        var error = doc.RootElement.GetProperty("error");
        await Assert.That(error.GetProperty("code").GetInt32()).IsEqualTo(-32601);
        await Assert.That(called).IsFalse();

        cts.Cancel();
        await SwallowCancellation(runTask);
    }

    [Test]
    public async Task Inbound_server_request_with_no_handler_writes_method_not_found_error() {
        await using var harness = new Harness();
        using var       cts     = new CancellationTokenSource();
        var              runTask = harness.Connection.RunAsync(cts.Token);

        // OnServerRequest intentionally left unset (default-decline posture).
        await harness.WriteFrameToConnectionAsync(
            """{"jsonrpc":"2.0","id":99,"method":"session/request_permission","params":{}}"""
        );

        var frame = await harness.ReadFrameFromConnectionAsync();
        using var doc = JsonDocument.Parse(frame);
        await Assert.That(doc.RootElement.GetProperty("id").GetInt64()).IsEqualTo(99L);
        await Assert.That(doc.RootElement.TryGetProperty("result", out _)).IsFalse();
        var error = doc.RootElement.GetProperty("error");
        await Assert.That(error.GetProperty("code").GetInt32()).IsEqualTo(-32601);

        cts.Cancel();
        await SwallowCancellation(runTask);
    }

    [Test]
    public async Task Cancelling_token_abandons_pending_request_without_hanging() {
        await using var harness = new Harness();
        using var       cts     = new CancellationTokenSource();
        var              runTask = harness.Connection.RunAsync(cts.Token);

        using var requestCts = new CancellationTokenSource();
        var       requestTask = harness.Connection.RequestAsync("session/prompt", null, requestCts.Token);

        // Make sure the request was actually sent before cancelling, so we're testing abandonment
        // of an in-flight call, not a call that never started.
        await harness.ReadFrameFromConnectionAsync();

        requestCts.Cancel();

        await Assert.ThrowsAsync<OperationCanceledException>(() => requestTask.WaitAsync(HangGuard));

        cts.Cancel();
        await SwallowCancellation(runTask);
    }

    [Test]
    public async Task Malformed_line_is_skipped_and_loop_still_delivers_next_valid_frame() {
        await using var harness = new Harness();
        using var       cts     = new CancellationTokenSource();
        var              runTask = harness.Connection.RunAsync(cts.Token);

        var tcs = new TaskCompletionSource<AcpNotification>(TaskCreationOptions.RunContinuationsAsynchronously);
        harness.Connection.OnNotification += n => tcs.TrySetResult(n);

        await harness.WriteFrameToConnectionAsync("{not valid json at all");
        await harness.WriteFrameToConnectionAsync(
            """{"jsonrpc":"2.0","method":"session/update","params":{"sessionId":"still-alive"}}"""
        );

        var notification = await tcs.Task.WaitAsync(HangGuard);
        await Assert.That(notification.Params!.Value.GetProperty("sessionId").GetString()).IsEqualTo("still-alive");

        cts.Cancel();
        await SwallowCancellation(runTask);
    }

    [Test]
    public async Task Wrong_typed_field_in_well_formed_JSON_is_skipped_and_loop_still_delivers_next_valid_frame() {
        await using var harness = new Harness();
        using var       cts     = new CancellationTokenSource();
        var              runTask = harness.Connection.RunAsync(cts.Token);

        var tcs = new TaskCompletionSource<AcpNotification>(TaskCreationOptions.RunContinuationsAsynchronously);
        harness.Connection.OnNotification += n => tcs.TrySetResult(n);

        // Well-formed JSON, but `method` is a number instead of a string — parses fine via
        // JsonDocument.Parse, then throws InvalidOperationException out of GetString() during
        // shape-dispatch. Also probe an error frame with a non-integer `code` for the same class
        // of bug (FormatException out of GetInt32()).
        await harness.WriteFrameToConnectionAsync("""{"jsonrpc":"2.0","id":1,"method":123}""");
        await harness.WriteFrameToConnectionAsync(
            """{"jsonrpc":"2.0","id":2,"error":{"code":"oops","message":"bad"}}"""
        );
        await harness.WriteFrameToConnectionAsync(
            """{"jsonrpc":"2.0","method":"session/update","params":{"sessionId":"still-alive"}}"""
        );

        var notification = await tcs.Task.WaitAsync(HangGuard);
        await Assert.That(notification.Params!.Value.GetProperty("sessionId").GetString()).IsEqualTo("still-alive");

        cts.Cancel();
        await SwallowCancellation(runTask);
    }

    // PR #244 review (Fix D, MAJOR): HandleResponse used to TryRemove the pending TCS FIRST, then
    // parse error.code/error.message with the throwing GetInt32()/GetString() accessors. A
    // well-formed-JSON-but-wrong-typed error payload (e.g. "code":"oops") made that parse throw,
    // DispatchLineAsync's broad catch logged+skipped the frame, and the ALREADY-REMOVED TCS was
    // never completed — the caller's RequestAsync hung until the connection was disposed. This
    // test has a REAL pending RequestAsync awaiting id=1 (unlike the malformed-frame test above,
    // whose id=2 has no pending caller and so never exercised the orphan path) and asserts the
    // caller faults with AcpRpcException instead of hanging.
    [Test]
    public async Task Wrong_typed_error_code_on_a_pending_request_faults_the_caller_instead_of_hanging() {
        await using var harness = new Harness();
        using var       cts     = new CancellationTokenSource();
        var              runTask = harness.Connection.RunAsync(cts.Token);

        var requestTask = harness.Connection.RequestAsync("session/prompt", null, CancellationToken.None);
        var frame       = await harness.ReadFrameFromConnectionAsync();
        var id          = JsonDocument.Parse(frame).RootElement.GetProperty("id").GetInt64();

        await harness.WriteFrameToConnectionAsync(
            $$$"""{"jsonrpc":"2.0","id":{{{id}}},"error":{"code":"oops","message":"x"}}"""
        );

        // Bounded .WaitAsync: before the fix this would hang for the whole HangGuard and then
        // throw TimeoutException (masking the real bug) rather than surfacing the RPC error.
        var ex = await Assert.ThrowsAsync<AcpRpcException>(() => requestTask.WaitAsync(HangGuard));
        await Assert.That(ex).IsNotNull();

        cts.Cancel();
        await SwallowCancellation(runTask);
    }

    // Same class of bug, but the `error` value itself is non-object (a JSON string) rather than
    // having a wrong-typed field inside it — HandleResponse's `errorElement is { } error` pattern
    // matches ANY non-null JsonElement (including a string), so `error.TryGetProperty(...)` on a
    // string value must not throw either.
    [Test]
    public async Task Non_object_error_payload_on_a_pending_request_faults_the_caller_instead_of_hanging() {
        await using var harness = new Harness();
        using var       cts     = new CancellationTokenSource();
        var              runTask = harness.Connection.RunAsync(cts.Token);

        var requestTask = harness.Connection.RequestAsync("session/prompt", null, CancellationToken.None);
        var frame       = await harness.ReadFrameFromConnectionAsync();
        var id          = JsonDocument.Parse(frame).RootElement.GetProperty("id").GetInt64();

        await harness.WriteFrameToConnectionAsync(
            $$$"""{"jsonrpc":"2.0","id":{{{id}}},"error":"totally-not-an-object"}"""
        );

        var ex = await Assert.ThrowsAsync<AcpRpcException>(() => requestTask.WaitAsync(HangGuard));
        await Assert.That(ex).IsNotNull();

        cts.Cancel();
        await SwallowCancellation(runTask);
    }

    [Test]
    public async Task Server_request_with_string_id_echoes_the_same_string_id_verbatim() {
        await using var harness = new Harness();
        using var       cts     = new CancellationTokenSource();
        var              runTask = harness.Connection.RunAsync(cts.Token);

        harness.Connection.OnServerRequest = (request, _) => {
            var result = JsonSerializer.SerializeToElement(new { content = "file contents" });
            return Task.FromResult<JsonElement?>(result);
        };

        await harness.WriteFrameToConnectionAsync(
            """{"jsonrpc":"2.0","id":"agent-generated-string-id","method":"fs/read_text_file","params":{}}"""
        );

        var frame = await harness.ReadFrameFromConnectionAsync();
        using var doc = JsonDocument.Parse(frame);
        var idElement = doc.RootElement.GetProperty("id");
        await Assert.That(idElement.ValueKind).IsEqualTo(JsonValueKind.String);
        await Assert.That(idElement.GetString()).IsEqualTo("agent-generated-string-id");
        await Assert.That(doc.RootElement.GetProperty("result").GetProperty("content").GetString())
            .IsEqualTo("file contents");

        // Confirm the loop is still alive after handling the string-id request: a subsequent valid
        // frame must still be delivered (guards against a naive long-forcing implementation that
        // would throw on the string id and silently wedge the read loop).
        var tcs = new TaskCompletionSource<AcpNotification>(TaskCreationOptions.RunContinuationsAsynchronously);
        harness.Connection.OnNotification += n => tcs.TrySetResult(n);
        await harness.WriteFrameToConnectionAsync(
            """{"jsonrpc":"2.0","method":"session/update","params":{"sessionId":"still-alive"}}"""
        );
        var notification = await tcs.Task.WaitAsync(HangGuard);
        await Assert.That(notification.Params!.Value.GetProperty("sessionId").GetString()).IsEqualTo("still-alive");

        cts.Cancel();
        await SwallowCancellation(runTask);
    }

    [Test]
    public async Task NotifyAsync_writes_notification_frame_without_id() {
        await using var harness = new Harness();
        using var       cts     = new CancellationTokenSource();
        var              runTask = harness.Connection.RunAsync(cts.Token);

        await harness.Connection.NotifyAsync("session/cancel", null).WaitAsync(HangGuard);

        var frame = await harness.ReadFrameFromConnectionAsync();
        using var doc = JsonDocument.Parse(frame);
        await Assert.That(doc.RootElement.GetProperty("method").GetString()).IsEqualTo("session/cancel");
        await Assert.That(doc.RootElement.TryGetProperty("id", out _)).IsFalse();

        cts.Cancel();
        await SwallowCancellation(runTask);
    }

    static async Task SwallowCancellation(Task task) {
        try {
            await task.WaitAsync(HangGuard);
        } catch (OperationCanceledException) {
            // expected shutdown path for this test's owned CTS
        }
    }

    // ── KCAP_ACP_DEBUG_FRAMES gate: full inbound/outbound frame logging ────────────────────────

    /// <summary>Records every log call — mirrors <c>AcpTranscriptAggregationTests.CaptureLogger</c>'s
    /// established pattern.</summary>
    sealed class CaptureLogger : ILogger {
        public readonly List<(LogLevel Level, string Message)> Entries = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool         IsEnabled(LogLevel logLevel)                            => true;

        public void Log<TState>(LogLevel level, EventId id, TState state, Exception? ex, Func<TState, Exception?, string> formatter)
            => Entries.Add((level, formatter(state, ex)));
    }

    [Test]
    public async Task DebugFrames_off_by_default_never_logs_the_full_outbound_or_inbound_frame() {
        var logger = new CaptureLogger();
        // debugFrames omitted — proves the default is Off, matching every pre-existing call site.
        await using var harness = new Harness(logger);
        using var        cts     = new CancellationTokenSource();
        var               runTask = harness.Connection.RunAsync(cts.Token);

        var requestTask = harness.Connection.RequestAsync("session/prompt", null, CancellationToken.None);
        var frame        = await harness.ReadFrameFromConnectionAsync();
        var id           = JsonDocument.Parse(frame).RootElement.GetProperty("id").GetInt64();

        await harness.WriteFrameToConnectionAsync(
            $$$"""{"jsonrpc":"2.0","id":{{{id}}},"result":{"stopReason":"end_turn"}}"""
        );
        await requestTask.WaitAsync(HangGuard);

        await Assert.That(logger.Entries).DoesNotContain(e => e.Message.Contains("ACP >>>"));
        await Assert.That(logger.Entries).DoesNotContain(e => e.Message.Contains("ACP <<<"));
        await Assert.That(logger.Entries).DoesNotContain(e => e.Message.Contains("session/prompt"));
        await Assert.That(logger.Entries).DoesNotContain(e => e.Message.Contains("stopReason"));

        cts.Cancel();
        await SwallowCancellation(runTask);
    }

    [Test]
    public async Task DebugFrames_on_logs_the_full_outbound_request_frame() {
        var logger = new CaptureLogger();
        await using var harness = new Harness(logger, debugFrames: true);
        using var        cts     = new CancellationTokenSource();
        var               runTask = harness.Connection.RunAsync(cts.Token);

        _ = harness.Connection.RequestAsync("session/prompt", null, CancellationToken.None);
        var frame = await harness.ReadFrameFromConnectionAsync();
        var id    = JsonDocument.Parse(frame).RootElement.GetProperty("id").GetInt64();

        await Assert.That(logger.Entries).Contains(e =>
            e.Level == LogLevel.Debug && e.Message.Contains("ACP >>>") && e.Message.Contains("session/prompt"));

        // Clean shutdown: still owe the pending request a response before disposing.
        await harness.WriteFrameToConnectionAsync($$$"""{"jsonrpc":"2.0","id":{{{id}}},"result":{}}""");

        cts.Cancel();
        await SwallowCancellation(runTask);
    }

    [Test]
    public async Task DebugFrames_on_logs_the_full_inbound_notification_frame() {
        var logger = new CaptureLogger();
        await using var harness = new Harness(logger, debugFrames: true);
        using var        cts     = new CancellationTokenSource();
        var               runTask = harness.Connection.RunAsync(cts.Token);

        var tcs = new TaskCompletionSource<AcpNotification>(TaskCreationOptions.RunContinuationsAsynchronously);
        harness.Connection.OnNotification += n => tcs.TrySetResult(n);

        await harness.WriteFrameToConnectionAsync(
            """{"jsonrpc":"2.0","method":"session/update","params":{"sessionId":"abc-secret-marker"}}"""
        );
        await tcs.Task.WaitAsync(HangGuard);

        await Assert.That(logger.Entries).Contains(e =>
            e.Level == LogLevel.Debug && e.Message.Contains("ACP <<<") && e.Message.Contains("abc-secret-marker"));

        cts.Cancel();
        await SwallowCancellation(runTask);
    }

    [Test]
    public async Task DebugFrames_on_does_not_regress_the_existing_shape_only_malformed_line_logging() {
        // AcpConnection already logs frame SHAPE only for malformed/unrecognized lines — that must
        // not regress when the opt-in full-frame logging is added. This proves the pre-existing
        // malformed-line handling is unchanged with the flag on: still skipped, still keeps the loop
        // alive, no throw.
        var logger = new CaptureLogger();
        await using var harness = new Harness(logger, debugFrames: true);
        using var        cts     = new CancellationTokenSource();
        var               runTask = harness.Connection.RunAsync(cts.Token);

        var tcs = new TaskCompletionSource<AcpNotification>(TaskCreationOptions.RunContinuationsAsynchronously);
        harness.Connection.OnNotification += n => tcs.TrySetResult(n);

        await harness.WriteFrameToConnectionAsync("{not valid json at all");
        await harness.WriteFrameToConnectionAsync(
            """{"jsonrpc":"2.0","method":"session/update","params":{"sessionId":"still-alive"}}"""
        );

        var notification = await tcs.Task.WaitAsync(HangGuard);
        await Assert.That(notification.Params!.Value.GetProperty("sessionId").GetString()).IsEqualTo("still-alive");

        cts.Cancel();
        await SwallowCancellation(runTask);
    }

    /// <summary>A stream whose writes always fail — models a broken pipe (child dead) so the
    /// write-side reconnect trigger is directly assertable.</summary>
    sealed class ThrowingWriteStream : Stream {
        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => 0;
        public override long Position { get => 0; set { } }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) => 0;
        public override long Seek(long offset, SeekOrigin origin) => 0;
        public override void SetLength(long value) { }
        public override void Write(byte[] buffer, int offset, int count) => throw new IOException("broken pipe");
        public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken ct) => throw new IOException("broken pipe");
        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken ct = default) => throw new IOException("broken pipe");
    }

    [Test]
    public async Task Stream_write_failure_sets_transport_ended_latch_and_fires_pre_fault_hook_before_throwing() {
        var hookFired = false;
        var latchAtHookTime = false;

        await using var connection = new AcpConnection(
            new ThrowingWriteStream(), new MemoryStream(), NullLogger.Instance);

        connection.BeforeFaultingPending = () => {
            hookFired       = true;
            latchAtHookTime = connection.TransportEnded;
        };

        await Assert.That(async () => await connection.NotifyAsync("session/cancel", null))
            .Throws<IOException>();

        await Assert.That(hookFired).IsTrue();
        // The latch is set strictly BEFORE the hook is invoked (reconnect spec §5.2) — the commit
        // liveness check depends on that ordering.
        await Assert.That(latchAtHookTime).IsTrue();
        await Assert.That(connection.TransportEnded).IsTrue();
    }
}
