// test/Capacitor.Cli.Tests.Unit/Acp/AcpEventTranslatorTests.cs
using Capacitor.Cli.Core;
using Capacitor.Cli.Daemon.Acp;
using Microsoft.Extensions.Logging;

namespace Capacitor.Cli.Tests.Unit.Acp;

/// <summary>
/// Option B task 1: pure unit tests for <see cref="AcpEventTranslator.Translate"/> and its
/// synthesized-lifecycle builders — no ACP wire/process/runtime involved (see
/// <see cref="AcpHostedAgentRuntimeTests"/> for the <see cref="AcpSessionUpdate"/> reduction this
/// translator consumes). Every case is constructed directly against a hand-built
/// <see cref="AcpSessionUpdate"/>, per §2.2 of <c>docs/ai688-option-b-canonical-surfacing-design.md</c>.
/// </summary>
public class AcpEventTranslatorTests {
    const string TimestampIso = "2026-07-08T12:00:00Z";

    /// <summary>Records every log call — mirrors the pattern established in
    /// <c>AcpTranscriptAggregationTests.CaptureLogger</c>.</summary>
    sealed class CaptureLogger : ILogger {
        public readonly List<(LogLevel Level, string Message)> Entries = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool         IsEnabled(LogLevel logLevel)                            => true;

        public void Log<TState>(LogLevel level, EventId id, TState state, Exception? ex, Func<TState, Exception?, string> formatter)
            => Entries.Add((level, formatter(state, ex)));
    }

    [Test]
    public async Task AgentMessageChunk_translates_to_AssistantText_using_the_updates_own_text() {
        var update = new AcpSessionUpdate(AcpUpdateKind.AgentMessageChunk, Text: "hello there");

        var env = AcpEventTranslator.Translate(update, seq: 3, timestampIso: TimestampIso);

        await Assert.That(env).IsNotNull();
        await Assert.That(env!.Value.Kind).IsEqualTo(AcpEventKind.AssistantText);
        await Assert.That(env.Value.Text).IsEqualTo("hello there");
        await Assert.That(env.Value.Seq).IsEqualTo(3L);
        await Assert.That(env.Value.TimestampIso).IsEqualTo(TimestampIso);
        await Assert.That(env.Value.ContractVersion).IsEqualTo(1);
    }

    [Test]
    public async Task AgentMessageChunk_prefers_the_aggregatedText_override_over_the_updates_own_text() {
        var update = new AcpSessionUpdate(AcpUpdateKind.AgentMessageChunk, Text: "chunk 2 only");

        var env = AcpEventTranslator.Translate(update, seq: 1, timestampIso: TimestampIso, aggregatedText: "chunk 1 + chunk 2");

        await Assert.That(env!.Value.Text).IsEqualTo("chunk 1 + chunk 2");
    }

    [Test]
    public async Task AgentThoughtChunk_translates_to_AssistantThinking_with_the_same_text_rule() {
        var update = new AcpSessionUpdate(AcpUpdateKind.AgentThoughtChunk, Text: "thinking...");

        var env = AcpEventTranslator.Translate(update, seq: 2, timestampIso: TimestampIso);

        await Assert.That(env!.Value.Kind).IsEqualTo(AcpEventKind.AssistantThinking);
        await Assert.That(env.Value.Text).IsEqualTo("thinking...");

        var aggregated = AcpEventTranslator.Translate(update, seq: 5, timestampIso: TimestampIso, aggregatedText: "full thought");
        await Assert.That(aggregated!.Value.Text).IsEqualTo("full thought");
    }

    [Test]
    public async Task ToolCall_translates_to_ToolCall_envelope_carrying_id_name_and_ToolInputJson() {
        var update = new AcpSessionUpdate(
            AcpUpdateKind.ToolCall,
            ToolCallId:    "call-1",
            ToolTitle:     "Run shell command",
            ToolKind:      "execute",
            ToolStatus:    "pending",
            ToolInputJson: """{"command":"echo hi"}""");

        var env = AcpEventTranslator.Translate(update, seq: 4, timestampIso: TimestampIso);

        await Assert.That(env).IsNotNull();
        await Assert.That(env!.Value.Kind).IsEqualTo(AcpEventKind.ToolCall);
        await Assert.That(env.Value.ToolCallId).IsEqualTo("call-1");
        await Assert.That(env.Value.ToolName).IsEqualTo("Run shell command");
        await Assert.That(env.Value.ToolInputJson).IsEqualTo("""{"command":"echo hi"}""");
    }

    [Test]
    public async Task ToolCallUpdate_status_only_with_no_result_content_returns_null() {
        // §2.2 footnote 2: a status-only tool_call_update (e.g. pending -> in_progress) must never
        // emit an empty ToolResultReceived.
        var pending = new AcpSessionUpdate(AcpUpdateKind.ToolCallUpdate, ToolCallId: "call-1", ToolStatus: "pending");
        var running = new AcpSessionUpdate(AcpUpdateKind.ToolCallUpdate, ToolCallId: "call-1", ToolStatus: "in_progress");

        await Assert.That(AcpEventTranslator.Translate(pending, 1, TimestampIso)).IsNull();
        await Assert.That(AcpEventTranslator.Translate(running, 2, TimestampIso)).IsNull();
    }

    [Test]
    public async Task ToolCallUpdate_terminal_but_no_extractable_content_returns_null() {
        // Terminal (completed) status alone is not enough — content must be extractable too.
        var update = new AcpSessionUpdate(AcpUpdateKind.ToolCallUpdate, ToolCallId: "call-1", ToolStatus: "completed", ToolResultText: null);

        await Assert.That(AcpEventTranslator.Translate(update, 1, TimestampIso)).IsNull();
    }

    [Test]
    public async Task ToolCallUpdate_terminal_completed_with_content_translates_to_ToolResult() {
        var update = new AcpSessionUpdate(
            AcpUpdateKind.ToolCallUpdate,
            ToolCallId:     "call-1",
            ToolStatus:     "completed",
            ToolResultText: "hi\n",
            ToolIsError:    false);

        var env = AcpEventTranslator.Translate(update, seq: 6, timestampIso: TimestampIso);

        await Assert.That(env).IsNotNull();
        await Assert.That(env!.Value.Kind).IsEqualTo(AcpEventKind.ToolResult);
        await Assert.That(env.Value.ToolCallId).IsEqualTo("call-1");
        await Assert.That(env.Value.ToolResult).IsEqualTo("hi\n");
        await Assert.That(env.Value.ToolIsError).IsFalse();
    }

    [Test]
    public async Task ToolCallUpdate_terminal_failed_with_content_translates_to_ToolResult_with_IsError() {
        var update = new AcpSessionUpdate(
            AcpUpdateKind.ToolCallUpdate,
            ToolCallId:     "call-1",
            ToolStatus:     "failed",
            ToolResultText: "boom",
            ToolIsError:    true);

        var env = AcpEventTranslator.Translate(update, seq: 7, timestampIso: TimestampIso);

        await Assert.That(env!.Value.Kind).IsEqualTo(AcpEventKind.ToolResult);
        await Assert.That(env.Value.ToolResult).IsEqualTo("boom");
        await Assert.That(env.Value.ToolIsError).IsTrue();
    }

    [Test]
    public async Task Plan_AvailableCommands_and_Unknown_all_translate_to_null() {
        await Assert.That(AcpEventTranslator.Translate(new AcpSessionUpdate(AcpUpdateKind.Plan), 1, TimestampIso)).IsNull();
        await Assert.That(AcpEventTranslator.Translate(new AcpSessionUpdate(AcpUpdateKind.AvailableCommands), 1, TimestampIso)).IsNull();
        await Assert.That(AcpEventTranslator.Translate(new AcpSessionUpdate(AcpUpdateKind.Unknown), 1, TimestampIso)).IsNull();
    }

    [Test]
    public async Task SessionInfo_with_title_translates_to_SessionTitle_envelope_carrying_the_title() {
        var update = new AcpSessionUpdate(AcpUpdateKind.SessionInfo, Title: "Shell Reporter");

        var env = AcpEventTranslator.Translate(update, seq: 3, timestampIso: TimestampIso);

        await Assert.That(env).IsNotNull();
        await Assert.That(env!.Value.Kind).IsEqualTo(AcpEventKind.SessionTitle);
        await Assert.That(env.Value.Text).IsEqualTo("Shell Reporter");
        await Assert.That(env.Value.Seq).IsEqualTo(3L);
        await Assert.That(env.Value.TimestampIso).IsEqualTo(TimestampIso);
    }

    [Test]
    [Arguments(null)]
    [Arguments("")]
    [Arguments("   ")]
    public async Task SessionInfo_with_blank_title_translates_to_null(string? title) {
        var update = new AcpSessionUpdate(AcpUpdateKind.SessionInfo, Title: title);

        await Assert.That(AcpEventTranslator.Translate(update, 1, TimestampIso)).IsNull();
    }

    // ── KCAP_ACP_DEBUG_FRAMES gate: Unknown-kind raw-JSON dump ──────────────────────────────────

    static AcpSessionUpdate UnknownUpdateWithSecretMarker() {
        const string secretMarker = "sk-super-secret-prompt-content-marker";
        using var doc = System.Text.Json.JsonDocument.Parse($$"""{"sessionUpdate":"something_new","detail":"{{secretMarker}}"}""");

        return new AcpSessionUpdate(AcpUpdateKind.Unknown, Raw: doc.RootElement.Clone());
    }

    [Test]
    public async Task Unknown_kind_with_DebugFrames_off_by_default_logs_shape_only_never_the_raw_content() {
        var logger = new CaptureLogger();
        var update = UnknownUpdateWithSecretMarker();

        // debugFrames omitted entirely — proves the parameter defaults to Off, matching every
        // pre-existing call site that doesn't pass it.
        var env = AcpEventTranslator.Translate(update, seq: 1, timestampIso: TimestampIso, logger: logger);

        await Assert.That(env).IsNull();
        await Assert.That(logger.Entries).Count().IsEqualTo(1);
        await Assert.That(logger.Entries[0].Level).IsEqualTo(LogLevel.Debug);
        await Assert.That(logger.Entries[0].Message).DoesNotContain("sk-super-secret-prompt-content-marker");
        await Assert.That(logger.Entries[0].Message).Contains("RawLength");
    }

    [Test]
    public async Task Unknown_kind_with_DebugFrames_explicitly_off_logs_shape_only_never_the_raw_content() {
        var logger = new CaptureLogger();
        var update = UnknownUpdateWithSecretMarker();

        AcpEventTranslator.Translate(update, seq: 1, timestampIso: TimestampIso, logger: logger, debugFrames: false);

        await Assert.That(logger.Entries).Count().IsEqualTo(1);
        await Assert.That(logger.Entries[0].Message).DoesNotContain("sk-super-secret-prompt-content-marker");
    }

    [Test]
    public async Task Unknown_kind_with_DebugFrames_on_logs_the_full_raw_content() {
        var logger = new CaptureLogger();
        var update = UnknownUpdateWithSecretMarker();

        AcpEventTranslator.Translate(update, seq: 1, timestampIso: TimestampIso, logger: logger, debugFrames: true);

        await Assert.That(logger.Entries).Count().IsEqualTo(1);
        await Assert.That(logger.Entries[0].Level).IsEqualTo(LogLevel.Debug);
        await Assert.That(logger.Entries[0].Message).Contains("sk-super-secret-prompt-content-marker");
    }

    [Test]
    public async Task Unknown_kind_with_no_logger_supplied_never_throws_regardless_of_DebugFrames() {
        var update = UnknownUpdateWithSecretMarker();

        await Assert.That(AcpEventTranslator.Translate(update, seq: 1, timestampIso: TimestampIso, debugFrames: true)).IsNull();
        await Assert.That(AcpEventTranslator.Translate(update, seq: 1, timestampIso: TimestampIso, debugFrames: false)).IsNull();
    }

    [Test]
    public async Task BuildSessionStarted_stamps_seq_timestamp_and_session_fields() {
        var env = AcpEventTranslator.BuildSessionStarted(
            seq: 0,
            timestampIso: TimestampIso,
            cwd: "/repo",
            model: "claude-opus-4-8",
            rawSessionId: "raw-1",
            sessionMode: "agent");

        await Assert.That(env.Kind).IsEqualTo(AcpEventKind.SessionStarted);
        await Assert.That(env.Seq).IsEqualTo(0L);
        await Assert.That(env.TimestampIso).IsEqualTo(TimestampIso);
        await Assert.That(env.Cwd).IsEqualTo("/repo");
        await Assert.That(env.Model).IsEqualTo("claude-opus-4-8");
        await Assert.That(env.RawSessionId).IsEqualTo("raw-1");
        await Assert.That(env.SessionMode).IsEqualTo("agent");
        await Assert.That(env.ContractVersion).IsEqualTo(1);
    }

    [Test]
    public async Task BuildUserMessage_stamps_seq_timestamp_and_text() {
        var env = AcpEventTranslator.BuildUserMessage(seq: 1, timestampIso: TimestampIso, text: "do the thing");

        await Assert.That(env.Kind).IsEqualTo(AcpEventKind.UserMessage);
        await Assert.That(env.Seq).IsEqualTo(1L);
        await Assert.That(env.TimestampIso).IsEqualTo(TimestampIso);
        await Assert.That(env.Text).IsEqualTo("do the thing");
    }
}
