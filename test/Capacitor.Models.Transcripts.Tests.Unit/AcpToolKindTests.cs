namespace Capacitor.Models.Transcripts.Tests.Unit;

public class AcpToolKindTests {
    [Test]
    public async Task Constants_match_the_ACP_tool_kind_vocabulary() {
        await Assert.That(AcpToolKind.Read).IsEqualTo("read");
        await Assert.That(AcpToolKind.Edit).IsEqualTo("edit");
        await Assert.That(AcpToolKind.Delete).IsEqualTo("delete");
        await Assert.That(AcpToolKind.Move).IsEqualTo("move");
        await Assert.That(AcpToolKind.Search).IsEqualTo("search");
        await Assert.That(AcpToolKind.Execute).IsEqualTo("execute");
        await Assert.That(AcpToolKind.Think).IsEqualTo("think");
        await Assert.That(AcpToolKind.Fetch).IsEqualTo("fetch");
        await Assert.That(AcpToolKind.SwitchMode).IsEqualTo("switch_mode");
        await Assert.That(AcpToolKind.Other).IsEqualTo("other");
    }

    /// The closed set is what lets a consumer switch on ten tokens. An absent kind stays absent —
    /// "no lane classified this" is a different answer from "none of the above".
    [Test]
    public async Task Normalize_keeps_the_vocabulary_closed_and_absence_distinguishable() {
        foreach (var known in new[] { "read", "edit", "delete", "move", "search", "execute", "think", "fetch", "switch_mode", "other" })
            await Assert.That(AcpToolKind.Normalize(known)).IsEqualTo(known).Because(known);

        await Assert.That(AcpToolKind.Normalize("Read")).IsEqualTo(AcpToolKind.Other);
        await Assert.That(AcpToolKind.Normalize("summarise")).IsEqualTo(AcpToolKind.Other);
        await Assert.That(AcpToolKind.Normalize(null)).IsNull();
        await Assert.That(AcpToolKind.Normalize("")).IsNull();
        await Assert.That(AcpToolKind.Normalize("  ")).IsNull();
    }
}
