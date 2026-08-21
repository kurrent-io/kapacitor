using Capacitor.Cli.Commands;
using Capacitor.Cli.Core;
using TUnit.Assertions.Enums;

namespace Capacitor.Cli.Tests.Unit.Commands;

public class RecapOutlineTests {
    [Test]
    public async Task FormatTurnOutline_uses_prose_line_when_present() {
        const string turns = """
            [ {"turn_index":0,"user_prompt":"add feature","prose":"Added the feature and tests.","tools":[{"name":"Edit"}],"files":[{"path":"a.cs"}]} ]
            """;

        var outline = RecapCommand.FormatTurnOutline(turns);

        await Assert.That(outline).Contains("## Turns");
        await Assert.That(outline).Contains("Added the feature and tests.");
        await Assert.That(outline).Contains("0");
    }

    [Test]
    public async Task FormatTurnOutline_falls_back_to_prompt_and_metadata_when_prose_absent() {
        const string turns = """
            [ {"turn_index":2,"user_prompt":"investigate the failing build and fix it","prose":null,"tools":[{"name":"Bash"},{"name":"Edit"}],"files":[{"path":"a.cs"},{"path":"b.cs"}]} ]
            """;

        var outline = RecapCommand.FormatTurnOutline(turns);

        await Assert.That(outline).Contains("investigate the failing build");
        await Assert.That(outline).Contains("Bash");
        await Assert.That(outline).Contains("Edit");
        await Assert.That(outline).Contains("2 files");
    }

    [Test]
    public async Task FormatTurnOutline_truncates_long_prompt_in_fallback() {
        var longPrompt = new string('x', 200);
        var turns      = $$"""
            [ {"turn_index":0,"user_prompt":"{{longPrompt}}","prose":null,"tools":[],"files":[]} ]
            """;

        var outline = RecapCommand.FormatTurnOutline(turns);

        await Assert.That(outline).Contains("…");
        await Assert.That(outline.Length).IsLessThan(200);
    }

    [Test]
    public async Task FormatTurnOutline_returns_empty_for_empty_array() {
        await Assert.That(RecapCommand.FormatTurnOutline("[]")).IsEqualTo("");
    }

    [Test]
    public async Task FormatTurnOutline_returns_empty_for_non_json_body() {
        await Assert.That(RecapCommand.FormatTurnOutline("not json")).IsEqualTo("");
    }

    [Test]
    public async Task FormatTurnOutline_returns_empty_for_empty_body() {
        await Assert.That(RecapCommand.FormatTurnOutline("")).IsEqualTo("");
    }

    [Test]
    public async Task FormatTurnOutline_collapses_internal_newlines_to_single_line() {
        // prose carries a real newline (the \n is a JSON escape, decoded to U+000A by the parser).
        const string turns = """
            [ {"turn_index":7,"user_prompt":"p","prose":"first line\nsecond line","tools":[],"files":[]} ]
            """;

        var outline = RecapCommand.FormatTurnOutline(turns);

        // The turn must render on exactly one line: the internal newline is collapsed, not passed
        // through as a wrapped, unindented second line that breaks the outline's alignment.
        var turnLine = outline.Split('\n', StringSplitOptions.RemoveEmptyEntries).Single(l => l.Contains("first line"));

        await Assert.That(turnLine).DoesNotContain("\n");
        await Assert.That(turnLine).Contains("second line");
        await Assert.That(turnLine.TrimStart()).StartsWith("7");
    }

    [Test]
    public async Task FormatTurnOutline_returns_empty_when_turn_index_is_wrong_type() {
        // Well-formed JSON array, but turn_index is a string — GetInt32() throws
        // InvalidOperationException, which must degrade to "" rather than crash recap.
        const string turns = """
            [ {"turn_index":"0","user_prompt":"p","prose":"x","tools":[],"files":[]} ]
            """;

        await Assert.That(RecapCommand.FormatTurnOutline(turns)).IsEqualTo("");
    }

    [Test]
    public async Task ExtractToolNames_dedupes_and_preserves_order() {
        using var doc = System.Text.Json.JsonDocument.Parse(
            """{"tools":[{"name":"Edit"},{"name":"Bash"},{"name":"Edit"}]}""");

        var names = RecapCommand.ExtractToolNames(doc.RootElement);

        await Assert.That(names).IsEquivalentTo(new List<string> { "Edit", "Bash" }, CollectionOrdering.Matching);
    }

    [Test]
    public async Task DistinctSessionIds_preserves_first_seen_order_and_drops_nulls() {
        var now     = DateTimeOffset.UtcNow;
        var entries = new List<RecapEntry> {
            new("whats_done", "A", null, null, "sa", null, now),
            new("plan",       "A", null, null, "pa", null, now),
            new("whats_done", null, null, null, "orphan", null, now),
            new("whats_done", "B", null, null, "sb", null, now),
            new("plan",       "A", null, null, "pa2", null, now),
        };

        var ids = RecapCommand.DistinctSessionIds(entries);

        await Assert.That(ids).IsEquivalentTo(new List<string> { "A", "B" }, CollectionOrdering.Matching);
    }

    [Test]
    public async Task OutlineSessionIds_uses_resolved_ids_from_entries_not_input() {
        var now     = DateTimeOffset.UtcNow;
        var entries = new List<RecapEntry> {
            new("whats_done", "concrete-guid", null, null, "s", null, now),
            new("user_prompt", "concrete-guid", null, null, "hi", null, now),
        };

        // Input is a slug; /recap resolved it to the concrete id, so the outline must target that id.
        var ids = RecapCommand.OutlineSessionIds(entries, "meta-session-slug");

        await Assert.That(ids).IsEquivalentTo(new List<string> { "concrete-guid" }, CollectionOrdering.Matching);
    }

    [Test]
    public async Task OutlineSessionIds_falls_back_to_input_when_no_entries() {
        var ids = RecapCommand.OutlineSessionIds([], "the-input-id");

        await Assert.That(ids).IsEquivalentTo(new List<string> { "the-input-id" }, CollectionOrdering.Matching);
    }

    [Test]
    public async Task DrillDownPointer_includes_concrete_id_for_single_session() {
        var hint = RecapCommand.DrillDownPointer(new List<string> { "abc-123" });

        await Assert.That(hint).Contains("--get-turn <N> abc-123");
        await Assert.That(hint).DoesNotContain("<sessionId>");
    }

    [Test]
    public async Task DrillDownPointer_uses_placeholder_for_multiple_sessions() {
        var hint = RecapCommand.DrillDownPointer(new List<string> { "a", "b" });

        await Assert.That(hint).Contains("<sessionId>");
    }
}
