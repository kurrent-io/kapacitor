// test/Capacitor.Cli.Tests.Unit/Acp/AcpTranscriptAggregationTests.cs
using System.Text.Json;
using Capacitor.Cli.Core;
using Capacitor.Cli.Daemon.Acp;
using Capacitor.Cli.Daemon.Services;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Capacitor.Cli.Tests.Unit.Acp;

/// <summary>
/// Option B task 2: exercises <see cref="AcpHostedAgentRuntime"/>'s chunk aggregation +
/// serialized single-flight prompt-turn worker end-to-end against <see cref="FakeAcpAgent"/>, via the
/// ordered <see cref="AcpHostedAgentRuntime.Envelopes"/> transcript (§2.1 of
/// <c>docs/ai688-option-b-canonical-surfacing-design.md</c>). Mirrors the
/// <c>AcpHostedAgentRuntimeTests</c> harness pattern; no real <c>cursor-agent acp</c> process.
/// </summary>
public class AcpTranscriptAggregationTests {
    static readonly TimeSpan HangGuard = TimeSpan.FromSeconds(5);

    static readonly JsonElement EndTurnResult = JsonDocument.Parse("""{"stopReason":"end_turn"}""").RootElement.Clone();

    /// <summary>Minimal <see cref="IAcpProcess"/> stand-in — these tests never exercise process exit/terminate.</summary>
    sealed class FakeAcpProcess : IAcpProcess {
        public int  Pid       { get; init; } = 4242;
        public bool HasExited { get; private set; }
        public int? ExitCode  { get; private set; }

        public Task WaitForExitAsync(TimeSpan? timeout = null) =>
            timeout is { } t ? Task.Delay(t) : Task.Delay(Timeout.InfiniteTimeSpan);

        public Task TerminateAsync(TimeSpan? timeout = null) {
            HasExited = true;
            ExitCode  = 0;
            return Task.CompletedTask;
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    sealed class Harness : IAsyncDisposable {
        public FakeAcpAgent          Fake    { get; }
        public AcpConnection         Conn    { get; }
        public FakeAcpProcess        Process { get; }
        public AcpHostedAgentRuntime Runtime { get; }
        public CancellationTokenSource Cts   { get; } = new();

        Task _fakeRunTask = Task.CompletedTask;

        /// <summary> reliability fix (Qodo #6): <paramref name="logger"/>/<paramref name="transcriptCapacity"/>
        /// let the bounded-channel drop test inject a capturing logger and a tiny cap without every
        /// other <c>Harness()</c> call site having to care.</summary>
        public Harness(ILogger? logger = null, int? transcriptCapacity = null) {
            Fake    = new FakeAcpAgent();
            Conn    = new AcpConnection(Fake.ClientWriteStream, Fake.ClientReadStream, NullLogger.Instance);
            Process = new FakeAcpProcess();
            Runtime = new AcpHostedAgentRuntime(Conn, Process, logger ?? NullLogger.Instance, transcriptCapacity: transcriptCapacity);
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

    static async Task WaitForPromptCallCountAsync(FakeAcpAgent fake, int minCount) {
        var deadline = DateTime.UtcNow + HangGuard;
        while (fake.ReceivedCalls.Count(c => c.Method == "session/prompt") < minCount && DateTime.UtcNow < deadline)
            await Task.Delay(10);
    }

    static async Task<AcpEventEnvelope> ReadEnvelopeAsync(AcpHostedAgentRuntime runtime) =>
        await runtime.Envelopes.ReadAsync().AsTask().WaitAsync(HangGuard);

    // ── Aggregation ──────────────────────────────────────────────────────────────────────────────

    [Test]
    public async Task N_agent_message_chunks_in_one_turn_coalesce_into_one_AssistantText_envelope() {
        await using var h = new Harness();

        var chunk1 = FakeAcpAgent.BuildSessionUpdateNotification(FakeAcpAgent.FixedSessionId, FakeAcpAgent.BuildAgentMessageChunkUpdate("Hello "));
        var chunk2 = FakeAcpAgent.BuildSessionUpdateNotification(FakeAcpAgent.FixedSessionId, FakeAcpAgent.BuildAgentMessageChunkUpdate("wor"));
        var chunk3 = FakeAcpAgent.BuildSessionUpdateNotification(FakeAcpAgent.FixedSessionId, FakeAcpAgent.BuildAgentMessageChunkUpdate("ld"));
        h.Fake.EnqueuePromptScript(new[] { chunk1, chunk2, chunk3 }, EndTurnResult);

        h.StartFakeAgentLoop();
        await h.Runtime.StartAsync("/abs/worktree", "hi", h.Cts.Token).WaitAsync(HangGuard);

        var userMessage = await ReadEnvelopeAsync(h.Runtime);
        await Assert.That(userMessage.Kind).IsEqualTo(AcpEventKind.UserMessage);
        await Assert.That(userMessage.Text).IsEqualTo("hi");

        // The only flush that can have produced this envelope is the TURN-END flush (stopReason) —
        // there is no kind transition anywhere in this turn's script.
        var assistantText = await ReadEnvelopeAsync(h.Runtime);
        await Assert.That(assistantText.Kind).IsEqualTo(AcpEventKind.AssistantText);
        await Assert.That(assistantText.Text).IsEqualTo("Hello world");
        await Assert.That(assistantText.Seq).IsEqualTo(0); // task 3 assigns the real seq, not this task
    }

    [Test]
    public async Task N_agent_thought_chunks_in_one_turn_coalesce_into_one_AssistantThinking_envelope() {
        await using var h = new Harness();

        var chunk1 = FakeAcpAgent.BuildSessionUpdateNotification(FakeAcpAgent.FixedSessionId, FakeAcpAgent.BuildAgentThoughtChunkUpdate("thinking "));
        var chunk2 = FakeAcpAgent.BuildSessionUpdateNotification(FakeAcpAgent.FixedSessionId, FakeAcpAgent.BuildAgentThoughtChunkUpdate("it over"));
        h.Fake.EnqueuePromptScript(new[] { chunk1, chunk2 }, EndTurnResult);

        h.StartFakeAgentLoop();
        await h.Runtime.StartAsync("/abs/worktree", "hi", h.Cts.Token).WaitAsync(HangGuard);

        await ReadEnvelopeAsync(h.Runtime); // UserMessage

        var assistantThinking = await ReadEnvelopeAsync(h.Runtime);
        await Assert.That(assistantThinking.Kind).IsEqualTo(AcpEventKind.AssistantThinking);
        await Assert.That(assistantThinking.Text).IsEqualTo("thinking it over");
    }

    [Test]
    public async Task A_message_run_followed_by_a_thought_chunk_flushes_two_separate_envelopes() {
        await using var h = new Harness();

        var messageChunk = FakeAcpAgent.BuildSessionUpdateNotification(FakeAcpAgent.FixedSessionId, FakeAcpAgent.BuildAgentMessageChunkUpdate("here is my answer"));
        var thoughtChunk = FakeAcpAgent.BuildSessionUpdateNotification(FakeAcpAgent.FixedSessionId, FakeAcpAgent.BuildAgentThoughtChunkUpdate("actually let me reconsider"));
        h.Fake.EnqueuePromptScript(new[] { messageChunk, thoughtChunk }, EndTurnResult);

        h.StartFakeAgentLoop();
        await h.Runtime.StartAsync("/abs/worktree", "hi", h.Cts.Token).WaitAsync(HangGuard);

        await ReadEnvelopeAsync(h.Runtime); // UserMessage

        // Kind-transition flush: the message run must close out BEFORE the thought chunk starts a new
        // run, producing two independent envelopes rather than one merged/corrupted run.
        var assistantText = await ReadEnvelopeAsync(h.Runtime);
        await Assert.That(assistantText.Kind).IsEqualTo(AcpEventKind.AssistantText);
        await Assert.That(assistantText.Text).IsEqualTo("here is my answer");

        // Turn-end flush closes out the thought run.
        var assistantThinking = await ReadEnvelopeAsync(h.Runtime);
        await Assert.That(assistantThinking.Kind).IsEqualTo(AcpEventKind.AssistantThinking);
        await Assert.That(assistantThinking.Text).IsEqualTo("actually let me reconsider");
    }

    [Test]
    public async Task A_tool_call_mid_run_flushes_the_open_message_run_then_emits_the_ToolCall_envelope_in_order() {
        await using var h = new Harness();

        var messageChunk = FakeAcpAgent.BuildSessionUpdateNotification(FakeAcpAgent.FixedSessionId, FakeAcpAgent.BuildAgentMessageChunkUpdate("checking..."));
        var toolCall      = FakeAcpAgent.BuildSessionUpdateNotification(
            FakeAcpAgent.FixedSessionId,
            FakeAcpAgent.BuildToolCallUpdate("call-1", "Run shell command", "execute", "pending", rawInputJson: """{"command":"echo hi"}"""));
        var trailingChunk = FakeAcpAgent.BuildSessionUpdateNotification(FakeAcpAgent.FixedSessionId, FakeAcpAgent.BuildAgentMessageChunkUpdate("done"));
        h.Fake.EnqueuePromptScript(new[] { messageChunk, toolCall, trailingChunk }, EndTurnResult);

        h.StartFakeAgentLoop();
        await h.Runtime.StartAsync("/abs/worktree", "run it", h.Cts.Token).WaitAsync(HangGuard);

        await ReadEnvelopeAsync(h.Runtime); // UserMessage

        var flushedMessage = await ReadEnvelopeAsync(h.Runtime);
        await Assert.That(flushedMessage.Kind).IsEqualTo(AcpEventKind.AssistantText);
        await Assert.That(flushedMessage.Text).IsEqualTo("checking...");

        var toolCallEnvelope = await ReadEnvelopeAsync(h.Runtime);
        await Assert.That(toolCallEnvelope.Kind).IsEqualTo(AcpEventKind.ToolCall);
        await Assert.That(toolCallEnvelope.ToolCallId).IsEqualTo("call-1");
        await Assert.That(toolCallEnvelope.ToolName).IsEqualTo("Run shell command");
        await Assert.That(toolCallEnvelope.ToolInputJson).IsEqualTo("""{"command":"echo hi"}""");

        // A fresh run started after the tool call, flushed by the turn-end (stopReason).
        var finalMessage = await ReadEnvelopeAsync(h.Runtime);
        await Assert.That(finalMessage.Kind).IsEqualTo(AcpEventKind.AssistantText);
        await Assert.That(finalMessage.Text).IsEqualTo("done");
    }

    [Test]
    public async Task A_session_info_update_mid_run_does_not_split_the_message_run() {
        // A native session-title update (session_info_update) interleaved between two message
        // chunks must NOT flush the open run: the title is orderless metadata, not transcript
        // content. It is emitted standalone and the surrounding chunks still coalesce into ONE
        // AssistantText envelope (regression for the aggregation-split bug).
        await using var h = new Harness();

        var chunk1    = FakeAcpAgent.BuildSessionUpdateNotification(FakeAcpAgent.FixedSessionId, FakeAcpAgent.BuildAgentMessageChunkUpdate("Hello "));
        var infoTitle = FakeAcpAgent.BuildSessionUpdateNotification(FakeAcpAgent.FixedSessionId, FakeAcpAgent.BuildSessionInfoUpdate("Shell Reporter"));
        var chunk2    = FakeAcpAgent.BuildSessionUpdateNotification(FakeAcpAgent.FixedSessionId, FakeAcpAgent.BuildAgentMessageChunkUpdate("world"));
        h.Fake.EnqueuePromptScript(new[] { chunk1, infoTitle, chunk2 }, EndTurnResult);

        h.StartFakeAgentLoop();
        await h.Runtime.StartAsync("/abs/worktree", "hi", h.Cts.Token).WaitAsync(HangGuard);

        await ReadEnvelopeAsync(h.Runtime); // UserMessage

        // The title is emitted standalone (it did NOT flush the open run)...
        var title = await ReadEnvelopeAsync(h.Runtime);
        await Assert.That(title.Kind).IsEqualTo(AcpEventKind.SessionTitle);
        await Assert.That(title.Text).IsEqualTo("Shell Reporter");

        // ...and the two message chunks still coalesce into ONE AssistantText run.
        var message = await ReadEnvelopeAsync(h.Runtime);
        await Assert.That(message.Kind).IsEqualTo(AcpEventKind.AssistantText);
        await Assert.That(message.Text).IsEqualTo("Hello world");
    }

    [Test]
    public async Task UserMessage_envelope_is_emitted_once_per_turn_before_that_turns_assistant_envelopes() {
        await using var h = new Harness();

        var turn1Chunk = FakeAcpAgent.BuildSessionUpdateNotification(FakeAcpAgent.FixedSessionId, FakeAcpAgent.BuildAgentMessageChunkUpdate("first reply"));
        h.Fake.EnqueuePromptScript(new[] { turn1Chunk }, EndTurnResult);
        var turn2Chunk = FakeAcpAgent.BuildSessionUpdateNotification(FakeAcpAgent.FixedSessionId, FakeAcpAgent.BuildAgentMessageChunkUpdate("second reply"));
        h.Fake.EnqueuePromptScript(new[] { turn2Chunk }, EndTurnResult);

        h.StartFakeAgentLoop();
        await h.Runtime.StartAsync("/abs/worktree", "first", h.Cts.Token).WaitAsync(HangGuard);

        var e1 = await ReadEnvelopeAsync(h.Runtime);
        var e2 = await ReadEnvelopeAsync(h.Runtime);
        await Assert.That(e1.Kind).IsEqualTo(AcpEventKind.UserMessage);
        await Assert.That(e1.Text).IsEqualTo("first");
        await Assert.That(e2.Kind).IsEqualTo(AcpEventKind.AssistantText);
        await Assert.That(e2.Text).IsEqualTo("first reply");

        await h.Runtime.SendUserInputAsync("second").WaitAsync(HangGuard);

        var e3 = await ReadEnvelopeAsync(h.Runtime);
        var e4 = await ReadEnvelopeAsync(h.Runtime);
        await Assert.That(e3.Kind).IsEqualTo(AcpEventKind.UserMessage);
        await Assert.That(e3.Text).IsEqualTo("second");
        await Assert.That(e4.Kind).IsEqualTo(AcpEventKind.AssistantText);
        await Assert.That(e4.Text).IsEqualTo("second reply");
    }

    // ── Serialization (round-2 finding) ─────────────────────────────────────────────────────────

    [Test]
    public async Task Overlapping_turns_are_serialized_so_each_stopReason_flushes_its_own_turns_buffer() {
        await using var h = new Harness();

        var turn1Chunk = FakeAcpAgent.BuildSessionUpdateNotification(FakeAcpAgent.FixedSessionId, FakeAcpAgent.BuildAgentMessageChunkUpdate("turn one reply"));
        h.Fake.EnqueuePromptScript(new[] { turn1Chunk }, EndTurnResult);
        var turn2Chunk = FakeAcpAgent.BuildSessionUpdateNotification(FakeAcpAgent.FixedSessionId, FakeAcpAgent.BuildAgentMessageChunkUpdate("turn two reply"));
        h.Fake.EnqueuePromptScript(new[] { turn2Chunk }, EndTurnResult);

        // Holds EVERY session/prompt response until released — models turn 1's real ACP round trip
        // still being "in flight" (no stopReason yet) at the moment turn 2 is enqueued.
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        h.Fake.HoldPromptResponses = gate;
        h.StartFakeAgentLoop();

        await h.Runtime.StartAsync("/abs/worktree", "first", h.Cts.Token).WaitAsync(HangGuard);

        // Turn 1's session/prompt has been sent (and its chunk written) — give the chunk a moment to
        // propagate through the in-memory pipe and get aggregated before we act.
        await WaitForPromptCallCountAsync(h.Fake, minCount: 1);
        await Task.Delay(200);

        await h.Runtime.SendUserInputAsync("second").WaitAsync(HangGuard);

        // THE serialization assertion (the round-2 Codex finding this task fixes): turn 2's
        // session/prompt must NOT have been sent yet — the worker is still awaiting turn 1's (held)
        // response. Before task 2, both the initial prompt and SendUserInputAsync fired independent,
        // untracked background turns (FireAndTrackPromptAsync) — under that shape this count would
        // already be 2 here, and turn 2's chunk could reach the aggregator while turn 1's buffer was
        // still open, corrupting both.
        await Task.Delay(200);
        await Assert.That(h.Fake.ReceivedCalls.Count(c => c.Method == "session/prompt")).IsEqualTo(1);

        // Release turn 1's response: the worker flushes turn 1's buffer, then (only then) dequeues
        // turn 2 and sends ITS session/prompt — which the fake also answers immediately since `gate`
        // is a one-shot TaskCompletionSource already completed by the time turn 2's request arrives.
        gate.TrySetResult();
        await WaitForPromptCallCountAsync(h.Fake, minCount: 2);

        var e1 = await ReadEnvelopeAsync(h.Runtime);
        var e2 = await ReadEnvelopeAsync(h.Runtime);
        var e3 = await ReadEnvelopeAsync(h.Runtime);
        var e4 = await ReadEnvelopeAsync(h.Runtime);

        await Assert.That(e1.Kind).IsEqualTo(AcpEventKind.UserMessage);
        await Assert.That(e1.Text).IsEqualTo("first");

        await Assert.That(e2.Kind).IsEqualTo(AcpEventKind.AssistantText);
        await Assert.That(e2.Text).IsEqualTo("turn one reply"); // NOT contaminated with turn 2's text

        await Assert.That(e3.Kind).IsEqualTo(AcpEventKind.UserMessage);
        await Assert.That(e3.Text).IsEqualTo("second");

        await Assert.That(e4.Kind).IsEqualTo(AcpEventKind.AssistantText);
        await Assert.That(e4.Text).IsEqualTo("turn two reply"); // NOT contaminated with turn 1's text
    }

    // ── Cancellation / dispose ───────────────────────────────────────────────────────────────────

    [Test]
    public async Task Dispose_of_a_turn_whose_stopReason_never_arrives_returns_promptly_and_completes_the_transcript() {
        await using var h = new Harness();

        // Explicit empty-updates script — without this, FakeAcpAgent's default script would emit one
        // agent_message_chunk, which the turn's cancellation-triggered courtesy flush would ALSO turn
        // into a buffered AssistantText envelope, complicating this test's sole focus (promptness +
        // completion). The partial-buffer-is-flushed behavior itself is covered by the dedicated test
        // below.
        h.Fake.EnqueuePromptScript(Array.Empty<JsonElement>(), EndTurnResult);
        h.Fake.HoldPromptResponses = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        h.StartFakeAgentLoop();

        await h.Runtime.StartAsync("/abs/worktree", "go", h.Cts.Token).WaitAsync(HangGuard);
        await WaitForPromptCallCountAsync(h.Fake, minCount: 1);

        // The crux of the cancellation contract: a turn stuck awaiting a stopReason
        // that will NEVER arrive (the fake holds the response forever) must not pin DisposeAsync.
        await h.Runtime.DisposeAsync().AsTask().WaitAsync(HangGuard);

        // ChannelReader.Completion only completes once the channel is BOTH writer-completed AND
        // fully drained — drain the turn's UserMessage envelope (the only one buffered; no assistant
        // chunk was ever scripted for this turn, so the cancellation's courtesy flush is a no-op)
        // before asserting completion.
        await ReadEnvelopeAsync(h.Runtime);
        await Assert.That(h.Runtime.Envelopes.Completion.IsCompleted).IsTrue();

        h.Fake.HoldPromptResponses.TrySetResult();
    }

    [Test]
    public async Task Dispose_flushes_the_partial_buffer_of_a_turn_whose_stopReason_never_arrived() {
        await using var h = new Harness();

        var chunk = FakeAcpAgent.BuildSessionUpdateNotification(FakeAcpAgent.FixedSessionId, FakeAcpAgent.BuildAgentMessageChunkUpdate("partial"));
        h.Fake.EnqueuePromptScript(new[] { chunk }, EndTurnResult);
        h.Fake.HoldPromptResponses = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        h.StartFakeAgentLoop();

        await h.Runtime.StartAsync("/abs/worktree", "go", h.Cts.Token).WaitAsync(HangGuard);
        await WaitForPromptCallCountAsync(h.Fake, minCount: 1);
        await Task.Delay(200); // let the chunk propagate through the pipe and get aggregated

        await h.Runtime.DisposeAsync().AsTask().WaitAsync(HangGuard);

        // Design decision (documented on AcpHostedAgentRuntime.DisposeAsync/FlushOpenRun): an open
        // run at session-stop FLUSHES rather than being silently dropped, so a turn cut short by
        // shutdown still contributes whatever partial text it produced.
        var userMessage = await ReadEnvelopeAsync(h.Runtime);
        await Assert.That(userMessage.Kind).IsEqualTo(AcpEventKind.UserMessage);

        var flushed = await ReadEnvelopeAsync(h.Runtime);
        await Assert.That(flushed.Kind).IsEqualTo(AcpEventKind.AssistantText);
        await Assert.That(flushed.Text).IsEqualTo("partial");

        await Assert.That(h.Runtime.Envelopes.Completion.IsCompleted).IsTrue();

        h.Fake.HoldPromptResponses.TrySetResult();
    }

    // ── Bounded transcript channel (PR #301 review, Qodo #6) ────────────────────────────

    /// <summary>Records every log call — mirrors <c>TokenRefreshLoopTests.CaptureLogger</c>'s
    /// established pattern for asserting on a warning without a real logging sink.</summary>
    sealed class CaptureLogger : ILogger {
        public readonly List<(LogLevel Level, string Message)> Entries = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool         IsEnabled(LogLevel logLevel)                            => true;

        public void Log<TState>(LogLevel level, EventId id, TState state, Exception? ex, Func<TState, Exception?, string> formatter)
            => Entries.Add((level, formatter(state, ex)));
    }

    /// <summary>
    /// Qodo #6: an unbounded transcript channel could grow without limit if the forwarder stalls
    /// (outage, blocked send) while the turn keeps producing envelopes. With a small test cap, a
    /// stalled reader (nobody drains <see cref="AcpHostedAgentRuntime.Envelopes"/> here, modelling a
    /// stuck forwarder) must not block/crash the aggregation path — it drops the OLDEST buffered
    /// envelopes (<see cref="System.Threading.Channels.BoundedChannelFullMode.DropOldest"/>) and logs
    /// a warning, keeping only the most recent ones.
    /// </summary>
    [Test]
    public async Task Transcript_channel_at_capacity_drops_the_oldest_envelopes_and_logs_a_warning_instead_of_growing_unbounded() {
        var logger = new CaptureLogger();
        await using var h = new Harness(logger, transcriptCapacity: 3);

        // 5 tool_call notifications, each translated 1:1 (no aggregation) — plus the turn's own
        // UserMessage envelope, this writes 6 envelopes total against a capacity of 3.
        var toolCalls = Enumerable.Range(1, 5)
            .Select(i => FakeAcpAgent.BuildSessionUpdateNotification(
                FakeAcpAgent.FixedSessionId,
                FakeAcpAgent.BuildToolCallUpdate($"call-{i}", $"Tool {i}", "execute", "pending")))
            .ToArray();
        h.Fake.EnqueuePromptScript(toolCalls, EndTurnResult);

        h.StartFakeAgentLoop();
        await h.Runtime.StartAsync("/abs/worktree", "go", h.Cts.Token).WaitAsync(HangGuard);
        await WaitForPromptCallCountAsync(h.Fake, minCount: 1);
        await Task.Delay(300); // let every notification propagate through the pipe and get aggregated
                                // — deliberately NOT reading Envelopes yet, so nothing is drained and
                                // the bounded channel actually has to drop.

        // The runtime kept running (no exception/hang) — draining now must succeed and return
        // exactly `capacity` envelopes: the LAST 3 written (the earlier ones — UserMessage,
        // call-1, call-2 — were dropped to make room).
        var drained = new List<AcpEventEnvelope>();
        while (h.Runtime.Envelopes.TryRead(out var e)) drained.Add(e);

        await Assert.That(drained.Count).IsEqualTo(3);
        await Assert.That(drained.Select(e => e.ToolCallId!)).IsEquivalentTo(new[] { "call-3", "call-4", "call-5" });
        await Assert.That(drained.All(e => e.Kind == AcpEventKind.ToolCall)).IsTrue();

        await Assert.That(logger.Entries).Contains(e => e.Level == LogLevel.Warning && e.Message.Contains("transcript", StringComparison.OrdinalIgnoreCase));

        h.Fake.HoldPromptResponses?.TrySetResult();
    }
}
