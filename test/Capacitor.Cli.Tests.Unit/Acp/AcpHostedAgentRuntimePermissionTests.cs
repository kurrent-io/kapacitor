// test/Capacitor.Cli.Tests.Unit/Acp/AcpHostedAgentRuntimePermissionTests.cs
using Capacitor.Cli.Core;
using Capacitor.Cli.Daemon.Acp;
using Capacitor.Cli.Daemon.Services;
using Microsoft.Extensions.Logging.Abstractions;

namespace Capacitor.Cli.Tests.Unit.Acp;

/// <summary>
/// End-to-end: <see cref="FakeAcpAgent"/> sends a real
/// <c>session/request_permission</c> server→client request mid-turn; the runtime's wired
/// <see cref="AcpInteractionBridge"/> forwards it to an injected "ask the server" delegate and
/// writes the JSON-RPC response back to the wire only once that delegate resolves — proving the
/// agent's request genuinely blocks on the human decision rather than getting an immediate
/// default-decline (the runtime's prior behavior, since <c>OnServerRequest</c> was unset).
/// </summary>
public class AcpHostedAgentRuntimePermissionTests {
    static readonly TimeSpan HangGuard = TimeSpan.FromSeconds(5);

    sealed class FakeAcpProcess : IAcpProcess {
        readonly TaskCompletionSource _exited = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int  Pid       { get; init; } = 4242;
        public bool HasExited { get; private set; }
        public int? ExitCode  { get; private set; }
        public void SignalExited(int exitCode = 0) { HasExited = true; ExitCode = exitCode; _exited.TrySetResult(); }
        public Task WaitForExitAsync(TimeSpan? timeout = null) => _exited.Task;
        public Task TerminateAsync(TimeSpan? timeout = null) { SignalExited(); return Task.CompletedTask; }
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    [Test]
    public async Task PermissionRequest_BlocksResponseUntilInjectedDecisionResolves() {
        var fake    = new FakeAcpAgent();
        var conn    = new AcpConnection(fake.ClientWriteStream, fake.ClientReadStream, NullLogger.Instance);
        var process = new FakeAcpProcess();

        var gate = new TaskCompletionSource<AcpInteractionDecision>(TaskCreationOptions.RunContinuationsAsynchronously);

        var runtime = new AcpHostedAgentRuntime(
            conn,
            process,
            NullLogger.Instance,
            requestInteraction: (req, ct) => gate.Task);

        using var cts = new CancellationTokenSource();
        var fakeRunTask = fake.RunAsync(cts.Token);

        await runtime.StartAsync("/abs/worktree", "", cts.Token).WaitAsync(HangGuard);

        // Script the fake to send a session/request_permission mid-turn, then answer session/prompt
        // normally afterward — the fake's new scripting mechanism (Step 3 below) lets a test drive
        // this without hand-rolling raw JSON-RPC frames.
        fake.EnqueuePermissionRequestDuringNextPrompt(
            toolCallJson: """{"toolCallId":"call-1","title":"Run ls"}""",
            optionsJson: """[{"optionId":"allow-once","name":"Allow","kind":"allow_once"},{"optionId":"deny","name":"Deny","kind":"reject_once"}]""");

        await runtime.SendUserInputAsync("run ls").WaitAsync(HangGuard);

        // The fake recorded the outbound session/request_permission call, but hasn't received a
        // response yet — the runtime's bridge is awaiting `gate.Task`, which we haven't resolved.
        var deadline = DateTime.UtcNow + HangGuard;
        while (!fake.ReceivedCalls.Any(c => c.Method == "session/request_permission" || fake.SentServerRequests.Any(r => r.Method == "session/request_permission")) && DateTime.UtcNow < deadline)
            await Task.Delay(10);

        await Assert.That(fake.SentServerRequests.Any(r => r.Method == "session/request_permission")).IsTrue();
        await Assert.That(fake.LastServerRequestResponse).IsNull(); // still pending — not yet answered

        // Now resolve the human decision. Round-7 spec-review Finding 5: supply a REAL, matching
        // SelectedOptionId ("allow-once", one of the two options offered above) rather than null —
        // a null SelectedOptionId must map to `cancelled` (see AcpInteractionBridgeTests' fail-closed
        // tests), never fall back to the first offered option, so this end-to-end test must not rely
        // on that removed behavior either to observe a `selected` outcome below.
        gate.TrySetResult(new AcpInteractionDecision("allow", "allow-once", "Allow", null, null, null));

        var responseDeadline = DateTime.UtcNow + HangGuard;
        while (fake.LastServerRequestResponse is null && DateTime.UtcNow < responseDeadline)
            await Task.Delay(10);

        await Assert.That(fake.LastServerRequestResponse).IsNotNull();
        var outcome = fake.LastServerRequestResponse!.Value.GetProperty("outcome");
        await Assert.That(outcome.GetProperty("outcome").GetString()).IsEqualTo("selected");
        await Assert.That(outcome.GetProperty("optionId").GetString()).IsEqualTo("allow-once");

        cts.Cancel();
        try { await fakeRunTask.WaitAsync(HangGuard); } catch (OperationCanceledException) { }
        await runtime.DisposeAsync();
        await fake.DisposeAsync();
    }

    [Test]
    public async Task PermissionRequest_NoInteractionDelegateWired_DefaultsToMethodNotFound() {
        // Backward-compat: a runtime constructed WITHOUT the new optional delegate (matching every
        // call site before this change) keeps the original exact default-decline posture — OnServerRequest
        // stays effectively unset for permission requests, so AcpConnection answers with a
        // JSON-RPC "Method not found" error, not a crash or hang.
        var fake    = new FakeAcpAgent();
        var conn    = new AcpConnection(fake.ClientWriteStream, fake.ClientReadStream, NullLogger.Instance);
        var process = new FakeAcpProcess();

        var runtime = new AcpHostedAgentRuntime(conn, process, NullLogger.Instance); // no requestInteraction arg

        using var cts = new CancellationTokenSource();
        var fakeRunTask = fake.RunAsync(cts.Token);

        await runtime.StartAsync("/abs/worktree", "", cts.Token).WaitAsync(HangGuard);

        fake.EnqueuePermissionRequestDuringNextPrompt(
            toolCallJson: """{"toolCallId":"call-1","title":"Run ls"}""",
            optionsJson: """[{"optionId":"allow-once","name":"Allow","kind":"allow_once"}]""");

        await runtime.SendUserInputAsync("run ls").WaitAsync(HangGuard);

        var deadline = DateTime.UtcNow + HangGuard;
        while (fake.LastServerRequestError is null && DateTime.UtcNow < deadline)
            await Task.Delay(10);

        await Assert.That(fake.LastServerRequestError).IsNotNull();
        await Assert.That(fake.LastServerRequestError!.Value.GetProperty("code").GetInt32()).IsEqualTo(-32601);

        cts.Cancel();
        try { await fakeRunTask.WaitAsync(HangGuard); } catch (OperationCanceledException) { }
        await runtime.DisposeAsync();
        await fake.DisposeAsync();
    }

    /// <summary>
    /// Spec-review Finding 2, end-to-end THROUGH the real <see cref="AcpInteractionBridge"/> (not a
    /// direct <c>MapPermissionDecision</c> unit call — <see cref="AcpInteractionBridgeTests"/>
    /// already covers that in isolation): a cancellation decision, delivered exactly the way a real
    /// "human clicked Deny" or "session ended while pending" resolution would arrive (an
    /// <see cref="AcpInteractionDecision"/> with <see cref="AcpInteractionDecision.Outcome"/> equal
    /// to the canonical <c>"cancel"</c> string), must produce an ACP <c>cancelled</c> response on the
    /// wire — NEVER <c>selected</c> — when routed through the runtime's wired
    /// <see cref="AcpConnection.OnServerRequest"/> exactly as production traffic would be.
    /// </summary>
    [Test]
    public async Task PermissionRequest_CancelledDecision_NeverProducesSelectedResponse() {
        var fake    = new FakeAcpAgent();
        var conn    = new AcpConnection(fake.ClientWriteStream, fake.ClientReadStream, NullLogger.Instance);
        var process = new FakeAcpProcess();

        var runtime = new AcpHostedAgentRuntime(
            conn,
            process,
            NullLogger.Instance,
            requestInteraction: (req, ct) => Task.FromResult(new AcpInteractionDecision("cancel", null, null, null, null, null)));

        using var cts = new CancellationTokenSource();
        var fakeRunTask = fake.RunAsync(cts.Token);

        await runtime.StartAsync("/abs/worktree", "", cts.Token).WaitAsync(HangGuard);

        fake.EnqueuePermissionRequestDuringNextPrompt(
            toolCallJson: """{"toolCallId":"call-1","title":"Run rm -rf /"}""",
            optionsJson: """[{"optionId":"allow-once","name":"Allow","kind":"allow_once"},{"optionId":"deny","name":"Deny","kind":"reject_once"}]""");

        await runtime.SendUserInputAsync("run it").WaitAsync(HangGuard);

        var deadline = DateTime.UtcNow + HangGuard;
        while (fake.LastServerRequestResponse is null && DateTime.UtcNow < deadline)
            await Task.Delay(10);

        await Assert.That(fake.LastServerRequestResponse).IsNotNull();
        var outcome = fake.LastServerRequestResponse!.Value.GetProperty("outcome");
        await Assert.That(outcome.GetProperty("outcome").GetString()).IsEqualTo("cancelled");
        await Assert.That(outcome.TryGetProperty("optionId", out _)).IsFalse();

        cts.Cancel();
        try { await fakeRunTask.WaitAsync(HangGuard); } catch (OperationCanceledException) { }
        await runtime.DisposeAsync();
        await fake.DisposeAsync();
    }

    /// <summary>
    /// Spec-review Finding 3(b): the runtime disposing (connection closing) WHILE a
    /// <c>session/request_permission</c> is genuinely pending — <c>requestInteraction</c>
    /// never completes on its own — must resolve the pending request to the well-formed ACP
    /// <c>cancelled</c> response exactly once, not hang forever and not surface as an unhandled
    /// exception anywhere in the dispose path. <see cref="AcpHostedAgentRuntime.DisposeAsync"/>
    /// cancels its own <c>_cts</c>, which is the SAME token threaded all the way through
    /// <see cref="AcpConnection"/>'s read loop into this bridge's <c>ct</c> parameter — so the
    /// injected delegate below observes that same cancellation and the bridge (Task B3's fix)
    /// converts it to <c>cancelled</c> rather than letting it propagate as an unhandled
    /// <see cref="OperationCanceledException"/> out of <c>AcpConnection.HandleServerRequestAsync</c>.
    /// </summary>
    [Test]
    public async Task PermissionRequest_ConnectionDisposedWhilePending_ResolvesToCancelledExactlyOnce() {
        var fake    = new FakeAcpAgent();
        var conn    = new AcpConnection(fake.ClientWriteStream, fake.ClientReadStream, NullLogger.Instance);
        var process = new FakeAcpProcess();

        var runtime = new AcpHostedAgentRuntime(
            conn,
            process,
            NullLogger.Instance,
            // Never resolves on its own — only cancellation (from DisposeAsync below) ends this.
            requestInteraction: (req, ct) => Task.Delay(Timeout.Infinite, ct).ContinueWith(_ => default(AcpInteractionDecision), TaskScheduler.Default));

        using var cts = new CancellationTokenSource();
        var fakeRunTask = fake.RunAsync(cts.Token);

        await runtime.StartAsync("/abs/worktree", "", cts.Token).WaitAsync(HangGuard);

        fake.EnqueuePermissionRequestDuringNextPrompt(
            toolCallJson: """{"toolCallId":"call-1","title":"Run ls"}""",
            optionsJson: """[{"optionId":"allow-once","name":"Allow","kind":"allow_once"}]""");

        await runtime.SendUserInputAsync("run ls").WaitAsync(HangGuard);

        // Confirm the request genuinely went out and is genuinely still pending before disposing.
        var sentDeadline = DateTime.UtcNow + HangGuard;
        while (!fake.SentServerRequests.Any(r => r.Method == "session/request_permission") && DateTime.UtcNow < sentDeadline)
            await Task.Delay(10);
        await Assert.That(fake.SentServerRequests.Any(r => r.Method == "session/request_permission")).IsTrue();
        await Assert.That(fake.LastServerRequestResponse).IsNull();
        await Assert.That(fake.LastServerRequestError).IsNull();

        // Dispose while pending — this is the disconnect this finding covers.
        await runtime.DisposeAsync().AsTask().WaitAsync(HangGuard);

        // The bridge must have written back a well-formed `cancelled` response before/as part of
        // teardown — never an unhandled exception, never a bare JSON-RPC "Internal error", and
        // never left permanently unanswered. DisposeAsync awaiting the connection's own read loop
        // only guarantees the response bytes were WRITTEN to the wire, not that the fake's separate
        // RunAsync task has already read and processed them — so poll briefly rather than asserting
        // immediately.
        var writtenBackDeadline = DateTime.UtcNow + HangGuard;
        while (fake.LastServerRequestResponse is null && DateTime.UtcNow < writtenBackDeadline)
            await Task.Delay(10);

        await Assert.That(fake.LastServerRequestResponse).IsNotNull();
        var outcome = fake.LastServerRequestResponse!.Value.GetProperty("outcome");
        await Assert.That(outcome.GetProperty("outcome").GetString()).IsEqualTo("cancelled");
        await Assert.That(fake.LastServerRequestError).IsNull(); // NOT AcpConnection's generic -32603 fallback

        cts.Cancel();
        try { await fakeRunTask.WaitAsync(HangGuard); } catch (OperationCanceledException) { }
        await fake.DisposeAsync();
    }

    /// <summary>
    /// Qodo daemon-review Q2, end-to-end through the real wiring: <see cref="AcpHostedAgentRuntime"/>
    /// used to wire <c>OnServerRequest</c> as <c>(request, ct) =&gt; _interactionBridge.HandleAsync(request, _sessionId ?? "", ct)</c> —
    /// closing over the runtime's own <c>_sessionId</c> field rather than trusting the inbound
    /// request's OWN <c>sessionId</c> param. <c>FakeAcpAgent</c>'s scripted
    /// <c>session/request_permission</c> frame always carries <see cref="FakeAcpAgent.FixedSessionId"/>
    /// in its params (see <c>BuildRequestPermissionFrame</c>), which happens to equal the same value
    /// <c>StartAsync</c>'s <c>session/new</c> response resolves into <c>_sessionId</c> — so the OLD
    /// wiring and the NEW params-sourced wiring were indistinguishable via that path alone. This test
    /// closes that gap by asserting directly that the forwarded <see cref="AcpInteractionRequest.AcpSessionId"/>
    /// equals the fixed session id from the wire, proving the value is genuinely sourced from the
    /// request rather than incidentally matching a runtime field of the same value.
    /// </summary>
    [Test]
    public async Task PermissionRequest_ForwardedAcpSessionId_MatchesRequestParamsSessionId() {
        var fake    = new FakeAcpAgent();
        var conn    = new AcpConnection(fake.ClientWriteStream, fake.ClientReadStream, NullLogger.Instance);
        var process = new FakeAcpProcess();

        AcpInteractionRequest? captured = null;

        var runtime = new AcpHostedAgentRuntime(
            conn,
            process,
            NullLogger.Instance,
            requestInteraction: (req, ct) => { captured = req; return Task.FromResult(new AcpInteractionDecision("allow", "allow-once", "Allow", null, null, null)); });

        using var cts = new CancellationTokenSource();
        var fakeRunTask = fake.RunAsync(cts.Token);

        await runtime.StartAsync("/abs/worktree", "", cts.Token).WaitAsync(HangGuard);

        fake.EnqueuePermissionRequestDuringNextPrompt(
            toolCallJson: """{"toolCallId":"call-1","title":"Run ls"}""",
            optionsJson: """[{"optionId":"allow-once","name":"Allow","kind":"allow_once"}]""");

        await runtime.SendUserInputAsync("run ls").WaitAsync(HangGuard);

        var deadline = DateTime.UtcNow + HangGuard;
        while (captured is null && DateTime.UtcNow < deadline)
            await Task.Delay(10);

        await Assert.That(captured).IsNotNull();
        await Assert.That(captured!.Value.AcpSessionId).IsEqualTo(FakeAcpAgent.FixedSessionId);
        await Assert.That(captured.Value.AcpSessionId).IsNotEmpty();

        cts.Cancel();
        try { await fakeRunTask.WaitAsync(HangGuard); } catch (OperationCanceledException) { }
        await runtime.DisposeAsync();
        await fake.DisposeAsync();
    }
}
