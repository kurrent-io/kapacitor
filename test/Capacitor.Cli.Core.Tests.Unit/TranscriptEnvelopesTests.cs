using Google.Protobuf.WellKnownTypes;
using Kurrent.Agent.Schema.Events;

namespace Capacitor.Cli.Core.Tests.Unit;

public class TranscriptEnvelopesTests {
    static readonly DateTimeOffset At = new(2026, 8, 26, 12, 0, 0, TimeSpan.Zero);

    static CanonicalEvent Ev(object payload, string? recordTimestamp = "2026-08-26T12:00:00Z") =>
        new(CanonicalEventTypes.Of(payload), payload, Guid.NewGuid(), At, recordTimestamp);

    [Test]
    public async Task Text_payloads_map_to_their_kinds_with_the_raw_record_timestamp() {
        var user = TranscriptEnvelopes.From(Ev(new UserMessageReceived { Content = "hello" }));
        await Assert.That(user).Count().IsEqualTo(1);
        await Assert.That(user[0].Kind).IsEqualTo(AcpEventKind.UserMessage);
        await Assert.That(user[0].Text).IsEqualTo("hello");
        await Assert.That(user[0].TimestampIso).IsEqualTo("2026-08-26T12:00:00Z");

        var text = TranscriptEnvelopes.From(Ev(new AssistantTextGenerated { Content = "Hi" }, recordTimestamp: null));
        await Assert.That(text[0].Kind).IsEqualTo(AcpEventKind.AssistantText);
        await Assert.That(text[0].TimestampIso).IsEqualTo("2026-08-26T12:00:00.0000000+00:00");
    }

    [Test]
    public async Task Thinking_with_empty_content_reads_as_encrypted() {
        var plain = TranscriptEnvelopes.From(Ev(new AssistantThinkingGenerated { Content = "hmm" }))[0];
        await Assert.That(plain.Kind).IsEqualTo(AcpEventKind.AssistantThinking);
        await Assert.That(plain.Text).IsEqualTo("hmm");
        await Assert.That(plain.ThinkingEncrypted).IsFalse();

        var empty = TranscriptEnvelopes.From(Ev(new AssistantThinkingGenerated { Content = "", Encrypted = false }))[0];
        await Assert.That(empty.Text).IsNull();
        await Assert.That(empty.ThinkingEncrypted).IsTrue();
    }

    [Test]
    public async Task Each_tool_call_becomes_one_envelope_with_compact_object_json() {
        var calls = new AssistantToolCallsGenerated();
        calls.ToolCalls.Add(new ToolCallInfo { CallId = "t1", ToolName = "Bash", Arguments = Struct.Parser.ParseJson("""{"command":"ls","n":[1,2],"o":{"a":true,"b":null}}""") });
        calls.ToolCalls.Add(new ToolCallInfo { CallId = "t2", ToolName = "Read" });

        var e = TranscriptEnvelopes.From(Ev(calls));
        await Assert.That(e).Count().IsEqualTo(2);
        await Assert.That(e[0].Kind).IsEqualTo(AcpEventKind.ToolCall);
        await Assert.That(e[0].ToolCallId).IsEqualTo("t1");
        await Assert.That(e[0].ToolName).IsEqualTo("Bash");
        await Assert.That(e[0].ToolInputJson).IsEqualTo("""{"command":"ls","n":[1,2],"o":{"a":true,"b":null}}""");
        await Assert.That(e[1].ToolInputJson).IsEqualTo("{}");
    }

    [Test]
    public async Task Tool_results_are_capped_at_4096_units_marker_included_and_never_split_a_surrogate_pair() {
        var big = TranscriptEnvelopes.From(Ev(new ToolResultReceived { CallId = "t", Result = new string('x', 5000) }))[0];
        await Assert.That(big.Kind).IsEqualTo(AcpEventKind.ToolResult);
        await Assert.That(big.ToolCallId).IsEqualTo("t");
        await Assert.That(big.ToolResult!.Length).IsEqualTo(4096);
        await Assert.That(big.ToolResult).EndsWith("…");
        await Assert.That(big.ToolIsError).IsFalse();

        var pair = new string('x', 4094) + "\U0001F600" + "tail";
        var cut = TranscriptEnvelopes.Cap(pair);
        await Assert.That(cut.Length).IsEqualTo(4095);
        await Assert.That(char.IsHighSurrogate(cut[^2])).IsFalse();
    }

    [Test]
    public async Task Payloads_the_chat_cannot_show_map_to_nothing() {
        await Assert.That(TranscriptEnvelopes.From(Ev(new SessionStarted()))).IsEmpty();
        await Assert.That(TranscriptEnvelopes.From(new CanonicalEvent("Other", new object(), Guid.NewGuid(), At))).IsEmpty();
    }
}
