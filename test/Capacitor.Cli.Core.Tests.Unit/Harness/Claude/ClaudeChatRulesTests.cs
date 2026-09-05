namespace Capacitor.Cli.Core.Tests.Unit.Harness.Claude;

/// The chat-level view of Claude records: what the Chat tab shows for each record shape.
public class ClaudeChatRulesTests {
    static readonly DateTimeOffset Received = new(2026, 9, 4, 10, 0, 0, TimeSpan.Zero);

    static IReadOnlyList<AcpEventEnvelope> P(string line) {
        var chat = TranscriptChat.For("claude")!;
        return chat.Project(line, 1, Received, chat.CreateContext("a1", null));
    }

    [Test]
    public async Task String_user_content_is_one_user_message_with_its_timestamp() {
        var e = P("""{"type":"user","message":{"role":"user","content":"hello"},"timestamp":"2026-08-26T12:00:00Z"}""");
        await Assert.That(e).Count().IsEqualTo(1);
        await Assert.That(e[0].Kind).IsEqualTo(AcpEventKind.UserMessage);
        await Assert.That(e[0].Text).IsEqualTo("hello");
        await Assert.That(e[0].TimestampIso).IsEqualTo("2026-08-26T12:00:00Z");
    }

    [Test]
    public async Task Meta_and_sidechain_records_project_to_nothing() {
        await Assert.That(P("""{"type":"user","isMeta":true,"message":{"content":"x"}}""")).IsEmpty();
        await Assert.That(P("""{"type":"user","isSidechain":true,"message":{"content":"x"}}""")).IsEmpty();
        await Assert.That(P("""{"type":"assistant","isSidechain":true,"message":{"content":[{"type":"text","text":"x"}]}}""")).IsEmpty();
    }

    [Test]
    public async Task Wrappers_are_stripped_and_a_blank_remainder_is_not_emitted() {
        var stripped = P("""{"type":"user","message":{"content":[{"type":"text","text":"<system-reminder>\nnoise\n</system-reminder>real"}]}}""");
        await Assert.That(stripped).Count().IsEqualTo(1);
        await Assert.That(stripped[0].Text).IsEqualTo("real");

        var onlyWrappers = P("""{"type":"user","message":{"content":[{"type":"text","text":"<command-name>/clear</command-name><local-command-stdout>ok</local-command-stdout>"}]}}""");
        await Assert.That(onlyWrappers).IsEmpty();
    }

    [Test]
    public async Task Tool_results_carry_string_or_block_content_capped_and_flag_errors() {
        var str = P("""{"type":"user","message":{"content":[{"type":"tool_result","tool_use_id":"t1","content":"done","is_error":true}]}}""");
        await Assert.That(str[0].Kind).IsEqualTo(AcpEventKind.ToolResult);
        await Assert.That(str[0].ToolCallId).IsEqualTo("t1");
        await Assert.That(str[0].ToolResult).IsEqualTo("done");
        await Assert.That(str[0].ToolIsError).IsTrue();

        var blocks = P("""{"type":"user","message":{"content":[{"type":"tool_result","tool_use_id":"t2","content":[{"type":"text","text":"a"},{"type":"text","text":"b"}]}]}}""");
        await Assert.That(blocks[0].ToolResult).IsEqualTo("a\nb");
        await Assert.That(blocks[0].ToolIsError).IsFalse();

        var big = new string('x', 5000);
        var capped = P($$$"""{"type":"user","message":{"content":[{"type":"tool_result","tool_use_id":"t3","content":"{{{big}}}"}]}}""");
        await Assert.That(capped[0].ToolResult!.Length).IsEqualTo(4096);
    }

    [Test]
    public async Task Assistant_blocks_map_to_text_thinking_and_tool_call() {
        var line = """{"type":"assistant","timestamp":"2026-08-26T12:00:01Z","message":{"model":"claude-fable-5","content":[{"type":"thinking","thinking":"hmm"},{"type":"text","text":"Hi"},{"type":"tool_use","id":"toolu_1","name":"Bash","input":{"command":"ls"}}]}}""";
        var e = P(line);

        await Assert.That(e).Count().IsEqualTo(3);
        await Assert.That(e[0].Kind).IsEqualTo(AcpEventKind.AssistantThinking);
        await Assert.That(e[0].Text).IsEqualTo("hmm");
        await Assert.That(e[1].Kind).IsEqualTo(AcpEventKind.AssistantText);
        await Assert.That(e[1].Text).IsEqualTo("Hi");
        await Assert.That(e[2].Kind).IsEqualTo(AcpEventKind.ToolCall);
        await Assert.That(e[2].ToolCallId).IsEqualTo("toolu_1");
        await Assert.That(e[2].ToolName).IsEqualTo("Bash");
        await Assert.That(e[2].ToolInputJson).IsEqualTo("""{"command":"ls"}""");
        await Assert.That(e[2].TimestampIso).IsEqualTo("2026-08-26T12:00:01Z");
    }

    [Test]
    public async Task Encrypted_thinking_and_non_object_inputs_are_normalized() {
        var enc = P("""{"type":"assistant","message":{"content":[{"type":"thinking","thinking":"","signature":"abc"}]}}""");
        await Assert.That(enc[0].ThinkingEncrypted).IsTrue();

        foreach (var (input, expected) in new[] {
            ("[1,2]", """{"input":[1,2]}"""),
            ("\"s\"", """{"input":"s"}"""),
            ("null", """{"input":null}"""),
        }) {
            var e = P($$$"""{"type":"assistant","message":{"content":[{"type":"tool_use","id":"t","name":"X","input":{{{input}}}}]}}""");
            await Assert.That(e[0].ToolInputJson).IsEqualTo(expected);
        }
    }

    [Test]
    public async Task Every_other_record_type_and_malformed_input_project_to_nothing() {
        foreach (var type in new[] { "attachment", "summary", "system", "file-history-snapshot", "file-history-delta", "mode", "permission-mode", "last-prompt", "ai-title", "atis-latch", "worktree-state", "queue-operation", "progress", "unknown-future" })
            await Assert.That(P($$$"""{"type":"{{{type}}}","message":{"content":"x"}}""")).IsEmpty().Because(type);

        await Assert.That(P("not json")).IsEmpty();
        await Assert.That(P("[1,2]")).IsEmpty();
        await Assert.That(P("""{"type":"user","message":{"content":42}}""")).IsEmpty();
        await Assert.That(P("""{"type":"assistant","message":{"content":[{"type":"text","text":7}]}}""")).IsEmpty();
    }

    /// A record Claude Code injects for a finished background task is system-attributed, never
    /// the user's words: its summary leads in bold and its result follows as markdown.
    [Test]
    public async Task A_task_notification_projects_to_a_system_note_of_summary_and_result() {
        var line = """{"type":"user","origin":{"kind":"task-notification"},"promptSource":"system","message":{"role":"user","content":"<task-notification>\n<task-id>k1</task-id>\n<status>completed</status>\n<summary>Agent \"Review Task 15\" finished</summary>\n<result>\n## Findings\n\nNone.\n</result>\n</task-notification>"}}""";
        var note = P(line);
        await Assert.That(note).Count().IsEqualTo(1);
        await Assert.That(note[0].Kind).IsEqualTo(AcpEventKind.SystemNote);
        await Assert.That(note[0].Text).IsEqualTo("**Agent \"Review Task 15\" finished**\n\n## Findings\n\nNone.");

        var bare = P("""{"type":"user","origin":{"kind":"task-notification"},"message":{"content":"<task-notification>\n<summary>MCP task done.</summary>\n</task-notification>"}}""");
        await Assert.That(bare[0].Text).IsEqualTo("**MCP task done.**");

        var human = P("""{"type":"user","origin":{"kind":"human"},"promptSource":"typed","message":{"content":"hello"}}""");
        await Assert.That(human[0].Kind).IsEqualTo(AcpEventKind.UserMessage);
    }
}
