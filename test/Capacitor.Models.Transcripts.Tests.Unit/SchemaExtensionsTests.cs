using Google.Protobuf.WellKnownTypes;
using Kurrent.Agent.Schema.Events;

namespace Capacitor.Models.Transcripts.Tests.Unit;

public class SchemaExtensionsTests {
    [Test]
    public async Task Flags_and_text_read_from_one_slug_and_absent_reads_as_false_or_null() {
        var evt = new UserMessageReceived { Content = "x" };
        var slug = new Struct();
        slug.Fields["is_meta"] = Value.ForBool(true);
        slug.Fields["origin_kind"] = Value.ForString("task-notification");
        evt.Extensions["claude_code"] = slug;

        var read = SchemaExtensions.Slug(evt, "claude_code");
        await Assert.That(SchemaExtensions.Flag(read, "is_meta")).IsTrue();
        await Assert.That(SchemaExtensions.Flag(read, "is_sidechain")).IsFalse();
        await Assert.That(SchemaExtensions.Text(read, "origin_kind")).IsEqualTo("task-notification");
        await Assert.That(SchemaExtensions.Text(read, "is_meta")).IsNull();
        await Assert.That(SchemaExtensions.Slug(evt, "codex")).IsNull();
        await Assert.That(SchemaExtensions.Of(new object())).IsNull();
    }

    [Test]
    public async Task Event_type_names_are_the_persisted_names() {
        await Assert.That(CanonicalEventTypes.Of(new AssistantToolCallsGenerated())).IsEqualTo("AssistantToolCallsGenerated");
        await Assert.That(CanonicalEventTypes.Of(new ToolResultReceived())).IsEqualTo("ToolResultReceived");
    }
}
