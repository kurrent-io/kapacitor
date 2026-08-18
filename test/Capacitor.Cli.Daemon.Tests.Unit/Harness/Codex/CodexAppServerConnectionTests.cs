using System.IO.Pipelines;
using System.Text;
using System.Text.Json;
using Capacitor.Cli.Core.Acp;
using Capacitor.Cli.Daemon.Harness.Codex;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Capacitor.Cli.Daemon.Tests.Unit.Harness.Codex;

/// <summary>
/// Exercises <see cref="CodexAppServerConnection"/> over an in-memory duplex "wire" so no real
/// process is spawned — the same harness shape as <c>AcpConnectionTests</c> (two <see cref="Pipe"/>s
/// standing in for the app-server's stdin/stdout). The app-server speaks the same JSON-RPC 2.0
/// envelope as ACP, so these tests pin the transport contract the runtime depends on: id
/// correlation, out-of-order responses, error faulting, notification fan-out, the always-answered
/// server-request guarantee, and read-loop resilience to malformed frames.
/// </summary>
public class CodexAppServerConnectionTests {
    static readonly TimeSpan HangGuard = TimeSpan.FromSeconds(5);

    sealed class Harness : IAsyncDisposable {
        readonly Pipe   _toAgent  = new();
        readonly Pipe   _toClient = new();
        readonly Stream _agentReadsFromClient;
        readonly Stream _agentWritesToClient;

        public CodexAppServerConnection Connection { get; }

        public Harness(ILogger? logger = null, bool debugFrames = false) {
            _agentReadsFromClient = _toAgent.Reader.AsStream();
            _agentWritesToClient  = _toClient.Writer.AsStream();

            Connection = new CodexAppServerConnection(
                writeStream: _toAgent.Writer.AsStream(),
                readStream: _toClient.Reader.AsStream(),
                logger: logger ?? NullLogger<CodexAppServerConnection>.Instance,
                debugFrames: debugFrames
            );
        }

        public async Task<string> ReadFrameFromConnectionAsync() {
            var line = await ReadLineAsync(_agentReadsFromClient).WaitAsync(HangGuard);
            return line ?? throw new InvalidOperationException("stream completed before a frame arrived");
        }

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
        var             runTask = harness.Connection.RunAsync(cts.Token);

        var requestTask = harness.Connection.RequestAsync("initialize", null, CancellationToken.None);

        var frame = await harness.ReadFrameFromConnectionAsync();
        using var doc = JsonDocument.Parse(frame);
        var       id  = doc.RootElement.GetProperty("id").GetInt64();
        await Assert.That(doc.RootElement.GetProperty("method").GetString()).IsEqualTo("initialize");
        await Assert.That(doc.RootElement.GetProperty("jsonrpc").GetString()).IsEqualTo("2.0");

        await harness.WriteFrameToConnectionAsync(
            $$$"""{"jsonrpc":"2.0","id":{{{id}}},"result":{"userAgent":"codex"}}"""
        );

        var result = await requestTask.WaitAsync(HangGuard);
        await Assert.That(result.GetProperty("userAgent").GetString()).IsEqualTo("codex");

        cts.Cancel();
        await SwallowCancellation(runTask);
    }

    [Test]
    public async Task Concurrent_requests_correlate_to_their_own_responses_when_interleaved() {
        await using var harness = new Harness();
        using var       cts     = new CancellationTokenSource();
        var             runTask = harness.Connection.RunAsync(cts.Token);

        var requestA = harness.Connection.RequestAsync("newThread", null, CancellationToken.None);
        var frameA   = await harness.ReadFrameFromConnectionAsync();
        var idA      = JsonDocument.Parse(frameA).RootElement.GetProperty("id").GetInt64();

        var requestB = harness.Connection.RequestAsync("sendTurn", null, CancellationToken.None);
        var frameB   = await harness.ReadFrameFromConnectionAsync();
        var idB      = JsonDocument.Parse(frameB).RootElement.GetProperty("id").GetInt64();

        await Assert.That(idA).IsNotEqualTo(idB);

        // Respond out of order: B's response arrives before A's.
        await harness.WriteFrameToConnectionAsync($$$"""{"jsonrpc":"2.0","id":{{{idB}}},"result":{"marker":"B"}}""");
        await harness.WriteFrameToConnectionAsync($$$"""{"jsonrpc":"2.0","id":{{{idA}}},"result":{"marker":"A"}}""");

        var resultA = await requestA.WaitAsync(HangGuard);
        var resultB = await requestB.WaitAsync(HangGuard);

        await Assert.That(resultA.GetProperty("marker").GetString()).IsEqualTo("A");
        await Assert.That(resultB.GetProperty("marker").GetString()).IsEqualTo("B");

        cts.Cancel();
        await SwallowCancellation(runTask);
    }

    [Test]
    public async Task Error_response_throws_CodexAppServerRpcException_with_code_and_message() {
        await using var harness = new Harness();
        using var       cts     = new CancellationTokenSource();
        var             runTask = harness.Connection.RunAsync(cts.Token);

        var requestTask = harness.Connection.RequestAsync("sendTurn", null, CancellationToken.None);
        var frame       = await harness.ReadFrameFromConnectionAsync();
        var id          = JsonDocument.Parse(frame).RootElement.GetProperty("id").GetInt64();

        await harness.WriteFrameToConnectionAsync(
            $$$"""{"jsonrpc":"2.0","id":{{{id}}},"error":{"code":-32602,"message":"invalid thread"}}"""
        );

        var ex = await Assert.ThrowsAsync<CodexAppServerRpcException>(() => requestTask.WaitAsync(HangGuard));
        await Assert.That(ex!.Code).IsEqualTo(-32602);
        await Assert.That(ex.Message).IsEqualTo("invalid thread");

        cts.Cancel();
        await SwallowCancellation(runTask);
    }

    [Test]
    public async Task Inbound_notification_raises_OnNotification_with_method_and_params() {
        await using var harness = new Harness();
        using var       cts     = new CancellationTokenSource();
        var             runTask = harness.Connection.RunAsync(cts.Token);

        var tcs = new TaskCompletionSource<AcpNotification>(TaskCreationOptions.RunContinuationsAsynchronously);
        harness.Connection.OnNotification += n => tcs.TrySetResult(n);

        await harness.WriteFrameToConnectionAsync(
            """{"jsonrpc":"2.0","method":"turn.completed","params":{"threadId":"t1","usage":{"inputTokens":10}}}"""
        );

        var notification = await tcs.Task.WaitAsync(HangGuard);
        await Assert.That(notification.Method).IsEqualTo("turn.completed");
        await Assert.That(notification.Params).IsNotNull();
        await Assert.That(notification.Params!.Value.GetProperty("threadId").GetString()).IsEqualTo("t1");

        cts.Cancel();
        await SwallowCancellation(runTask);
    }

    [Test]
    public async Task Inbound_server_request_with_handler_set_invokes_handler_and_echoes_id() {
        await using var harness = new Harness();
        using var       cts     = new CancellationTokenSource();
        var             runTask = harness.Connection.RunAsync(cts.Token);

        harness.Connection.OnServerRequest = (_, _) =>
            Task.FromResult<JsonElement?>(JsonSerializer.SerializeToElement(new { decision = "approved" }));

        await harness.WriteFrameToConnectionAsync(
            """{"jsonrpc":"2.0","id":99,"method":"execCommandApproval","params":{"command":"ls"}}"""
        );

        var frame = await harness.ReadFrameFromConnectionAsync();
        using var doc = JsonDocument.Parse(frame);
        await Assert.That(doc.RootElement.GetProperty("id").GetInt64()).IsEqualTo(99L);
        await Assert.That(doc.RootElement.TryGetProperty("error", out _)).IsFalse();
        await Assert.That(doc.RootElement.GetProperty("result").GetProperty("decision").GetString()).IsEqualTo("approved");

        cts.Cancel();
        await SwallowCancellation(runTask);
    }

    [Test]
    public async Task Inbound_server_request_handler_throwing_still_writes_an_internal_error_response() {
        await using var harness = new Harness();
        using var       cts     = new CancellationTokenSource();
        var             runTask = harness.Connection.RunAsync(cts.Token);

        harness.Connection.OnServerRequest = (_, _) => throw new InvalidOperationException("boom");

        await harness.WriteFrameToConnectionAsync(
            """{"jsonrpc":"2.0","id":7,"method":"execCommandApproval","params":{}}"""
        );

        var frame = await harness.ReadFrameFromConnectionAsync();
        using var doc = JsonDocument.Parse(frame);
        await Assert.That(doc.RootElement.GetProperty("id").GetInt64()).IsEqualTo(7L);
        await Assert.That(doc.RootElement.TryGetProperty("result", out _)).IsFalse();
        await Assert.That(doc.RootElement.GetProperty("error").GetProperty("code").GetInt32()).IsEqualTo(-32603);

        // Loop must still be alive afterward — a wedge would silently swallow this too.
        var tcs = new TaskCompletionSource<AcpNotification>(TaskCreationOptions.RunContinuationsAsynchronously);
        harness.Connection.OnNotification += n => tcs.TrySetResult(n);
        await harness.WriteFrameToConnectionAsync("""{"jsonrpc":"2.0","method":"turn.completed","params":{"threadId":"alive"}}""");
        var notification = await tcs.Task.WaitAsync(HangGuard);
        await Assert.That(notification.Params!.Value.GetProperty("threadId").GetString()).IsEqualTo("alive");

        cts.Cancel();
        await SwallowCancellation(runTask);
    }

    [Test]
    public async Task Inbound_server_request_with_no_handler_writes_method_not_found_error() {
        await using var harness = new Harness();
        using var       cts     = new CancellationTokenSource();
        var             runTask = harness.Connection.RunAsync(cts.Token);

        // OnServerRequest intentionally left unset (default-decline posture) — an unattended reviewer
        // never elevates an approval request into a grant.
        await harness.WriteFrameToConnectionAsync(
            """{"jsonrpc":"2.0","id":99,"method":"applyPatchApproval","params":{}}"""
        );

        var frame = await harness.ReadFrameFromConnectionAsync();
        using var doc = JsonDocument.Parse(frame);
        await Assert.That(doc.RootElement.GetProperty("id").GetInt64()).IsEqualTo(99L);
        await Assert.That(doc.RootElement.TryGetProperty("result", out _)).IsFalse();
        await Assert.That(doc.RootElement.GetProperty("error").GetProperty("code").GetInt32()).IsEqualTo(-32601);

        cts.Cancel();
        await SwallowCancellation(runTask);
    }

    [Test]
    public async Task Inbound_server_request_handler_returning_null_writes_method_not_found_error() {
        await using var harness = new Harness();
        using var       cts     = new CancellationTokenSource();
        var             runTask = harness.Connection.RunAsync(cts.Token);

        harness.Connection.OnServerRequest = (_, _) => Task.FromResult<JsonElement?>(null);

        await harness.WriteFrameToConnectionAsync(
            """{"jsonrpc":"2.0","id":8,"method":"applyPatchApproval","params":{}}"""
        );

        var frame = await harness.ReadFrameFromConnectionAsync();
        using var doc = JsonDocument.Parse(frame);
        await Assert.That(doc.RootElement.GetProperty("id").GetInt64()).IsEqualTo(8L);
        await Assert.That(doc.RootElement.TryGetProperty("result", out _)).IsFalse();
        await Assert.That(doc.RootElement.GetProperty("error").GetProperty("code").GetInt32()).IsEqualTo(-32601);

        cts.Cancel();
        await SwallowCancellation(runTask);
    }

    [Test]
    public async Task Server_request_with_string_id_echoes_the_same_string_id_verbatim() {
        await using var harness = new Harness();
        using var       cts     = new CancellationTokenSource();
        var             runTask = harness.Connection.RunAsync(cts.Token);

        harness.Connection.OnServerRequest = (_, _) =>
            Task.FromResult<JsonElement?>(JsonSerializer.SerializeToElement(new { decision = "denied" }));

        await harness.WriteFrameToConnectionAsync(
            """{"jsonrpc":"2.0","id":"srv-generated-string-id","method":"execCommandApproval","params":{}}"""
        );

        var frame = await harness.ReadFrameFromConnectionAsync();
        using var doc = JsonDocument.Parse(frame);
        var idElement = doc.RootElement.GetProperty("id");
        await Assert.That(idElement.ValueKind).IsEqualTo(JsonValueKind.String);
        await Assert.That(idElement.GetString()).IsEqualTo("srv-generated-string-id");

        // Loop still alive after a string-id request (guards a naive long-forcing implementation).
        var tcs = new TaskCompletionSource<AcpNotification>(TaskCreationOptions.RunContinuationsAsynchronously);
        harness.Connection.OnNotification += n => tcs.TrySetResult(n);
        await harness.WriteFrameToConnectionAsync("""{"jsonrpc":"2.0","method":"turn.completed","params":{"threadId":"alive"}}""");
        var notification = await tcs.Task.WaitAsync(HangGuard);
        await Assert.That(notification.Params!.Value.GetProperty("threadId").GetString()).IsEqualTo("alive");

        cts.Cancel();
        await SwallowCancellation(runTask);
    }

    [Test]
    public async Task Cancelling_token_abandons_pending_request_without_hanging() {
        await using var harness = new Harness();
        using var       cts     = new CancellationTokenSource();
        var             runTask = harness.Connection.RunAsync(cts.Token);

        using var requestCts = new CancellationTokenSource();
        var       requestTask = harness.Connection.RequestAsync("sendTurn", null, requestCts.Token);

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
        var             runTask = harness.Connection.RunAsync(cts.Token);

        var tcs = new TaskCompletionSource<AcpNotification>(TaskCreationOptions.RunContinuationsAsynchronously);
        harness.Connection.OnNotification += n => tcs.TrySetResult(n);

        await harness.WriteFrameToConnectionAsync("{not valid json at all");
        await harness.WriteFrameToConnectionAsync("""{"jsonrpc":"2.0","method":"turn.completed","params":{"threadId":"alive"}}""");

        var notification = await tcs.Task.WaitAsync(HangGuard);
        await Assert.That(notification.Params!.Value.GetProperty("threadId").GetString()).IsEqualTo("alive");

        cts.Cancel();
        await SwallowCancellation(runTask);
    }

    [Test]
    public async Task Wrong_typed_error_code_on_a_pending_request_faults_the_caller_instead_of_hanging() {
        await using var harness = new Harness();
        using var       cts     = new CancellationTokenSource();
        var             runTask = harness.Connection.RunAsync(cts.Token);

        var requestTask = harness.Connection.RequestAsync("sendTurn", null, CancellationToken.None);
        var frame       = await harness.ReadFrameFromConnectionAsync();
        var id          = JsonDocument.Parse(frame).RootElement.GetProperty("id").GetInt64();

        // Well-formed JSON with a wrong-typed error.code — must fault the caller, not orphan it.
        await harness.WriteFrameToConnectionAsync(
            $$$"""{"jsonrpc":"2.0","id":{{{id}}},"error":{"code":"oops","message":"x"}}"""
        );

        var ex = await Assert.ThrowsAsync<CodexAppServerRpcException>(() => requestTask.WaitAsync(HangGuard));
        await Assert.That(ex).IsNotNull();

        cts.Cancel();
        await SwallowCancellation(runTask);
    }

    [Test]
    public async Task Non_object_error_payload_on_a_pending_request_faults_the_caller_instead_of_hanging() {
        await using var harness = new Harness();
        using var       cts     = new CancellationTokenSource();
        var             runTask = harness.Connection.RunAsync(cts.Token);

        var requestTask = harness.Connection.RequestAsync("sendTurn", null, CancellationToken.None);
        var frame       = await harness.ReadFrameFromConnectionAsync();
        var id          = JsonDocument.Parse(frame).RootElement.GetProperty("id").GetInt64();

        await harness.WriteFrameToConnectionAsync(
            $$$"""{"jsonrpc":"2.0","id":{{{id}}},"error":"totally-not-an-object"}"""
        );

        var ex = await Assert.ThrowsAsync<CodexAppServerRpcException>(() => requestTask.WaitAsync(HangGuard));
        await Assert.That(ex).IsNotNull();

        cts.Cancel();
        await SwallowCancellation(runTask);
    }

    [Test]
    public async Task Read_loop_ending_faults_a_pending_request_instead_of_hanging() {
        await using var harness = new Harness();
        using var       cts     = new CancellationTokenSource();
        var             runTask = harness.Connection.RunAsync(cts.Token);

        var requestTask = harness.Connection.RequestAsync("sendTurn", null, CancellationToken.None);
        await harness.ReadFrameFromConnectionAsync();

        // The app-server dies mid-turn: cancel the read loop, which must fault every pending request
        // rather than leave the runtime's turn awaiting forever.
        cts.Cancel();

        await Assert.ThrowsAsync<ObjectDisposedException>(() => requestTask.WaitAsync(HangGuard));
        await SwallowCancellation(runTask);
    }

    [Test]
    public async Task NotifyAsync_writes_notification_frame_without_id() {
        await using var harness = new Harness();
        using var       cts     = new CancellationTokenSource();
        var             runTask = harness.Connection.RunAsync(cts.Token);

        await harness.Connection.NotifyAsync("interruptTurn", null).WaitAsync(HangGuard);

        var frame = await harness.ReadFrameFromConnectionAsync();
        using var doc = JsonDocument.Parse(frame);
        await Assert.That(doc.RootElement.GetProperty("method").GetString()).IsEqualTo("interruptTurn");
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
}
