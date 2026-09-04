using System.Text.Json;
using Google.Protobuf.WellKnownTypes;

namespace Capacitor.Models.Transcripts.Tests.Unit;

public class TranscriptTextTests {
    [Test]
    public async Task Text_blocks_of_the_named_type_join_with_newlines_and_others_are_skipped() {
        using var doc = JsonDocument.Parse("""[{"type":"text","text":"a"},{"type":"image"},{"type":"text","text":"b"}]""");
        await Assert.That(TranscriptText.JoinTextBlocks(doc.RootElement, "text")).IsEqualTo("a\nb");
        await Assert.That(TranscriptText.JoinTextBlocks(doc.RootElement, "input_text")).IsEqualTo("");
    }

    [Test]
    public async Task StructOf_keeps_field_order_and_nested_values() {
        using var doc = JsonDocument.Parse("""{"command":"ls","opts":{"all":true},"n":[1,2]}""");
        var s = TranscriptText.StructOf(doc.RootElement);
        await Assert.That(s.Fields.Keys.ToArray()).IsEquivalentTo(new[] { "command", "opts", "n" });
        await Assert.That(s.Fields["command"].StringValue).IsEqualTo("ls");
        await Assert.That(s.Fields["opts"].StructValue.Fields["all"].BoolValue).IsTrue();
        await Assert.That(s.Fields["n"].ListValue.Values.Count).IsEqualTo(2);
    }

    [Test]
    public async Task Wrap_puts_a_non_object_value_or_a_string_under_one_property() {
        using var doc = JsonDocument.Parse("[1,2]");
        var wrapped = TranscriptText.Wrap("input", doc.RootElement);
        await Assert.That(wrapped.Fields["input"].KindCase).IsEqualTo(Value.KindOneofCase.ListValue);

        using var nul = JsonDocument.Parse("null");
        await Assert.That(TranscriptText.Wrap("input", nul.RootElement).Fields["input"].KindCase).IsEqualTo(Value.KindOneofCase.NullValue);

        await Assert.That(TranscriptText.Wrap("arguments", "not json").Fields["arguments"].StringValue).IsEqualTo("not json");
    }
}
