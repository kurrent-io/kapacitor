using System.Text.Json;
using Capacitor.Cli.Core.Harness.Codex;

namespace Capacitor.Cli.Core.Tests.Unit.Harness.Codex;

public class CodexRolloutEventsTests {
    static IReadOnlyList<AcpEventEnvelope> P(string line) => CodexRolloutEvents.Instance.Project(line);

    static string Item(string payload, string ts = "2026-08-25T00:00:00Z") =>
        $$$"""{"timestamp":"{{{ts}}}","ordinal":1,"type":"response_item","payload":{{{payload}}}}""";

    [Test]
    public async Task User_and_assistant_messages_join_their_text_blocks() {
        var user = P(Item("""{"type":"message","role":"user","content":[{"type":"input_text","text":"a"},{"type":"input_text","text":"b"}]}"""));
        await Assert.That(user[0].Kind).IsEqualTo(AcpEventKind.UserMessage);
        await Assert.That(user[0].Text).IsEqualTo("a\nb");
        await Assert.That(user[0].TimestampIso).IsEqualTo("2026-08-25T00:00:00Z");

        var assistant = P(Item("""{"type":"message","role":"assistant","content":[{"type":"output_text","text":"Hi"}]}"""));
        await Assert.That(assistant[0].Kind).IsEqualTo(AcpEventKind.AssistantText);
        await Assert.That(assistant[0].Text).IsEqualTo("Hi");
    }

    [Test]
    public async Task Injected_preludes_developer_and_system_roles_are_skipped() {
        foreach (var prelude in new[] { "<environment_context>", "# AGENTS.md instructions", "<turn_aborted>", "<user_instructions>", "<permissions instructions>" })
            await Assert.That(P(Item($$"""{"type":"message","role":"user","content":[{"type":"input_text","text":"{{prelude}}\nstuff"}]}"""))).IsEmpty().Because(prelude);

        await Assert.That(P(Item("""{"type":"message","role":"developer","content":[{"type":"input_text","text":"x"}]}"""))).IsEmpty();
        await Assert.That(P(Item("""{"type":"message","role":"system","content":[{"type":"input_text","text":"x"}]}"""))).IsEmpty();
    }

    [Test]
    public async Task Tool_calls_always_carry_a_json_object_input() {
        var fn = P(Item("""{"type":"function_call","name":"spawn_agent","call_id":"c1","arguments":"{\"task\":\"t\"}"}"""));
        await Assert.That(fn[0].Kind).IsEqualTo(AcpEventKind.ToolCall);
        await Assert.That(fn[0].ToolCallId).IsEqualTo("c1");
        await Assert.That(fn[0].ToolName).IsEqualTo("spawn_agent");
        await Assert.That(fn[0].ToolInputJson).IsEqualTo("""{"task":"t"}""");

        var nonObject = P(Item("""{"type":"function_call","name":"f","call_id":"c2","arguments":"not json"}"""));
        await Assert.That(nonObject[0].ToolInputJson).IsEqualTo("""{"arguments":"not json"}""");

        var custom = P(Item("""{"type":"custom_tool_call","name":"exec","call_id":"c3","input":"const r = 1;"}"""));
        await Assert.That(custom[0].ToolName).IsEqualTo("exec");
        await Assert.That(custom[0].ToolInputJson).IsEqualTo("""{"input":"const r = 1;"}""");

        foreach (var e in new[] { fn[0], nonObject[0], custom[0] }) {
            using var doc = JsonDocument.Parse(e.ToolInputJson!);
            await Assert.That(doc.RootElement.ValueKind).IsEqualTo(JsonValueKind.Object);
        }
    }

    [Test]
    public async Task Tool_outputs_carry_string_or_block_output_capped() {
        var str = P(Item("""{"type":"function_call_output","call_id":"c1","output":"{\"ok\":true}"}"""));
        await Assert.That(str[0].Kind).IsEqualTo(AcpEventKind.ToolResult);
        await Assert.That(str[0].ToolCallId).IsEqualTo("c1");
        await Assert.That(str[0].ToolResult).IsEqualTo("""{"ok":true}""");

        var blocks = P(Item("""{"type":"custom_tool_call_output","call_id":"c3","output":[{"type":"input_text","text":"Script completed"},{"type":"input_text","text":"Output:"}]}"""));
        await Assert.That(blocks[0].ToolResult).IsEqualTo("Script completed\nOutput:");

        var big = new string('x', 5000);
        var capped = P(Item($$"""{"type":"function_call_output","call_id":"c4","output":"{{big}}"}"""));
        await Assert.That(capped[0].ToolResult!.Length).IsEqualTo(4096);
    }

    [Test]
    public async Task Reasoning_joins_summaries_and_flags_encrypted_only_content() {
        var summarized = P(Item("""{"type":"reasoning","summary":[{"type":"summary_text","text":"plan"},{"type":"summary_text","text":"more"}],"encrypted_content":"zzz"}"""));
        await Assert.That(summarized[0].Kind).IsEqualTo(AcpEventKind.AssistantThinking);
        await Assert.That(summarized[0].Text).IsEqualTo("plan\nmore");
        await Assert.That(summarized[0].ThinkingEncrypted).IsFalse();

        var encrypted = P(Item("""{"type":"reasoning","summary":[],"encrypted_content":"zzz"}"""));
        await Assert.That(encrypted[0].Text).IsNull();
        await Assert.That(encrypted[0].ThinkingEncrypted).IsTrue();
    }

    [Test]
    public async Task Every_other_envelope_and_payload_type_projects_to_nothing() {
        foreach (var type in new[] { "event_msg", "turn_context", "session_meta", "world_state", "compacted", "inter_agent_communication_metadata" })
            await Assert.That(P($$$"""{"type":"{{{type}}}","payload":{"type":"message","role":"user","content":[{"type":"input_text","text":"x"}]}}""")).IsEmpty().Because(type);

        await Assert.That(P(Item("""{"type":"agent_message","content":[{"type":"input_text","text":"x"}]}"""))).IsEmpty();
        await Assert.That(P("garbage")).IsEmpty();
        await Assert.That(P(Item("""{"type":"message","role":"user","content":"not-an-array"}"""))).IsEmpty();
    }
}
