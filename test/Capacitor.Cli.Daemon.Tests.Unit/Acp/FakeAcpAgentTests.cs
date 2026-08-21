using System.Text.Json;
using Capacitor.Cli.Core.Acp;
using Capacitor.Cli.Daemon.Acp;
using Microsoft.Extensions.Logging.Abstractions;

namespace Capacitor.Cli.Daemon.Tests.Unit.Acp;

/// <summary>
/// Smoke tests for <see cref="FakeAcpAgent"/> itself — proves the reusable fixture correctly plays
/// the agent side of the ACP wire protocol against a real <see cref="AcpConnection"/>, so Task 9
/// (<c>AcpHostedAgentRuntime</c>) can build on it with confidence instead of re-deriving the wire
/// shapes from scratch.
/// </summary>
public class FakeAcpAgentTests {
    static readonly TimeSpan HangGuard = TimeSpan.FromSeconds(5);

    [Test]
    public async Task Initialize_completes_with_probe_confirmed_capabilities() {
        await using var fake = new FakeAcpAgent();
        using var       cts  = new CancellationTokenSource();

        var connection  = new AcpConnection(fake.ClientWriteStream, fake.ClientReadStream, NullLogger<AcpConnection>.Instance);
        var connRunTask = connection.RunAsync(cts.Token);
        var fakeRunTask = fake.RunAsync(cts.Token);

        var result = await connection.RequestAsync("initialize", null, CancellationToken.None).WaitAsync(HangGuard);

        await Assert.That(result.GetProperty("protocolVersion").GetInt32()).IsEqualTo(1);
        await Assert.That(result.GetProperty("agentCapabilities").GetProperty("loadSession").GetBoolean()).IsTrue();

        cts.Cancel();
        await SwallowCancellation(connRunTask);
        await SwallowCancellation(fakeRunTask);
    }

    [Test]
    public async Task SessionNew_returns_fixed_deterministic_session_id() {
        await using var fake = new FakeAcpAgent();
        using var       cts  = new CancellationTokenSource();

        var connection  = new AcpConnection(fake.ClientWriteStream, fake.ClientReadStream, NullLogger<AcpConnection>.Instance);
        var connRunTask = connection.RunAsync(cts.Token);
        var fakeRunTask = fake.RunAsync(cts.Token);

        var result = await connection.RequestAsync("session/new", null, CancellationToken.None).WaitAsync(HangGuard);

        await Assert.That(result.GetProperty("sessionId").GetString()).IsEqualTo(FakeAcpAgent.FixedSessionId);

        cts.Cancel();
        await SwallowCancellation(connRunTask);
        await SwallowCancellation(fakeRunTask);
    }

    [Test]
    public async Task SessionPrompt_with_default_script_emits_one_agent_message_chunk_then_resolves_end_turn() {
        await using var fake = new FakeAcpAgent();
        using var       cts  = new CancellationTokenSource();

        var connection  = new AcpConnection(fake.ClientWriteStream, fake.ClientReadStream, NullLogger<AcpConnection>.Instance);
        var connRunTask = connection.RunAsync(cts.Token);
        var fakeRunTask = fake.RunAsync(cts.Token);

        var notifications = new List<AcpNotification>();
        var doneTcs        = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        connection.OnNotification += n => {
            notifications.Add(n);
            if (n.Method == "session/update"
                && n.Params!.Value.GetProperty("update").GetProperty("sessionUpdate").GetString() == "agent_message_chunk") {
                doneTcs.TrySetResult();
            }
        };

        var promptResultTask = connection.RequestAsync("session/prompt", null, CancellationToken.None);

        await doneTcs.Task.WaitAsync(HangGuard);
        var promptResult = await promptResultTask.WaitAsync(HangGuard);

        await Assert.That(promptResult.GetProperty("stopReason").GetString()).IsEqualTo("end_turn");

        var chunkNotifications = notifications.Where(n =>
            n.Method == "session/update"
            && n.Params!.Value.GetProperty("update").GetProperty("sessionUpdate").GetString() == "agent_message_chunk"
        ).ToList();

        await Assert.That(chunkNotifications).Count().IsEqualTo(1);
        var text = chunkNotifications[0].Params!.Value.GetProperty("update").GetProperty("content").GetProperty("text").GetString();
        await Assert.That(text).IsNotNull();
        await Assert.That(text!.Length > 0).IsTrue();

        cts.Cancel();
        await SwallowCancellation(connRunTask);
        await SwallowCancellation(fakeRunTask);
    }

    [Test]
    public async Task Fake_records_inbound_calls_in_order_with_params_captured_verbatim() {
        await using var fake = new FakeAcpAgent();
        using var       cts  = new CancellationTokenSource();

        var connection  = new AcpConnection(fake.ClientWriteStream, fake.ClientReadStream, NullLogger<AcpConnection>.Instance);
        var connRunTask = connection.RunAsync(cts.Token);
        var fakeRunTask = fake.RunAsync(cts.Token);

        const string cwd = "/tmp/acp-fixture-test-cwd";

        await connection.RequestAsync("initialize", null, CancellationToken.None).WaitAsync(HangGuard);

        var sessionNewParams = JsonDocument.Parse($$"""{"cwd":"{{cwd}}","mcpServers":[]}""").RootElement.Clone();
        await connection.RequestAsync("session/new", sessionNewParams, CancellationToken.None).WaitAsync(HangGuard);

        await connection.RequestAsync("session/prompt", null, CancellationToken.None).WaitAsync(HangGuard);

        // Give the fake's dispatch loop a moment to append the session/prompt call to its recorded
        // list — the prompt's RequestAsync already resolved by the time we get here (it awaits the
        // response frame, which the fake writes only after appending the record), so no extra wait
        // is actually required, but we guard with a short poll to avoid a hypothetical race if the
        // fake ever reorders "append record" vs "write response".
        var deadline = DateTime.UtcNow + HangGuard;
        while (fake.ReceivedCalls.Count < 3 && DateTime.UtcNow < deadline)
            await Task.Delay(10);

        await Assert.That(fake.ReceivedCalls.Count).IsGreaterThanOrEqualTo(3);
        await Assert.That(fake.ReceivedCalls[0].Method).IsEqualTo("initialize");
        await Assert.That(fake.ReceivedCalls[1].Method).IsEqualTo("session/new");
        await Assert.That(fake.ReceivedCalls[2].Method).IsEqualTo("session/prompt");

        var capturedCwd = fake.ReceivedCalls[1].Params!.Value.GetProperty("cwd").GetString();
        await Assert.That(capturedCwd).IsEqualTo(cwd);

        cts.Cancel();
        await SwallowCancellation(connRunTask);
        await SwallowCancellation(fakeRunTask);
    }

    [Test]
    public async Task SessionCancel_notification_is_recorded_and_provokes_no_response_frame() {
        await using var fake = new FakeAcpAgent();
        using var       cts  = new CancellationTokenSource();

        var connection  = new AcpConnection(fake.ClientWriteStream, fake.ClientReadStream, NullLogger<AcpConnection>.Instance);
        var connRunTask = connection.RunAsync(cts.Token);
        var fakeRunTask = fake.RunAsync(cts.Token);

        var cancelParams = JsonDocument.Parse($$"""{"sessionId":"{{FakeAcpAgent.FixedSessionId}}"}""").RootElement.Clone();
        await connection.NotifyAsync("session/cancel", cancelParams).WaitAsync(HangGuard);

        // Send a subsequent request and confirm its response is the NEXT (and only) frame produced
        // in reaction — i.e. session/cancel provoked no response frame of its own on the wire.
        var result = await connection.RequestAsync("initialize", null, CancellationToken.None).WaitAsync(HangGuard);
        await Assert.That(result.GetProperty("protocolVersion").GetInt32()).IsEqualTo(1);

        var deadline = DateTime.UtcNow + HangGuard;
        while (fake.ReceivedCalls.Count < 1 && DateTime.UtcNow < deadline)
            await Task.Delay(10);

        var cancelCall = fake.ReceivedCalls.SingleOrDefault(c => c.Method == "session/cancel");
        await Assert.That(cancelCall.Method).IsEqualTo("session/cancel");
        await Assert.That(cancelCall.Params!.Value.GetProperty("sessionId").GetString()).IsEqualTo(FakeAcpAgent.FixedSessionId);

        cts.Cancel();
        await SwallowCancellation(connRunTask);
        await SwallowCancellation(fakeRunTask);
    }

    static async Task SwallowCancellation(Task task) {
        try {
            await task.WaitAsync(HangGuard);
        } catch (OperationCanceledException) {
            // expected shutdown path for this test's owned CTS
        }
    }

    /// <summary>
    /// Qodo daemon-review Q3: <see cref="FakeAcpAgent.RunAsync"/> dispatches each inbound line as
    /// untracked background work (<c>DispatchLineAsync</c>, fire-and-forget — required to avoid the
    /// self-deadlock documented on that method) and PRE-FIX only <c>Debug.WriteLine</c>'d a fault,
    /// so a faulted dispatch manifested to a test as a silent hang/timeout (whatever the test was
    /// awaiting from the connection never arrives) rather than a clear failure pointing at the fake.
    /// This test forces a dispatch fault directly — malformed JSON written straight onto the fake's
    /// simulated stdin (<see cref="FakeAcpAgent.ClientWriteStream"/>), which <c>DispatchLineAsync</c>'s
    /// unguarded <c>JsonDocument.Parse(line)</c> throws on — and proves the fault is captured and
    /// surfaced via <see cref="FakeAcpAgent.DispatchFault"/> rather than silently swallowed.
    /// </summary>
    [Test]
    public async Task DispatchLineAsync_fault_is_captured_and_exposed_via_DispatchFault() {
        var fake = new FakeAcpAgent();
        using var cts = new CancellationTokenSource();

        var fakeRunTask = fake.RunAsync(cts.Token);

        var bytes = System.Text.Encoding.UTF8.GetBytes("not valid json\n");
        await fake.ClientWriteStream.WriteAsync(bytes, CancellationToken.None).AsTask().WaitAsync(HangGuard);
        await fake.ClientWriteStream.FlushAsync(CancellationToken.None).WaitAsync(HangGuard);

        var deadline = DateTime.UtcNow + HangGuard;
        while (fake.DispatchFault is null && DateTime.UtcNow < deadline)
            await Task.Delay(10);

        await Assert.That(fake.DispatchFault).IsNotNull();
        await Assert.That(fake.DispatchFault).IsTypeOf<JsonException>();

        cts.Cancel();
        await SwallowCancellation(fakeRunTask);

        // DisposeAsync rethrows the captured fault (proven by the next test) — this test's own
        // focus is DispatchFault itself, so swallow the rethrow here rather than duplicating that
        // assertion.
        try { await fake.DisposeAsync(); } catch (JsonException) { }
    }

    /// <summary>
    /// Qodo daemon-review Q3: <see cref="FakeAcpAgent.DisposeAsync"/> rethrows the FIRST captured
    /// dispatch fault (via <see cref="System.Runtime.ExceptionServices.ExceptionDispatchInfo"/>, to
    /// preserve the original stack trace) so a test that never explicitly inspects
    /// <see cref="FakeAcpAgent.DispatchFault"/> — the common case, since most tests just
    /// <c>await using var fake = new FakeAcpAgent();</c> — still fails loudly instead of the fault
    /// being silently dropped when the fixture is torn down.
    /// </summary>
    [Test]
    public async Task DisposeAsync_rethrows_captured_dispatch_fault() {
        var fake = new FakeAcpAgent();
        using var cts = new CancellationTokenSource();

        var fakeRunTask = fake.RunAsync(cts.Token);

        var bytes = System.Text.Encoding.UTF8.GetBytes("not valid json\n");
        await fake.ClientWriteStream.WriteAsync(bytes, CancellationToken.None).AsTask().WaitAsync(HangGuard);
        await fake.ClientWriteStream.FlushAsync(CancellationToken.None).WaitAsync(HangGuard);

        var deadline = DateTime.UtcNow + HangGuard;
        while (fake.DispatchFault is null && DateTime.UtcNow < deadline)
            await Task.Delay(10);
        await Assert.That(fake.DispatchFault).IsNotNull();

        cts.Cancel();
        await SwallowCancellation(fakeRunTask);

        await Assert.ThrowsAsync<JsonException>(async () => await fake.DisposeAsync());
    }
}
