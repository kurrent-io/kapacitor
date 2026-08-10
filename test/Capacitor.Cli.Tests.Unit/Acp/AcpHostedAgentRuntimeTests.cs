// test/Capacitor.Cli.Tests.Unit/Acp/AcpHostedAgentRuntimeTests.cs
using System.Threading.Channels;
using Capacitor.Cli.Daemon.Acp;
using Capacitor.Cli.Daemon.Services;
using Microsoft.Extensions.Logging.Abstractions;

namespace Capacitor.Cli.Tests.Unit.Acp;

/// <summary>
/// Exercises <see cref="AcpHostedAgentRuntime"/> end-to-end against <see cref="FakeAcpAgent"/> — no
/// real <c>cursor-agent</c> process is spawned; <see cref="FakeAcpProcess"/> stands in for the
/// process-lifecycle side (Task 10's factory implements the real one over
/// <see cref="System.Diagnostics.Process"/>).
/// </summary>
public class AcpHostedAgentRuntimeTests {
    static readonly TimeSpan HangGuard = TimeSpan.FromSeconds(5);

    /// <summary>
    /// <see cref="IAcpProcess"/> fake whose <see cref="WaitForExitAsync"/> genuinely blocks (like
    /// the real <c>AcpChildProcess</c> over a live child process) until <see cref="SignalExited"/>
    /// is called or the process is <see cref="TerminateAsync"/>d — needed to exercise Fix
    /// E's "stay open until the process exits" contract on <c>AcpHostedAgentRuntime.ReadOutputAsync</c>.
    /// The un-signalled default (used by every pre-existing test in this file, which never calls
    /// <see cref="SignalExited"/>) matches the OLD <c>Task.CompletedTask</c> behavior closely enough
    /// for handshake/update/stop tests that don't touch <c>ReadOutputAsync</c> at all.
    /// </summary>
    sealed class FakeAcpProcess : IAcpProcess {
        readonly TaskCompletionSource _exited = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public int  Pid            { get; init; } = 4242;
        public bool HasExited      { get; private set; }
        public int? ExitCode       { get; private set; }
        public int  TerminateCalls { get; private set; }
        public int  DisposeCalls   { get; private set; }

        /// <summary>Simulates the child process exiting on its own (no Terminate call).</summary>
        public void SignalExited(int exitCode = 0) {
            HasExited = true;
            ExitCode  = exitCode;
            _exited.TrySetResult();
        }

        public async Task WaitForExitAsync(TimeSpan? timeout = null) {
            if (timeout is { } t) {
                await Task.WhenAny(_exited.Task, Task.Delay(t)).ConfigureAwait(false);
            } else {
                await _exited.Task.ConfigureAwait(false);
            }
        }

        public Task TerminateAsync(TimeSpan? timeout = null) {
            TerminateCalls++;
            SignalExited();

            return Task.CompletedTask;
        }

        public ValueTask DisposeAsync() {
            DisposeCalls++;

            return ValueTask.CompletedTask;
        }
    }

    sealed class Harness : IAsyncDisposable {
        public FakeAcpAgent          Fake    { get; }
        public AcpConnection         Conn    { get; }
        public FakeAcpProcess        Process { get; }
        public AcpHostedAgentRuntime Runtime { get; }
        public CancellationTokenSource Cts   { get; } = new();

        Task _fakeRunTask = Task.CompletedTask;

        public Harness() {
            Fake    = new FakeAcpAgent();
            Conn    = new AcpConnection(Fake.ClientWriteStream, Fake.ClientReadStream, NullLogger.Instance);
            Process = new FakeAcpProcess();
            Runtime = new AcpHostedAgentRuntime(Conn, Process, NullLogger.Instance);
        }

        public void StartFakeAgentLoop() => _fakeRunTask = Fake.RunAsync(Cts.Token);

        public async ValueTask DisposeAsync() {
            Cts.Cancel();
            try {
                await _fakeRunTask.WaitAsync(HangGuard);
            } catch (OperationCanceledException) {
                // expected shutdown path
            }
            await Runtime.DisposeAsync();
            await Fake.DisposeAsync();
            Cts.Dispose();
        }
    }

    [Test]
    public async Task StartAsync_performs_initialize_then_session_new_then_session_prompt_in_order() {
        await using var h = new Harness();
        h.StartFakeAgentLoop();

        await h.Runtime.StartAsync("/abs/worktree", "do the thing", h.Cts.Token).WaitAsync(HangGuard);

        // Fix E: StartAsync returns once session/new resolves — it fires the initial
        // session/prompt as untracked background work rather than awaiting it, so the fake may not
        // have received (or recorded) it yet at this exact instant. Poll rather than asserting
        // immediately.
        var deadline = DateTime.UtcNow + HangGuard;
        while (h.Fake.ReceivedCalls.Count < 3 && DateTime.UtcNow < deadline)
            await Task.Delay(10);

        var calls = h.Fake.ReceivedCalls;
        await Assert.That(calls.Count).IsGreaterThanOrEqualTo(3);

        await Assert.That(calls[0].Method).IsEqualTo("initialize");

        await Assert.That(calls[1].Method).IsEqualTo("session/new");
        await Assert.That(calls[1].Params!.Value.GetProperty("cwd").GetString()).IsEqualTo("/abs/worktree");

        await Assert.That(calls[2].Method).IsEqualTo("session/prompt");
        var promptBlocks = calls[2].Params!.Value.GetProperty("prompt");
        await Assert.That(promptBlocks[0].GetProperty("text").GetString()).IsEqualTo("do the thing");
    }

    // A live capability probe against the real cursor-agent found it performs file/shell operations
    // itself and never requests client fs/terminal, so the daemon must keep advertising NONE of
    // them — advertising a capability we can't safely enforce is exactly the failure mode this
    // locks against. Fails loudly if a future change flips one on without revisiting that decision.
    [Test]
    public async Task StartAsync_advertises_no_fs_or_terminal_client_capabilities() {
        await using var h = new Harness();
        h.StartFakeAgentLoop();

        await h.Runtime.StartAsync("/abs/worktree", "do the thing", h.Cts.Token).WaitAsync(HangGuard);

        var deadline = DateTime.UtcNow + HangGuard;
        while (h.Fake.ReceivedCalls.Count < 1 && DateTime.UtcNow < deadline)
            await Task.Delay(10);

        var calls = h.Fake.ReceivedCalls;
        await Assert.That(calls.Count).IsGreaterThanOrEqualTo(1);
        await Assert.That(calls[0].Method).IsEqualTo("initialize");

        var clientCapabilities = calls[0].Params!.Value.GetProperty("clientCapabilities");
        await Assert.That(clientCapabilities.GetProperty("fs").GetProperty("readTextFile").GetBoolean()).IsFalse();
        await Assert.That(clientCapabilities.GetProperty("fs").GetProperty("writeTextFile").GetBoolean()).IsFalse();
        await Assert.That(clientCapabilities.GetProperty("terminal").GetBoolean()).IsFalse();
    }

    [Test]
    public async Task Scripted_agent_message_chunk_update_is_surfaced_as_reduced_DTO() {
        await using var h = new Harness();

        var update = FakeAcpAgent.BuildSessionUpdateNotification(
            FakeAcpAgent.FixedSessionId,
            FakeAcpAgent.BuildAgentMessageChunkUpdate("hello there"));
        var result = System.Text.Json.JsonDocument.Parse("""{"stopReason":"end_turn"}""").RootElement.Clone();
        h.Fake.EnqueuePromptScript(new[] { update }, result);

        h.StartFakeAgentLoop();

        await h.Runtime.StartAsync("/abs/worktree", "prompt", h.Cts.Token).WaitAsync(HangGuard);

        var received = await h.Runtime.Updates.ReadAsync().AsTask().WaitAsync(HangGuard);

        await Assert.That(received.Kind).IsEqualTo(AcpUpdateKind.AgentMessageChunk);
        await Assert.That(received.Text).IsEqualTo("hello there");
    }

    [Test]
    public async Task Unknown_sessionUpdate_variant_is_surfaced_as_Unknown_with_Raw_and_does_not_throw() {
        await using var h = new Harness();

        var weirdUpdate = System.Text.Json.JsonDocument.Parse("""{"sessionUpdate":"some_future_variant","foo":"bar"}""").RootElement.Clone();
        var notification = FakeAcpAgent.BuildSessionUpdateNotification(FakeAcpAgent.FixedSessionId, weirdUpdate);
        var result = System.Text.Json.JsonDocument.Parse("""{"stopReason":"end_turn"}""").RootElement.Clone();
        h.Fake.EnqueuePromptScript(new[] { notification }, result);

        h.StartFakeAgentLoop();

        await h.Runtime.StartAsync("/abs/worktree", "prompt", h.Cts.Token).WaitAsync(HangGuard);

        var received = await h.Runtime.Updates.ReadAsync().AsTask().WaitAsync(HangGuard);

        await Assert.That(received.Kind).IsEqualTo(AcpUpdateKind.Unknown);
        await Assert.That(received.Raw).IsNotNull();
        await Assert.That(received.Raw!.Value.GetProperty("foo").GetString()).IsEqualTo("bar");
    }

    [Test]
    public async Task SessionInfo_update_is_reduced_to_SessionInfo_with_the_captured_title() {
        await using var h = new Harness();

        var infoUpdate   = FakeAcpAgent.BuildSessionInfoUpdate("Shell Reporter");
        var notification = FakeAcpAgent.BuildSessionUpdateNotification(FakeAcpAgent.FixedSessionId, infoUpdate);
        var result       = System.Text.Json.JsonDocument.Parse("""{"stopReason":"end_turn"}""").RootElement.Clone();
        h.Fake.EnqueuePromptScript(new[] { notification }, result);

        h.StartFakeAgentLoop();
        await h.Runtime.StartAsync("/abs/worktree", "prompt", h.Cts.Token).WaitAsync(HangGuard);

        var received = await h.Runtime.Updates.ReadAsync().AsTask().WaitAsync(HangGuard);

        await Assert.That(received.Kind).IsEqualTo(AcpUpdateKind.SessionInfo);
        await Assert.That(received.Title).IsEqualTo("Shell Reporter");
    }

    [Test]
    public async Task SessionInfo_update_with_a_non_string_title_reduces_to_null_title_without_dropping_the_frame() {
        await using var h = new Harness();

        // A schema-drift session_info_update whose title is a NUMBER, not a string. GetStringOrNull
        // must treat it as absent (Title=null) rather than throwing — a thrown GetString() would
        // bubble up and make the read loop skip the whole notification frame.
        var badTitle     = System.Text.Json.JsonDocument.Parse("""{"sessionUpdate":"session_info_update","title":123}""").RootElement.Clone();
        var notification = FakeAcpAgent.BuildSessionUpdateNotification(FakeAcpAgent.FixedSessionId, badTitle);
        var result       = System.Text.Json.JsonDocument.Parse("""{"stopReason":"end_turn"}""").RootElement.Clone();
        h.Fake.EnqueuePromptScript(new[] { notification }, result);

        h.StartFakeAgentLoop();
        await h.Runtime.StartAsync("/abs/worktree", "prompt", h.Cts.Token).WaitAsync(HangGuard);

        var received = await h.Runtime.Updates.ReadAsync().AsTask().WaitAsync(HangGuard);

        await Assert.That(received.Kind).IsEqualTo(AcpUpdateKind.SessionInfo);
        await Assert.That(received.Title).IsNull();
    }

    // ── Option B task 1: Reduce() tool-call/tool-result field capture ──────────────

    [Test]
    public async Task ToolCall_update_captures_ToolInputJson_from_rawInput() {
        await using var h = new Harness();

        var toolCall = FakeAcpAgent.BuildToolCallUpdate(
            "call-1", "Run shell command", "execute", "pending",
            rawInputJson: """{"command":"echo hi"}""");
        var notification = FakeAcpAgent.BuildSessionUpdateNotification(FakeAcpAgent.FixedSessionId, toolCall);
        var result = System.Text.Json.JsonDocument.Parse("""{"stopReason":"end_turn"}""").RootElement.Clone();
        h.Fake.EnqueuePromptScript(new[] { notification }, result);

        h.StartFakeAgentLoop();
        await h.Runtime.StartAsync("/abs/worktree", "prompt", h.Cts.Token).WaitAsync(HangGuard);

        var received = await h.Runtime.Updates.ReadAsync().AsTask().WaitAsync(HangGuard);

        await Assert.That(received.Kind).IsEqualTo(AcpUpdateKind.ToolCall);
        await Assert.That(received.ToolCallId).IsEqualTo("call-1");
        await Assert.That(received.ToolTitle).IsEqualTo("Run shell command");
        await Assert.That(received.ToolInputJson).IsEqualTo("""{"command":"echo hi"}""");
    }

    [Test]
    public async Task ToolCall_update_without_rawInput_leaves_ToolInputJson_null() {
        await using var h = new Harness();

        var toolCall = FakeAcpAgent.BuildToolCallUpdate("call-1", "Run shell command", "execute", "pending");
        var notification = FakeAcpAgent.BuildSessionUpdateNotification(FakeAcpAgent.FixedSessionId, toolCall);
        var result = System.Text.Json.JsonDocument.Parse("""{"stopReason":"end_turn"}""").RootElement.Clone();
        h.Fake.EnqueuePromptScript(new[] { notification }, result);

        h.StartFakeAgentLoop();
        await h.Runtime.StartAsync("/abs/worktree", "prompt", h.Cts.Token).WaitAsync(HangGuard);

        var received = await h.Runtime.Updates.ReadAsync().AsTask().WaitAsync(HangGuard);

        await Assert.That(received.ToolInputJson).IsNull();
    }

    [Test]
    public async Task Status_only_ToolCallUpdate_captures_status_but_no_ToolResultText() {
        await using var h = new Harness();

        var statusUpdate = FakeAcpAgent.BuildToolCallStatusUpdate("call-1", "in_progress");
        var notification = FakeAcpAgent.BuildSessionUpdateNotification(FakeAcpAgent.FixedSessionId, statusUpdate);
        var result = System.Text.Json.JsonDocument.Parse("""{"stopReason":"end_turn"}""").RootElement.Clone();
        h.Fake.EnqueuePromptScript(new[] { notification }, result);

        h.StartFakeAgentLoop();
        await h.Runtime.StartAsync("/abs/worktree", "prompt", h.Cts.Token).WaitAsync(HangGuard);

        var received = await h.Runtime.Updates.ReadAsync().AsTask().WaitAsync(HangGuard);

        await Assert.That(received.Kind).IsEqualTo(AcpUpdateKind.ToolCallUpdate);
        await Assert.That(received.ToolStatus).IsEqualTo("in_progress");
        await Assert.That(received.ToolResultText).IsNull();
        await Assert.That(received.ToolIsError).IsFalse();
    }

    [Test]
    public async Task Terminal_ToolCallUpdate_captures_ToolResultText_from_content_text_block() {
        await using var h = new Harness();

        var statusUpdate = FakeAcpAgent.BuildToolCallStatusUpdate("call-1", "completed", resultText: "hi\n");
        var notification = FakeAcpAgent.BuildSessionUpdateNotification(FakeAcpAgent.FixedSessionId, statusUpdate);
        var result = System.Text.Json.JsonDocument.Parse("""{"stopReason":"end_turn"}""").RootElement.Clone();
        h.Fake.EnqueuePromptScript(new[] { notification }, result);

        h.StartFakeAgentLoop();
        await h.Runtime.StartAsync("/abs/worktree", "prompt", h.Cts.Token).WaitAsync(HangGuard);

        var received = await h.Runtime.Updates.ReadAsync().AsTask().WaitAsync(HangGuard);

        await Assert.That(received.ToolStatus).IsEqualTo("completed");
        await Assert.That(received.ToolResultText).IsEqualTo("hi\n");
        await Assert.That(received.ToolIsError).IsFalse();
    }

    [Test]
    public async Task Terminal_failed_ToolCallUpdate_sets_ToolIsError_true() {
        await using var h = new Harness();

        var statusUpdate = FakeAcpAgent.BuildToolCallStatusUpdate("call-1", "failed", resultText: "boom");
        var notification = FakeAcpAgent.BuildSessionUpdateNotification(FakeAcpAgent.FixedSessionId, statusUpdate);
        var result = System.Text.Json.JsonDocument.Parse("""{"stopReason":"end_turn"}""").RootElement.Clone();
        h.Fake.EnqueuePromptScript(new[] { notification }, result);

        h.StartFakeAgentLoop();
        await h.Runtime.StartAsync("/abs/worktree", "prompt", h.Cts.Token).WaitAsync(HangGuard);

        var received = await h.Runtime.Updates.ReadAsync().AsTask().WaitAsync(HangGuard);

        await Assert.That(received.ToolStatus).IsEqualTo("failed");
        await Assert.That(received.ToolResultText).IsEqualTo("boom");
        await Assert.That(received.ToolIsError).IsTrue();
    }

    [Test]
    public async Task Terminal_ToolCallUpdate_falls_back_to_rawOutput_when_no_content_text_block() {
        await using var h = new Harness();

        var statusUpdate = FakeAcpAgent.BuildToolCallStatusUpdate(
            "call-1", "completed", rawOutputJson: """{"exitCode":0}""");
        var notification = FakeAcpAgent.BuildSessionUpdateNotification(FakeAcpAgent.FixedSessionId, statusUpdate);
        var result = System.Text.Json.JsonDocument.Parse("""{"stopReason":"end_turn"}""").RootElement.Clone();
        h.Fake.EnqueuePromptScript(new[] { notification }, result);

        h.StartFakeAgentLoop();
        await h.Runtime.StartAsync("/abs/worktree", "prompt", h.Cts.Token).WaitAsync(HangGuard);

        var received = await h.Runtime.Updates.ReadAsync().AsTask().WaitAsync(HangGuard);

        await Assert.That(received.ToolResultText).IsEqualTo("""{"exitCode":0}""");
    }

    [Test]
    public async Task SendUserInputAsync_after_start_sends_another_session_prompt() {
        await using var h = new Harness();
        h.StartFakeAgentLoop();

        await h.Runtime.StartAsync("/abs/worktree", "", h.Cts.Token).WaitAsync(HangGuard);

        // Fix E: SendUserInputAsync fires the session/prompt as untracked background work
        // and returns as soon as it's queued, NOT once the fake has received/answered it — so poll
        // for the call to land instead of asserting immediately after the await returns.
        await h.Runtime.SendUserInputAsync("more").WaitAsync(HangGuard);

        var deadline = DateTime.UtcNow + HangGuard;
        while (!h.Fake.ReceivedCalls.Any(c => c.Method == "session/prompt") && DateTime.UtcNow < deadline)
            await Task.Delay(10);

        var promptCalls = h.Fake.ReceivedCalls.Where(c => c.Method == "session/prompt").ToArray();
        await Assert.That(promptCalls.Length).IsEqualTo(1);
        await Assert.That(promptCalls[0].Params!.Value.GetProperty("prompt")[0].GetProperty("text").GetString())
            .IsEqualTo("more");
    }

    [Test]
    public async Task RequestGracefulStopAsync_sends_session_cancel_notification() {
        await using var h = new Harness();
        h.StartFakeAgentLoop();

        await h.Runtime.StartAsync("/abs/worktree", "", h.Cts.Token).WaitAsync(HangGuard);

        await h.Runtime.RequestGracefulStopAsync().WaitAsync(HangGuard);

        // session/cancel is a notification (no id) — give the fake's read loop a moment to record it.
        var deadline = DateTime.UtcNow + HangGuard;
        while (!h.Fake.ReceivedCalls.Any(c => c.Method == "session/cancel") && DateTime.UtcNow < deadline)
            await Task.Delay(10);

        var cancelCall = h.Fake.ReceivedCalls.SingleOrDefault(c => c.Method == "session/cancel");
        await Assert.That(cancelCall.Method).IsEqualTo("session/cancel");
        await Assert.That(cancelCall.Params!.Value.GetProperty("sessionId").GetString()).IsEqualTo(FakeAcpAgent.FixedSessionId);
    }

    [Test]
    public async Task SendRawInputAsync_throws_NotSupportedException_and_ReadOutputAsync_yields_nothing() {
        await using var h = new Harness();
        h.StartFakeAgentLoop();

        await Assert.ThrowsAsync<NotSupportedException>(() => h.Runtime.SendRawInputAsync(new byte[] { 1 }));

        // ReadOutputAsync never yields a byte, but (Fix E) it also must not complete on its
        // own — it stays open until the process exits or ct cancels (see the dedicated
        // ReadOutputAsync_* tests below for that contract). Cancel to end the enumeration here,
        // since this test's focus is "yields nothing", not "when does it end".
        using var readCts = CancellationTokenSource.CreateLinkedTokenSource(h.Cts.Token);

        var any = false;
        var readTask = Task.Run(async () => {
            await foreach (var _ in h.Runtime.ReadOutputAsync(readCts.Token))
                any = true;
        });

        await Task.Delay(50); // give the loop a moment to (not) yield anything
        await readCts.CancelAsync();
        await readTask.WaitAsync(HangGuard);

        await Assert.That(any).IsFalse();
    }

    // ── Fix E: ReadOutputAsync must stay open (not complete immediately) ──────────

    [Test]
    public async Task ReadOutputAsync_stays_open_until_the_process_exits() {
        await using var h = new Harness();
        h.StartFakeAgentLoop();

        var completed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var readTask = Task.Run(async () => {
            await foreach (var _ in h.Runtime.ReadOutputAsync(h.Cts.Token)) {
                // never yields
            }
            completed.TrySetResult();
        });

        // Give the enumerator a chance to run — it must NOT have completed yet (the old
        // implementation returned/yield-broke immediately, which is exactly the bug: the
        // orchestrator's read loop would see the output stream "end" for a still-live agent and
        // finalize it as Failed).
        await Task.Delay(200);
        await Assert.That(completed.Task.IsCompleted).IsFalse();

        h.Process.SignalExited();

        await completed.Task.WaitAsync(HangGuard);
        await readTask.WaitAsync(HangGuard);
    }

    [Test]
    public async Task ReadOutputAsync_stays_open_until_the_cancellation_token_fires() {
        await using var h = new Harness();
        h.StartFakeAgentLoop();

        using var readCts = new CancellationTokenSource();
        var        completed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var readTask = Task.Run(async () => {
            await foreach (var _ in h.Runtime.ReadOutputAsync(readCts.Token)) {
                // never yields
            }
            completed.TrySetResult();
        });

        await Task.Delay(200);
        await Assert.That(completed.Task.IsCompleted).IsFalse();

        await readCts.CancelAsync();

        await completed.Task.WaitAsync(HangGuard);
        await readTask.WaitAsync(HangGuard);
    }

    // ── Fix E: StartAsync/SendUserInputAsync must not block on turn completion ───────

    [Test]
    public async Task StartAsync_does_not_await_the_initial_prompt_turn_to_completion() {
        await using var h = new Harness();

        // The fake answers initialize/session-new immediately but holds EVERY session/prompt
        // response indefinitely — models a long-running first turn. Before the fix,
        // AcpHostedAgentRuntime.StartAsync awaited SendPromptAsync (the initial prompt) to
        // completion, so this would hang past HangGuard; the fix fires it as untracked background
        // work and returns once session/new resolves.
        h.Fake.HoldPromptResponses = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        h.StartFakeAgentLoop();

        await h.Runtime.StartAsync("/abs/worktree", "do the thing", h.Cts.Token).WaitAsync(HangGuard);

        // Prove the prompt really was sent (as background work) even though we never released the
        // hold — the fake recorded the call as soon as it arrived, before answering.
        var deadline = DateTime.UtcNow + HangGuard;
        while (!h.Fake.ReceivedCalls.Any(c => c.Method == "session/prompt") && DateTime.UtcNow < deadline)
            await Task.Delay(10);

        await Assert.That(h.Fake.ReceivedCalls.Any(c => c.Method == "session/prompt")).IsTrue();

        // Release the held response so the harness can tear down cleanly.
        h.Fake.HoldPromptResponses.TrySetResult();
    }

    [Test]
    public async Task SendUserInputAsync_returns_promptly_without_waiting_for_the_prompt_turn_to_complete() {
        await using var h = new Harness();
        h.StartFakeAgentLoop();

        // Establish the session with no initial prompt (StartAsync's own prompt firing is covered
        // by the test above), then hold every subsequent session/prompt response.
        await h.Runtime.StartAsync("/abs/worktree", "", h.Cts.Token).WaitAsync(HangGuard);

        h.Fake.HoldPromptResponses = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        // Before the fix, SendUserInputAsync awaited SendPromptAsync (the session/prompt round
        // trip) to completion — with the response held indefinitely, this call would hang past
        // HangGuard. The fix fires the prompt as untracked background work and returns immediately.
        await h.Runtime.SendUserInputAsync("more").WaitAsync(HangGuard);

        var deadline = DateTime.UtcNow + HangGuard;
        while (h.Fake.ReceivedCalls.Count(c => c.Method == "session/prompt") < 1 && DateTime.UtcNow < deadline)
            await Task.Delay(10);

        await Assert.That(h.Fake.ReceivedCalls.Any(c => c.Method == "session/prompt")).IsTrue();

        h.Fake.HoldPromptResponses.TrySetResult();
    }

    // ── Test plan item 9: cancellation propagates out of StartAsync ───────────────────

    /// <summary>
    /// A <c>ct</c> canceled WHILE <c>session/set_config_option</c> is in flight must abort
    /// <c>StartAsync</c> via a propagated <see cref="OperationCanceledException"/> — the runtime
    /// must never hand a live runtime back to a caller who already canceled the launch (spec-review
    /// Finding 2). Contrasted against <see cref="AcpHostedAgentRuntimeModelSelectionTests.StartAsync_JsonRpcErrorFromSetConfigOption_IsNonFatal_PromptStillFires"/>,
    /// which shares almost the same setup but asserts the opposite outcome for an RPC ERROR
    /// response (as opposed to cancellation).
    /// </summary>
    [Test]
    public async Task StartAsync_CanceledWhileSetConfigOptionInFlight_ThrowsOperationCanceled() {
        await using var h = new Harness();
        h.Fake.SetSessionNewResult(FakeAcpAgent.BuildSessionNewResult(
            FakeAcpAgent.FixedSessionId,
            currentModelId: "composer-2.5[fast=true]",
            availableModels: [("claude-sonnet-4-5[thinking=true,context=200k]", "claude-sonnet-4-5")]));
        h.Fake.HoldSetConfigOptionResponse = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        h.StartFakeAgentLoop();

        using var innerCts = CancellationTokenSource.CreateLinkedTokenSource(h.Cts.Token);

        var startTask = h.Runtime.StartAsync(
            "/abs/worktree", "do the thing", innerCts.Token,
            requestedModel: "claude-sonnet-4-5"
        );

        // Wait for session/set_config_option to actually be in flight before cancelling.
        var deadline = DateTime.UtcNow + HangGuard;
        while (!h.Fake.ReceivedCalls.Any(c => c.Method == "session/set_config_option") && DateTime.UtcNow < deadline)
            await Task.Delay(10);
        await Assert.That(h.Fake.ReceivedCalls.Any(c => c.Method == "session/set_config_option")).IsTrue();

        await innerCts.CancelAsync();

        await Assert.That(async () => await startTask.WaitAsync(HangGuard)).Throws<OperationCanceledException>();

        // Release the fake's held response so the harness can tear down cleanly.
        h.Fake.HoldSetConfigOptionResponse.TrySetResult();
    }

    // ── TerminationVerdict fused to the reap claim ─────────────────────────────────
    // TryStartReap/TakeReap/FirstTurnSettledForTest are exercised directly (internal) rather than
    // through HandleMcpSurfaceViolation/HandleUnexpectedUnattendedInteraction, so these tests can
    // control claim/window timing deterministically instead of racing real ACP connection I/O.

    /// <summary>A starter's synchronous prefix can throw (production: _cts.Cancel() racing a
    /// disposed _cts). No verdict must survive that — and the claim must reopen for the next
    /// caller, which then wins both the claim and the verdict.</summary>
    [Test]
    public async Task Verdict_published_only_on_successful_claim() {
        await using var h = new Harness();

        await Assert.That(() => h.Runtime.TryStartReap(
                "first-reason", () => throw new InvalidOperationException("cancel failed")))
            .Throws<InvalidOperationException>();

        await Assert.That(h.Runtime.Verdict).IsNull();

        var claimed = h.Runtime.TryStartReap("second-reason", () => Task.CompletedTask);

        await Assert.That(claimed).IsTrue();
        await Assert.That(h.Runtime.Verdict).IsNotNull();
        await Assert.That(h.Runtime.Verdict!.Reason).IsEqualTo("second-reason");
    }

    /// <summary>The window bit must be read BEFORE the starter runs. Here the starter itself
    /// settles the marker synchronously (standing in for production's _cts.Cancel() eventually
    /// settling it via ProcessAdmittedTurnAsync's finally) — a snapshot taken anywhere other than
    /// strictly before the starter runs would observe "settled" and misclassify.</summary>
    [Test]
    public async Task Window_bit_snapshotted_before_cancel() {
        await using var h = new Harness();
        h.StartFakeAgentLoop();

        // Hold the prompt response so the first turn is genuinely still in flight when the reap is
        // claimed below — nothing has settled the marker yet.
        h.Fake.HoldPromptResponses = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        await h.Runtime.StartAsync("/abs/worktree", "review this", h.Cts.Token).WaitAsync(HangGuard);

        var deadline = DateTime.UtcNow + HangGuard;
        while (!h.Fake.ReceivedCalls.Any(c => c.Method == "session/prompt") && DateTime.UtcNow < deadline)
            await Task.Delay(10);
        await Assert.That(h.Fake.ReceivedCalls.Any(c => c.Method == "session/prompt")).IsTrue();
        await Assert.That(h.Runtime.FirstTurnSettledForTest.Task.IsCompleted).IsFalse();

        var claimed = h.Runtime.TryStartReap("reap-reason", () => {
            h.Runtime.FirstTurnSettledForTest.TrySetResult();
            return Task.CompletedTask;
        });

        await Assert.That(claimed).IsTrue();
        await Assert.That(h.Runtime.Verdict!.ReapedInsideLaunchWindow).IsTrue();

        h.Fake.HoldPromptResponses.TrySetResult();
    }

    /// <summary>Barrier contention: the winner is held inside its synchronous starter, so the claim
    /// + window snapshot + verdict publish are all still inside _reapLock. A concurrent TakeReap()
    /// (disposal's seam) and a concurrent second TryStartReap() call (the loser) must both block on
    /// that same lock — TakeReap can never observe a claim with no published verdict/task, and the
    /// loser can neither publish nor overwrite once it does get in.</summary>
    [Test]
    public async Task Concurrent_loser_cannot_publish_or_overwrite() {
        await using var h = new Harness();

        var winnerEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseWinner = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var winnerTask = Task.Run(() => h.Runtime.TryStartReap("winner-reason", () => {
            winnerEntered.TrySetResult();
            releaseWinner.Task.GetAwaiter().GetResult(); // synchronous block INSIDE _reapLock
            return Task.CompletedTask;
        }));

        await winnerEntered.Task.WaitAsync(HangGuard);

        // Explicit type argument: Task.Run would otherwise resolve TakeReap's Task? return via the
        // Func<Task> "unwrap" overload (treating it as the work to await) instead of Func<Task?>
        // (treating it as the RESULT), silently changing what takeTask represents.
        var takeTask  = Task.Run<Task?>(() => h.Runtime.TakeReap());
        var loserTask = Task.Run(() => h.Runtime.TryStartReap("loser-reason", () => Task.CompletedTask));

        // Neither concurrent caller can have progressed past the lock yet.
        await Task.Delay(200);
        await Assert.That(takeTask.IsCompleted).IsFalse();
        await Assert.That(loserTask.IsCompleted).IsFalse();

        releaseWinner.TrySetResult();

        await Assert.That(await winnerTask.WaitAsync(HangGuard)).IsTrue();
        await Assert.That(await loserTask.WaitAsync(HangGuard)).IsFalse();

        // A plain bool, not the Task value itself: TUnit's Assert.That special-cases a Task-typed
        // value as something to further await, which collides with checking whether the reference
        // came back null.
        Task? takenReap = await takeTask.WaitAsync(HangGuard);
        await Assert.That(takenReap is not null).IsTrue();

        await Assert.That(h.Runtime.Verdict).IsNotNull();
        await Assert.That(h.Runtime.Verdict!.Reason).IsEqualTo("winner-reason");
    }

    /// <summary>Finding 2: DisposeAsync latches _disposed BEFORE its early cancellation phase, and
    /// only reaches the reap wait + connection/process disposal much later. If a cancellation callback
    /// on _cts throws (cancellation callbacks can throw), the early phase must NOT abort teardown —
    /// the child/streams would leak with retry impossible (the latch is set). This registers a
    /// throwing callback so _cts.CancelAsync() faults, then asserts the child is STILL disposed and
    /// the coded verdict still surfaces.</summary>
    [Test]
    public async Task DisposeAsync_early_cancellation_fault_still_disposes_child_and_keeps_verdict() {
        await using var h = new Harness();

        // Faults DisposeAsync's early phase at `await _cts.CancelAsync()`. The no-op reap starter below
        // does NOT cancel _cts, so this callback fires only during DisposeAsync.
        h.Runtime.RuntimeShutdownTokenForTest.Register(() => throw new InvalidOperationException("early-cancel-fault"));

        h.Runtime.TryStartReap("kiro_reviewer_mcp_surface_unexpected: violation", () => Task.CompletedTask);
        await Assert.That(h.Runtime.ReadVerdict()).IsNotNull(); // precondition: a verdict exists to preserve

        // Pre-fix: the early fault aborts DisposeAsync before the guaranteed teardown, so the child is
        // never disposed (DisposeCalls == 0). Post-fix the early phase is contained and teardown runs.
        try { await h.Runtime.DisposeAsync(); } catch { /* pre-fix: the early fault propagates */ }

        await Assert.That(h.Process.DisposeCalls).IsGreaterThan(0);  // child disposed despite the fault
        await Assert.That(h.Runtime.ReadVerdict()).IsNotNull();      // coded verdict still surfaces
    }

    /// <summary>Finding 2 (round 3): the EARLY owner-cancel (`_ownerCts.Cancel()`) can throw too, and
    /// it ran BEFORE the incarnation was captured under the lock and BEFORE the terminal/gate signals.
    /// If a reconnect commits a SUCCESSOR incarnation in the window before the lock, a throwing owner
    /// cancel left the STALE predecessor selected (successor leaked) and skipped the signals that
    /// unpark parked waiters. Commits a successor right before the lock, faults the owner cancel, and
    /// asserts the LIVE successor is disposed (not the predecessor) and the terminal signal fired.</summary>
    [Test]
    public async Task DisposeAsync_owner_cancel_fault_disposes_the_successor_and_fires_terminal_signal() {
        await using var h = new Harness();

        // A throwing owner CTS — the EARLY cancellation callback, distinct from the _cts.CancelAsync()
        // the previous test faults.
        var ownerCts = new CancellationTokenSource();
        ownerCts.Token.Register(() => throw new InvalidOperationException("owner-cancel-fault"));
        h.Runtime.SetOwnerCtsForTest(ownerCts);

        h.Runtime.TryStartReap("kiro_reviewer_mcp_surface_unexpected: violation", () => Task.CompletedTask);

        // Commit a reconnect SUCCESSOR right before DisposeAsync takes _reconnectLock — the window the
        // round-2 code read `installed` before, so a stale-incarnation dispose would leak this one.
        var successorProcess = new FakeAcpProcess();
        h.Runtime.BeforeReconnectLockOnDisposeForTest =
            () => h.Runtime.CommitSuccessorIncarnationForTest(successorProcess);

        try { await h.Runtime.DisposeAsync(); } catch { /* pre-fix: the owner-cancel fault propagates */ }

        await Assert.That(successorProcess.DisposeCalls).IsGreaterThan(0);        // the LIVE successor disposed
        await Assert.That(h.Process.DisposeCalls).IsEqualTo(0);                   // the stale predecessor NOT disposed
        await Assert.That(h.Runtime.RuntimeTerminalForTest.IsCompleted).IsTrue(); // terminal signal fired despite the fault
    }

    /// <summary>Bug 1: DisposeAsync's contained early phase must still SIGNAL the turn worker to exit
    /// (cancel _cts AND complete _pendingTurns — the two things RunTurnWorkerAsync exits on) even when
    /// `ownerCts.Cancel()` throws. Otherwise the worker parks forever on _pendingTurns.WaitToReadAsync
    /// while teardown disposes its connection/process out from under it. Establishes a parked worker,
    /// faults the owner cancel, and asserts the worker completed (not parked).</summary>
    [Test]
    public async Task DisposeAsync_owner_cancel_fault_still_unblocks_the_turn_worker() {
        await using var h = new Harness();
        h.StartFakeAgentLoop();

        // No initial prompt → the single turn worker parks on _pendingTurns, still running.
        await h.Runtime.StartAsync("/abs/worktree", initialPrompt: null, h.Cts.Token).WaitAsync(HangGuard);
        await Assert.That(h.Runtime.TurnWorkerTaskForTest.IsCompleted).IsFalse(); // precondition: parked

        var ownerCts = new CancellationTokenSource();
        ownerCts.Token.Register(() => throw new InvalidOperationException("owner-cancel-fault"));
        h.Runtime.SetOwnerCtsForTest(ownerCts);

        try { await h.Runtime.DisposeAsync(); } catch { /* the fault is contained either way */ }

        // Pre-fix: the owner-cancel throw skips _cts.CancelAsync() + both channel completions, so the
        // worker stays parked (IsCompleted == false). Post-fix each cancel is independently guarded, so
        // the worker is signaled and DisposeAsync's bounded wait sees it exit.
        await Assert.That(h.Runtime.TurnWorkerTaskForTest.IsCompleted).IsTrue();
    }

    /// <summary>Reviewer launches always carry a prompt, but the window still needs a defined close
    /// for a launch that doesn't — otherwise no turn ever runs, ProcessAdmittedTurnAsync's finally
    /// never fires, and the marker (so the window) would stay open forever.</summary>
    [Test]
    public async Task Empty_initial_prompt_closes_window_at_handoff() {
        await using var h = new Harness();
        h.StartFakeAgentLoop();

        await h.Runtime.StartAsync("/abs/worktree", "", h.Cts.Token).WaitAsync(HangGuard);

        var claimed = h.Runtime.TryStartReap("reason", () => Task.CompletedTask);

        await Assert.That(claimed).IsTrue();
        await Assert.That(h.Runtime.Verdict!.ReapedInsideLaunchWindow).IsFalse();
    }

    /// <summary>Reap reasons can embed agent-controlled text (e.g. a JSON-RPC method from an inbound
    /// frame), which may contain non-BMP characters — a raw UTF-16 code-unit slice at the cap can
    /// land between a surrogate pair's two halves, leaving a lone surrogate in the forwarded string
    /// (design spec §3.1). Nine filler chars put "😀" (U+1F600, a high+low surrogate pair) exactly
    /// astride the cut: its high half at index maxLength-1, low half at index maxLength — precisely
    /// the boundary a naive <c>oneLine[..maxLength]</c> would slice through.</summary>
    [Test]
    public async Task Sanitize_never_splits_surrogate_at_boundary() {
        const int maxLength = 10;
        var oneLine = new string('a', maxLength - 1) + char.ConvertFromUtf32(0x1F600) + "-well-past-the-cap";

        // Precondition: the pair really does straddle the cut this test means to exercise.
        await Assert.That(char.IsHighSurrogate(oneLine[maxLength - 1])).IsTrue();
        await Assert.That(char.IsLowSurrogate(oneLine[maxLength])).IsTrue();

        var result = AcpHostedAgentRuntime.SanitizeForForward(oneLine, maxLength);

        await Assert.That(result.All(c => !char.IsHighSurrogate(c) && !char.IsLowSurrogate(c))).IsTrue();
        await Assert.That(result.Length).IsLessThanOrEqualTo(maxLength + 1); // +1 for the "…" suffix
        await Assert.That(result).EndsWith("…");
    }

    /// <summary>Line breaks collapse to spaces (<c>ReplaceLineEndings</c>) before the cap is ever
    /// applied, so a coded reason's prefix — e.g. the <c>unattended_interaction_forbidden:{method}</c>
    /// shape writers use (design spec §3.1) — survives at the front and no raw line-break character
    /// remains anywhere in the result.</summary>
    [Test]
    public async Task Sanitize_collapses_multiline_input_and_keeps_the_code_prefix() {
        const string prefix = "unattended_interaction_forbidden:tools/call";
        var input = $"{prefix}\nsecond line\r\nthird line";

        var result = AcpHostedAgentRuntime.SanitizeForForward(input);

        await Assert.That(result).StartsWith(prefix);
        await Assert.That(result).DoesNotContain("\n");
        await Assert.That(result).DoesNotContain("\r");
    }

    /// <summary>Truncation only ever trims the TAIL, so an oversized single-line reason keeps its
    /// leading coded prefix intact — the reason a caller can rely on the prefix for classification
    /// even when the full message got capped.</summary>
    [Test]
    public async Task Sanitize_of_an_oversized_reason_keeps_the_leading_coded_prefix() {
        const string prefix = "unattended_interaction_forbidden:tools/call";
        var input = prefix + new string('x', 600); // well past the 500-char default cap

        var result = AcpHostedAgentRuntime.SanitizeForForward(input);

        await Assert.That(result).StartsWith(prefix);
        await Assert.That(result).EndsWith("…");
        await Assert.That(result.Length).IsLessThanOrEqualTo(501); // default maxLength(500) + "…"
    }

    /// <summary>The back-off reads <c>oneLine[maxLength - 1]</c> — guards against a non-positive cap
    /// underflowing that index instead of just falling through to the pre-fix (already-safe)
    /// substring behavior.</summary>
    [Test]
    public async Task Sanitize_with_a_non_positive_cap_does_not_throw() {
        var result = AcpHostedAgentRuntime.SanitizeForForward("anything at all", maxLength: 0);

        await Assert.That(result).IsEqualTo("…");
    }
}
