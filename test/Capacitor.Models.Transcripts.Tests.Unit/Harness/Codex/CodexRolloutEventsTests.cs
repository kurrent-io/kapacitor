using Capacitor.Models.Transcripts.Harness.Codex;
using Kurrent.Agent.Schema.Events;

namespace Capacitor.Models.Transcripts.Tests.Unit.Harness.Codex;

public class CodexRolloutEventsTests {
    static readonly DateTimeOffset Received = new(2026, 9, 4, 10, 0, 0, TimeSpan.Zero);

    static ProjectionResult P(string line) =>
        CodexRolloutEvents.Instance.Project(line, 1, Received, CodexRolloutEvents.Instance.CreateContext("sess", null));

    static IReadOnlyList<CanonicalEvent> E(string line) => P(line).Events;

    static string Item(string payload, string ts = "2026-08-25T00:00:00Z") =>
        $$$"""{"timestamp":"{{{ts}}}","ordinal":1,"type":"response_item","payload":{{{payload}}}}""";

    [Test]
    public async Task User_and_assistant_messages_join_their_text_blocks_on_the_line_hash_id() {
        var line = Item("""{"type":"message","role":"user","content":[{"type":"input_text","text":"a"},{"type":"input_text","text":"b"}]}""");
        var user = E(line);
        await Assert.That(user).Count().IsEqualTo(1);
        await Assert.That(((UserMessageReceived)user[0].Payload).Content).IsEqualTo("a\nb");
        await Assert.That(user[0].EventId).IsEqualTo(TranscriptIds.CodexRecord(line));
        await Assert.That(user[0].RecordTimestamp).IsEqualTo("2026-08-25T00:00:00Z");
        await Assert.That(user[0].CausedBy).IsNull();

        var assistant = E(Item("""{"type":"message","role":"assistant","content":[{"type":"output_text","text":"Hi"}]}"""));
        await Assert.That(((AssistantTextGenerated)assistant[0].Payload).Content).IsEqualTo("Hi");
    }

    [Test]
    public async Task Injected_preludes_are_kept_here_and_developer_and_system_roles_are_skipped() {
        var prelude = E(Item("""{"type":"message","role":"user","content":[{"type":"input_text","text":"<environment_context>\nstuff"}]}"""));
        await Assert.That(prelude).Count().IsEqualTo(1);

        await Assert.That(E(Item("""{"type":"message","role":"developer","content":[{"type":"input_text","text":"x"}]}"""))).IsEmpty();
        await Assert.That(E(Item("""{"type":"message","role":"system","content":[{"type":"input_text","text":"x"}]}"""))).IsEmpty();
        await Assert.That(E(Item("""{"type":"message","role":"assistant","content":[]}"""))).IsEmpty();
    }

    [Test]
    public async Task Tool_calls_always_carry_a_struct_argument() {
        var fn = E(Item("""{"type":"function_call","name":"spawn_agent","call_id":"c1","arguments":"{\"task\":\"t\"}"}"""));
        var call = ((AssistantToolCallsGenerated)fn[0].Payload).ToolCalls[0];
        await Assert.That(call.CallId).IsEqualTo("c1");
        await Assert.That(call.ToolName).IsEqualTo("spawn_agent");
        await Assert.That(call.Arguments.Fields["task"].StringValue).IsEqualTo("t");

        var nonObject = E(Item("""{"type":"function_call","name":"f","call_id":"c2","arguments":"not json"}"""));
        await Assert.That(((AssistantToolCallsGenerated)nonObject[0].Payload).ToolCalls[0].Arguments.Fields["arguments"].StringValue).IsEqualTo("not json");

        var custom = E(Item("""{"type":"custom_tool_call","name":"exec","call_id":"c3","input":"const r = 1;"}"""));
        var customCall = ((AssistantToolCallsGenerated)custom[0].Payload).ToolCalls[0];
        await Assert.That(customCall.ToolName).IsEqualTo("exec");
        await Assert.That(customCall.Arguments.Fields["input"].StringValue).IsEqualTo("const r = 1;");
    }

    [Test]
    public async Task Tool_outputs_carry_string_or_block_output_uncapped() {
        var str = E(Item("""{"type":"function_call_output","call_id":"c1","output":"{\"ok\":true}"}"""));
        var result = (ToolResultReceived)str[0].Payload;
        await Assert.That(result.CallId).IsEqualTo("c1");
        await Assert.That(result.Result).IsEqualTo("""{"ok":true}""");

        var blocks = E(Item("""{"type":"custom_tool_call_output","call_id":"c3","output":[{"type":"input_text","text":"Script completed"},{"type":"input_text","text":"Output:"}]}"""));
        await Assert.That(((ToolResultReceived)blocks[0].Payload).Result).IsEqualTo("Script completed\nOutput:");

        var big = new string('x', 5000);
        var uncapped = E(Item($$"""{"type":"function_call_output","call_id":"c4","output":"{{big}}"}"""));
        await Assert.That(((ToolResultReceived)uncapped[0].Payload).Result.Length).IsEqualTo(5000);
    }

    [Test]
    public async Task Reasoning_joins_summaries_and_flags_encrypted_only_content() {
        var summarized = E(Item("""{"type":"reasoning","summary":[{"type":"summary_text","text":"plan"},{"type":"summary_text","text":"more"}],"encrypted_content":"zzz"}"""));
        var thinking = (AssistantThinkingGenerated)summarized[0].Payload;
        await Assert.That(thinking.Content).IsEqualTo("plan\nmore");
        await Assert.That(thinking.Encrypted).IsFalse();

        var encrypted = (AssistantThinkingGenerated)E(Item("""{"type":"reasoning","summary":[],"encrypted_content":"zzz"}"""))[0].Payload;
        await Assert.That(encrypted.Content).IsEqualTo("");
        await Assert.That(encrypted.Encrypted).IsTrue();
    }

    [Test]
    public async Task Every_other_envelope_and_payload_type_is_ignored_and_malformed_lines_are_rejected() {
        foreach (var type in new[] { "event_msg", "turn_context", "session_meta", "world_state", "compacted", "inter_agent_communication_metadata" }) {
            var r = P($$$"""{"type":"{{{type}}}","payload":{"type":"message","role":"user","content":[{"type":"input_text","text":"x"}]}}""");
            await Assert.That(r.Events).IsEmpty().Because(type);
            await Assert.That(r.Rejected).IsNull().Because(type);
        }
        await Assert.That(E(Item("""{"type":"agent_message","content":[{"type":"input_text","text":"x"}]}"""))).IsEmpty();
        await Assert.That(E(Item("""{"type":"message","role":"user","content":"not-an-array"}"""))).IsEmpty();

        foreach (var line in new[] { "", "garbage", "[1]" }) {
            await Assert.That(P(line).Rejected).IsNotNull().Because(line);
            await Assert.That(P(line).Events).IsEmpty().Because(line);
        }
    }

    [Test]
    public async Task A_missing_timestamp_uses_the_receive_time() {
        var e = E("""{"type":"response_item","payload":{"type":"message","role":"assistant","content":[{"type":"output_text","text":"x"}]}}""");
        await Assert.That(e[0].Timestamp).IsEqualTo(Received);
        await Assert.That(e[0].RecordTimestamp).IsNull();
        await Assert.That(((AssistantTextGenerated)e[0].Payload).Timestamp.ToDateTimeOffset()).IsEqualTo(Received);
    }
}
