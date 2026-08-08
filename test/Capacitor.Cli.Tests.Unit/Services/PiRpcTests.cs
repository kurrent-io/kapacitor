// test/Capacitor.Cli.Tests.Unit/Services/PiRpcTests.cs
using Capacitor.Cli.Core;
using Capacitor.Cli.Daemon.Acp;

namespace Capacitor.Cli.Tests.Unit.Services;

/// <summary>
/// Pure unit tests for <see cref="PiRpc"/> — no process/runtime involved. JSONL fixtures below are
/// literal lines matching the pinned Pi protocol shapes from the task brief.
/// </summary>
public class PiRpcTests {
    // ---- TryParseLine: response frames ----

    [Test]
    public async Task Response_frame_parses_with_id_echo_and_success_true() {
        var frame = PiRpc.TryParseLine("""{"id":"abc-1","type":"response","command":"prompt","success":true}""");

        await Assert.That(frame).IsNotNull();
        await Assert.That(frame!.Kind).IsEqualTo(PiRpcFrameKind.Response);
        await Assert.That(frame.Type).IsEqualTo("response");
        await Assert.That(frame.Id).IsEqualTo("abc-1");
        await Assert.That(frame.Success).IsEqualTo(true);
    }

    [Test]
    public async Task Response_frame_parses_with_success_false() {
        var frame = PiRpc.TryParseLine(
            """{"id":"abc-2","type":"response","command":"prompt","success":false,"error":"boom"}""");

        await Assert.That(frame).IsNotNull();
        await Assert.That(frame!.Kind).IsEqualTo(PiRpcFrameKind.Response);
        await Assert.That(frame.Id).IsEqualTo("abc-2");
        await Assert.That(frame.Success).IsEqualTo(false);
        await Assert.That(frame.Root.Str("error")).IsEqualTo("boom");
    }

    // ---- TryParseLine: event frames ----

    [Test]
    public async Task Event_frame_parses_as_Event_kind() {
        var frame = PiRpc.TryParseLine("""{"type":"agent_start"}""");

        await Assert.That(frame).IsNotNull();
        await Assert.That(frame!.Kind).IsEqualTo(PiRpcFrameKind.Event);
        await Assert.That(frame.Type).IsEqualTo("agent_start");
        await Assert.That(frame.Id).IsNull();
    }

    [Test]
    public async Task Object_with_no_type_field_parses_as_Unknown_kind() {
        var frame = PiRpc.TryParseLine("""{"foo":"bar"}""");

        await Assert.That(frame).IsNotNull();
        await Assert.That(frame!.Kind).IsEqualTo(PiRpcFrameKind.Unknown);
        await Assert.That(frame.Type).IsEqualTo("");
    }

    // ---- TryParseLine: null cases ----

    [Test]
    public async Task Blank_line_returns_null() {
        await Assert.That(PiRpc.TryParseLine("")).IsNull();
        await Assert.That(PiRpc.TryParseLine("   ")).IsNull();
    }

    [Test]
    public async Task Garbage_json_returns_null() {
        await Assert.That(PiRpc.TryParseLine("{not json")).IsNull();
        await Assert.That(PiRpc.TryParseLine("not even close")).IsNull();
    }

    [Test]
    public async Task Trailing_carriage_return_is_tolerated() {
        var frame = PiRpc.TryParseLine("{\"type\":\"agent_start\"}\r");

        await Assert.That(frame).IsNotNull();
        await Assert.That(frame!.Kind).IsEqualTo(PiRpcFrameKind.Event);
        await Assert.That(frame.Type).IsEqualTo("agent_start");
    }

    // ---- ToEnvelopes: assistant message_end ----

    [Test]
    public async Task Assistant_message_end_with_text_thinking_toolCall_and_usage_yields_four_envelopes() {
        const string line = """
            {"type":"message_end","message":{"role":"assistant","content":[
                {"type":"text","text":"Hello there"},
                {"type":"thinking","thinking":"pondering..."},
                {"type":"toolCall","id":"call_123","name":"bash","arguments":{"cmd":"ls"}}
            ],"model":"pi-large","usage":{"input":100,"output":50,"cacheRead":0,"cacheWrite":0},"stopReason":"stop"}}
            """;

        var frame = PiRpc.TryParseLine(line);
        await Assert.That(frame).IsNotNull();
        await Assert.That(frame!.Kind).IsEqualTo(PiRpcFrameKind.Event);

        var envelopes = PiRpc.ToEnvelopes(frame, fallbackModel: "fallback-model");

        await Assert.That(envelopes.Count).IsEqualTo(4);

        await Assert.That(envelopes[0].Kind).IsEqualTo(AcpEventKind.AssistantText);
        await Assert.That(envelopes[0].Text).IsEqualTo("Hello there");
        await Assert.That(envelopes[0].Model).IsEqualTo("pi-large");

        await Assert.That(envelopes[1].Kind).IsEqualTo(AcpEventKind.AssistantThinking);
        await Assert.That(envelopes[1].Text).IsEqualTo("pondering...");
        await Assert.That(envelopes[1].Model).IsEqualTo("pi-large");

        await Assert.That(envelopes[2].Kind).IsEqualTo(AcpEventKind.ToolCall);
        await Assert.That(envelopes[2].ToolCallId).IsEqualTo("call_123");
        await Assert.That(envelopes[2].ToolName).IsEqualTo("bash");
        await Assert.That(envelopes[2].ToolInputJson).IsEqualTo("""{"cmd":"ls"}""");
        await Assert.That(envelopes[2].Model).IsEqualTo("pi-large");

        await Assert.That(envelopes[3].Kind).IsEqualTo(AcpEventKind.Usage);
        await Assert.That(envelopes[3].Model).IsEqualTo("pi-large");
        await Assert.That(envelopes[3].ContextUsedTokens).IsEqualTo(100L);
    }

    [Test]
    public async Task Assistant_message_end_falls_back_to_fallback_model_when_message_has_none() {
        const string line = """
            {"type":"message_end","message":{"role":"assistant","content":[
                {"type":"text","text":"hi"}
            ]}}
            """;

        var frame = PiRpc.TryParseLine(line);
        var envelopes = PiRpc.ToEnvelopes(frame!, fallbackModel: "fallback-model");

        await Assert.That(envelopes.Count).IsEqualTo(1);
        await Assert.That(envelopes[0].Kind).IsEqualTo(AcpEventKind.AssistantText);
        await Assert.That(envelopes[0].Model).IsEqualTo("fallback-model");
    }

    // ---- ToEnvelopes: user message_end ----

    [Test]
    public async Task User_message_end_with_content_array_yields_one_user_message_envelope() {
        const string line = """{"type":"message_end","message":{"role":"user","content":[{"type":"text","text":"do the thing"}]}}""";

        var frame = PiRpc.TryParseLine(line);
        var envelopes = PiRpc.ToEnvelopes(frame!, fallbackModel: null);

        await Assert.That(envelopes.Count).IsEqualTo(1);
        await Assert.That(envelopes[0].Kind).IsEqualTo(AcpEventKind.UserMessage);
        await Assert.That(envelopes[0].Text).IsEqualTo("do the thing");
    }

    [Test]
    public async Task User_message_end_with_plain_string_content_is_handled() {
        const string line = """{"type":"message_end","message":{"role":"user","content":"do the other thing"}}""";

        var frame = PiRpc.TryParseLine(line);
        var envelopes = PiRpc.ToEnvelopes(frame!, fallbackModel: null);

        await Assert.That(envelopes.Count).IsEqualTo(1);
        await Assert.That(envelopes[0].Kind).IsEqualTo(AcpEventKind.UserMessage);
        await Assert.That(envelopes[0].Text).IsEqualTo("do the other thing");
    }

    // ---- ToEnvelopes: tool_execution_end ----

    [Test]
    public async Task Tool_execution_end_yields_tool_result_with_text_and_not_error() {
        // Upstream's `result` is an OBJECT carrying a content array (rpc.md ~1003) — not the bare
        // "stdout" shape an earlier revision of this test assumed.
        const string line = """
            {"type":"tool_execution_end","toolCallId":"call_123","toolName":"bash",
             "result":{"content":[{"type":"text","text":"total 48\ndrwxr-xr-x"}],"details":{"truncation":null}},
             "isError":false}
            """;

        var frame = PiRpc.TryParseLine(line);
        var envelopes = PiRpc.ToEnvelopes(frame!, fallbackModel: null);

        await Assert.That(envelopes.Count).IsEqualTo(1);
        await Assert.That(envelopes[0].Kind).IsEqualTo(AcpEventKind.ToolResult);
        await Assert.That(envelopes[0].ToolCallId).IsEqualTo("call_123");
        await Assert.That(envelopes[0].ToolIsError).IsEqualTo(false);
        await Assert.That(envelopes[0].ToolResult).IsEqualTo("total 48\ndrwxr-xr-x");
    }

    [Test]
    public async Task Tool_execution_end_result_concatenates_multiple_content_text_items() {
        const string line = """
            {"type":"tool_execution_end","toolCallId":"call_999","toolName":"bash",
             "result":{"content":[{"type":"text","text":"part one "},{"type":"text","text":"part two"}]},
             "isError":false}
            """;

        var frame = PiRpc.TryParseLine(line);
        var envelopes = PiRpc.ToEnvelopes(frame!, fallbackModel: null);

        await Assert.That(envelopes.Count).IsEqualTo(1);
        await Assert.That(envelopes[0].ToolResult).IsEqualTo("part one part two");
    }

    [Test]
    public async Task Tool_execution_end_result_falls_back_to_raw_json_when_no_content_array() {
        const string line = """{"type":"tool_execution_end","toolCallId":"call_777","result":{"foo":"bar"},"isError":false}""";

        var frame = PiRpc.TryParseLine(line);
        var envelopes = PiRpc.ToEnvelopes(frame!, fallbackModel: null);

        await Assert.That(envelopes.Count).IsEqualTo(1);
        await Assert.That(envelopes[0].ToolResult).IsEqualTo("""{"foo":"bar"}""");
    }

    [Test]
    public async Task Tool_execution_end_with_isError_true_is_marked_as_error() {
        // A plain string result is schema drift, not the documented shape — tolerated verbatim.
        const string line = """{"type":"tool_execution_end","toolCallId":"call_456","toolName":"bash","result":"command not found","isError":true}""";

        var frame = PiRpc.TryParseLine(line);
        var envelopes = PiRpc.ToEnvelopes(frame!, fallbackModel: null);

        await Assert.That(envelopes.Count).IsEqualTo(1);
        await Assert.That(envelopes[0].Kind).IsEqualTo(AcpEventKind.ToolResult);
        await Assert.That(envelopes[0].ToolCallId).IsEqualTo("call_456");
        await Assert.That(envelopes[0].ToolIsError).IsEqualTo(true);
        await Assert.That(envelopes[0].ToolResult).IsEqualTo("command not found");
    }

    // ---- ToEnvelopes: extension_error ----

    [Test]
    public async Task Extension_error_yields_bounded_system_note() {
        const string line = """{"type":"extension_error","error":"something went wrong"}""";

        var frame = PiRpc.TryParseLine(line);
        var envelopes = PiRpc.ToEnvelopes(frame!, fallbackModel: null);

        await Assert.That(envelopes.Count).IsEqualTo(1);
        await Assert.That(envelopes[0].Kind).IsEqualTo(AcpEventKind.SystemNote);
        await Assert.That(envelopes[0].Text).IsEqualTo("something went wrong");
    }

    [Test]
    public async Task Extension_error_text_is_capped_at_500_characters() {
        var longError = new string('x', 800);
        var line = $$"""{"type":"extension_error","error":"{{longError}}"}""";

        var frame = PiRpc.TryParseLine(line);
        var envelopes = PiRpc.ToEnvelopes(frame!, fallbackModel: null);

        await Assert.That(envelopes.Count).IsEqualTo(1);
        await Assert.That(envelopes[0].Kind).IsEqualTo(AcpEventKind.SystemNote);
        await Assert.That(envelopes[0].Text!.Length).IsEqualTo(500);
    }

    // ---- ToEnvelopes: extension_ui_request ----

    [Test]
    public async Task Extension_ui_request_yields_one_system_note_naming_the_method() {
        const string line = """
            {"type":"extension_ui_request","id":"uuid-1","method":"select","title":"Allow dangerous command?","options":["Allow","Block"],"timeout":10000}
            """;

        var frame = PiRpc.TryParseLine(line);
        var envelopes = PiRpc.ToEnvelopes(frame!, fallbackModel: null);

        await Assert.That(envelopes.Count).IsEqualTo(1);
        await Assert.That(envelopes[0].Kind).IsEqualTo(AcpEventKind.SystemNote);
        await Assert.That(envelopes[0].Text).Contains("select");
        await Assert.That(envelopes[0].Text).Contains("hosted sessions cannot answer");
    }

    // ---- ToEnvelopes: known-but-untranslated / unknown events ----

    [Test]
    public async Task Known_untranslated_events_yield_empty_envelope_list() {
        string[] lines = [
            """{"type":"agent_start"}""",
            """{"type":"agent_end"}""",
            """{"type":"agent_settled"}""",
            """{"type":"turn_start"}""",
            """{"type":"turn_end"}""",
            """{"type":"message_start"}""",
            """{"type":"message_update"}""",
            """{"type":"bash_execution_update"}""",
        ];

        foreach (var line in lines) {
            var frame = PiRpc.TryParseLine(line);
            var envelopes = PiRpc.ToEnvelopes(frame!, fallbackModel: null);
            await Assert.That(envelopes.Count).IsEqualTo(0);
        }
    }

    [Test]
    public async Task Unrecognized_event_type_yields_empty_envelope_list() {
        var frame = PiRpc.TryParseLine("""{"type":"some_future_event","stuff":123}""");
        var envelopes = PiRpc.ToEnvelopes(frame!, fallbackModel: null);

        await Assert.That(envelopes.Count).IsEqualTo(0);
    }

    [Test]
    public async Task Response_frame_yields_empty_envelope_list() {
        var frame = PiRpc.TryParseLine("""{"id":"x","type":"response","command":"prompt","success":true}""");
        var envelopes = PiRpc.ToEnvelopes(frame!, fallbackModel: null);

        await Assert.That(envelopes.Count).IsEqualTo(0);
    }

    // ---- Command builders ----

    [Test]
    public async Task PromptCommand_emits_streamingBehavior_followUp() {
        var json = PiRpc.PromptCommand("req-1", "hello");

        await Assert.That(json).Contains(""""streamingBehavior":"followUp"""");
        await Assert.That(json).Contains(""""type":"prompt"""");
        await Assert.That(json).Contains(""""id":"req-1"""");
        await Assert.That(json).Contains(""""message":"hello"""");
    }

    [Test]
    public async Task PromptCommand_round_trips_quotes_newlines_and_emoji_verbatim() {
        const string message = "she said \"hi\"\nline two 🎉";
        var json = PiRpc.PromptCommand("req-2", message);

        var frame = PiRpc.TryParseLine(json);
        await Assert.That(frame).IsNotNull();
        await Assert.That(frame!.Root.Str("message")).IsEqualTo(message);
        await Assert.That(frame.Root.Str("id")).IsEqualTo("req-2");
        await Assert.That(frame.Root.Str("streamingBehavior")).IsEqualTo("followUp");
    }

    [Test]
    public async Task AbortCommand_produces_exact_json() {
        var json = PiRpc.AbortCommand("req-3");
        var frame = PiRpc.TryParseLine(json);

        await Assert.That(frame).IsNotNull();
        await Assert.That(frame!.Root.Str("id")).IsEqualTo("req-3");
        await Assert.That(frame.Root.Str("type")).IsEqualTo("abort");
    }

    [Test]
    public async Task GetStateCommand_produces_exact_json() {
        var json = PiRpc.GetStateCommand("req-4");
        var frame = PiRpc.TryParseLine(json);

        await Assert.That(frame).IsNotNull();
        await Assert.That(frame!.Root.Str("id")).IsEqualTo("req-4");
        await Assert.That(frame.Root.Str("type")).IsEqualTo("get_state");
    }

    // set_model is PR-2's — see PiRpc's class doc. Its correct upstream shape
    // ({"type":"set_model","provider":..,"modelId":..}, rpc.md ~222) will be added with the reviewer
    // / model-selection lane; PR-1 never called SetModelCommand, so it was dead code carrying the
    // WRONG shape ({"model":..}) and has been removed along with this test.
}
