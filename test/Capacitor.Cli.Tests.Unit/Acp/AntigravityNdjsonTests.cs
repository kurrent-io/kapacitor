// test/Capacitor.Cli.Tests.Unit/Acp/AntigravityNdjsonTests.cs
using Capacitor.Cli.Core;
using Capacitor.Cli.Daemon.Acp;

namespace Capacitor.Cli.Tests.Unit.Acp;

/// <summary>
/// Pure unit tests for <see cref="AntigravityNdjson"/> and
/// <see cref="AntigravityStepAccumulator"/> — no process/runtime involved. Every non-obvious
/// fixture below is a VERBATIM line captured from a live <c>agy 1.1.10 -p … --output-format
/// stream-json</c> run (see the task brief), not invented, so a shape this test locks in is a shape
/// actually observed on the wire.
/// </summary>
public class AntigravityNdjsonTests {
    const string InitLine =
        """{"event":"init","conversation_id":"e80c33bf-c10f-4d2f-b626-b0043f488fc0","init":{"cwd":"/w","tools":["call_mcp_tool"],"permission_mode":"request-review"}}""";

    [Test]
    public async Task Init_yields_a_session_started_carrying_the_conversation_id() {
        var evt = AntigravityNdjson.TryParseLine(InitLine);
        await Assert.That(evt!.Kind).IsEqualTo(AntigravityEventKind.Init);
        await Assert.That(evt.ConversationId).IsEqualTo("e80c33bf-c10f-4d2f-b626-b0043f488fc0");

        var envelopes = AntigravityNdjson.ToEnvelopes(evt, model: "gemini-3.5-flash");
        await Assert.That(envelopes.Count).IsEqualTo(1);
        await Assert.That(envelopes[0].Kind).IsEqualTo(AcpEventKind.SessionStarted);
        await Assert.That(envelopes[0].RawSessionId).IsEqualTo("e80c33bf-c10f-4d2f-b626-b0043f488fc0");
        await Assert.That(envelopes[0].Cwd).IsEqualTo("/w");
        await Assert.That(envelopes[0].Model).IsEqualTo("gemini-3.5-flash");
    }

    [Test]
    public async Task A_terminal_result_never_produces_a_session_ended_envelope() {
        // The server's EndAgentSession owns session termination. A runtime that emits
        // session_ended would double-end the session.
        var evt = AntigravityNdjson.TryParseLine(
            """{"event":"result","result":{"conversation_id":"c","status":"SUCCESS","response":"ok","num_turns":1}}""");

        await Assert.That(evt!.Kind).IsEqualTo(AntigravityEventKind.Result);
        await Assert.That(evt.Status).IsEqualTo("SUCCESS");
        await Assert.That(AntigravityNdjson.ToEnvelopes(evt, null)
            .Any(e => e.Kind == AcpEventKind.SessionEnded)).IsFalse();
    }

    [Test]
    public async Task An_unknown_step_type_is_tolerated_not_rejected() {
        // Real agy output contains step_type:"unknown". Treating it as a protocol
        // violation would kill live reviewers.
        var evt = AntigravityNdjson.TryParseLine(
            """{"event":"step_update","step_update":{"conversation_id":"c","step_index":1,"state":"DONE","step_type":"unknown"}}""");

        await Assert.That(evt).IsNotNull();
        await Assert.That(evt!.Kind).IsEqualTo(AntigravityEventKind.StepUpdate);
    }

    [Test]
    public async Task A_malformed_or_blank_line_returns_null_rather_than_throwing() {
        await Assert.That(AntigravityNdjson.TryParseLine("")).IsNull();
        await Assert.That(AntigravityNdjson.TryParseLine("not json")).IsNull();
        await Assert.That(AntigravityNdjson.TryParseLine("""{"event":"nope"}""")!.Kind)
            .IsEqualTo(AntigravityEventKind.Unknown);
    }

    [Test]
    public async Task Text_deltas_aggregate_per_step_and_flush_on_done() {
        var acc = new AntigravityStepAccumulator();
        acc.Add(AntigravityNdjson.TryParseLine(
            """{"event":"step_update","step_update":{"step_index":2,"state":"ACTIVE","step_type":"agent_response","text_delta":"PROBE"}}""")!);
        var mid = acc.Flush(model: null);
        await Assert.That(mid.Count).IsEqualTo(0);   // nothing flushed while ACTIVE

        acc.Add(AntigravityNdjson.TryParseLine(
            """{"event":"step_update","step_update":{"step_index":2,"state":"DONE","step_type":"agent_response","text_delta":"_OK"}}""")!);
        var done = acc.Flush(model: null);

        await Assert.That(done.Any(e => e.Kind == AcpEventKind.AssistantText && e.Text == "PROBE_OK")).IsTrue();
    }

    [Test]
    public async Task A_step_flushed_once_is_not_re_emitted_on_a_later_flush() {
        var acc = new AntigravityStepAccumulator();
        acc.Add(AntigravityNdjson.TryParseLine(
            """{"event":"step_update","step_update":{"step_index":2,"state":"DONE","step_type":"agent_response","text_delta":"ONCE"}}""")!);
        var first = acc.Flush(model: null);
        await Assert.That(first.Count(e => e.Kind == AcpEventKind.AssistantText)).IsEqualTo(1);

        var second = acc.Flush(model: null);
        await Assert.That(second.Count).IsEqualTo(0);
    }

    [Test]
    public async Task Usage_on_a_done_agent_response_step_flushes_alongside_the_aggregated_text() {
        // Verbatim captured line: the DONE step_update carries both the tail of the text_delta
        // stream AND the step's usage block in the same event.
        var acc = new AntigravityStepAccumulator();
        acc.Add(AntigravityNdjson.TryParseLine(
            """{"event":"step_update","step_update":{"conversation_id":"e80c33bf-c10f-4d2f-b626-b0043f488fc0","step_index":2,"state":"ACTIVE","step_type":"agent_response","text_delta":"PROBE_OK"}}""")!);
        acc.Add(AntigravityNdjson.TryParseLine(
            """{"event":"step_update","step_update":{"conversation_id":"e80c33bf-c10f-4d2f-b626-b0043f488fc0","step_index":2,"state":"DONE","step_type":"agent_response","text_delta":"\n","duration_seconds":2.494169,"usage":{"input_tokens":16392,"output_tokens":46,"thinking_tokens":42,"cache_read_tokens":0,"total_tokens":16438}}}""")!);

        var envelopes = acc.Flush(model: "gemini-3.5-flash");

        var text = envelopes.Single(e => e.Kind == AcpEventKind.AssistantText);
        await Assert.That(text.Text).IsEqualTo("PROBE_OK\n");

        var usage = envelopes.Single(e => e.Kind == AcpEventKind.Usage);
        await Assert.That(usage.Model).IsEqualTo("gemini-3.5-flash");
        await Assert.That(usage.ContextUsedTokens).IsEqualTo(16392L);
    }

    [Test]
    public async Task A_done_checkpoint_step_with_no_text_flushes_only_usage() {
        // Verbatim captured line: a "checkpoint" step carries usage but no text_delta at all.
        var acc = new AntigravityStepAccumulator();
        acc.Add(AntigravityNdjson.TryParseLine(
            """{"event":"step_update","step_update":{"conversation_id":"e80c33bf-c10f-4d2f-b626-b0043f488fc0","step_index":3,"state":"DONE","step_type":"checkpoint","duration_seconds":0.787126,"usage":{"input_tokens":98,"output_tokens":3,"thinking_tokens":0,"cache_read_tokens":0,"total_tokens":101}}}""")!);

        var envelopes = acc.Flush(model: null);

        await Assert.That(envelopes.Count).IsEqualTo(1);
        await Assert.That(envelopes[0].Kind).IsEqualTo(AcpEventKind.Usage);
        await Assert.That(envelopes[0].ContextUsedTokens).IsEqualTo(98L);
    }

    [Test]
    public async Task A_done_step_with_neither_text_nor_usage_flushes_nothing() {
        // Verbatim captured line: a bare "user_input" DONE marker carries neither.
        var acc = new AntigravityStepAccumulator();
        acc.Add(AntigravityNdjson.TryParseLine(
            """{"event":"step_update","step_update":{"conversation_id":"e80c33bf-c10f-4d2f-b626-b0043f488fc0","step_index":0,"state":"DONE","step_type":"user_input"}}""")!);

        await Assert.That(acc.Flush(model: null).Count).IsEqualTo(0);
    }

    [Test]
    public async Task Independent_steps_flush_independently() {
        var acc = new AntigravityStepAccumulator();
        acc.Add(AntigravityNdjson.TryParseLine(
            """{"event":"step_update","step_update":{"step_index":1,"state":"DONE","step_type":"unknown","duration_seconds":0.001982}}""")!);
        acc.Add(AntigravityNdjson.TryParseLine(
            """{"event":"step_update","step_update":{"step_index":2,"state":"ACTIVE","step_type":"agent_response","text_delta":"still going"}}""")!);

        var envelopes = acc.Flush(model: null);

        // Step 1 (DONE, content-free) contributes nothing; step 2 (still ACTIVE) is untouched —
        // neither should produce an envelope, and step 2 must survive for a later flush.
        await Assert.That(envelopes.Count).IsEqualTo(0);

        acc.Add(AntigravityNdjson.TryParseLine(
            """{"event":"step_update","step_update":{"step_index":2,"state":"DONE","step_type":"agent_response","text_delta":"!"}}""")!);
        var later = acc.Flush(model: null);
        await Assert.That(later.Any(e => e.Kind == AcpEventKind.AssistantText && e.Text == "still going!")).IsTrue();
    }
}
