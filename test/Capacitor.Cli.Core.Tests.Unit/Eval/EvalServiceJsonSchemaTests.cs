using System.Text.Json;
using Capacitor.Cli.Core.Eval;

namespace Capacitor.Cli.Core.Tests.Unit.Eval;

/// <summary>
/// Validates that <see cref="EvalService.GetRetrospectiveJsonSchema()"/> encodes
/// the structured <c>{text, audience}</c> suggestion shape so that the
/// Claude CLI judge is actually constrained to emit objects — not bare
/// strings — for every suggestion item. Regression guard for that shape.
/// </summary>
public class EvalServiceJsonSchemaTests {
    // EvalService is internal; Capacitor.Cli.Core ships InternalsVisibleTo
    // for Capacitor.Cli.Tests.Unit so the const is directly accessible.

    [Test]
    public async Task RetrospectiveJsonSchema_suggestions_items_is_object_not_string() {
        using var doc  = JsonDocument.Parse(EvalService.GetRetrospectiveJsonSchema());
        var root       = doc.RootElement;

        var itemsType = root
            .GetProperty("properties")
            .GetProperty("suggestions")
            .GetProperty("items")
            .GetProperty("type")
            .GetString();

        await Assert.That(itemsType).IsEqualTo("object");
    }

    [Test]
    public async Task RetrospectiveJsonSchema_suggestions_items_has_text_and_audience_properties() {
        using var doc = JsonDocument.Parse(EvalService.GetRetrospectiveJsonSchema());
        var root      = doc.RootElement;

        var itemProps = root
            .GetProperty("properties")
            .GetProperty("suggestions")
            .GetProperty("items")
            .GetProperty("properties");

        await Assert.That(itemProps.TryGetProperty("text",     out _)).IsTrue();
        await Assert.That(itemProps.TryGetProperty("audience", out _)).IsTrue();
    }

    [Test]
    public async Task RetrospectiveJsonSchema_audience_enum_contains_agent_and_human() {
        using var doc = JsonDocument.Parse(EvalService.GetRetrospectiveJsonSchema());
        var root      = doc.RootElement;

        var audienceEnum = root
            .GetProperty("properties")
            .GetProperty("suggestions")
            .GetProperty("items")
            .GetProperty("properties")
            .GetProperty("audience")
            .GetProperty("enum");

        var values = audienceEnum.EnumerateArray()
            .Select(e => e.GetString())
            .ToList();

        await Assert.That(values).Contains("agent");
        await Assert.That(values).Contains("human");
    }

    [Test]
    public async Task RetrospectiveJsonSchema_suggestions_items_required_includes_text_and_audience() {
        using var doc = JsonDocument.Parse(EvalService.GetRetrospectiveJsonSchema());
        var root      = doc.RootElement;

        var required = root
            .GetProperty("properties")
            .GetProperty("suggestions")
            .GetProperty("items")
            .GetProperty("required");

        var fields = required.EnumerateArray()
            .Select(e => e.GetString())
            .ToList();

        await Assert.That(fields).Contains("text");
        await Assert.That(fields).Contains("audience");
    }

    [Test]
    public async Task RetrospectiveJsonSchema_outer_required_includes_all_top_level_fields() {
        using var doc = JsonDocument.Parse(EvalService.GetRetrospectiveJsonSchema());
        var root      = doc.RootElement;

        var required = root
            .GetProperty("required")
            .EnumerateArray()
            .Select(e => e.GetString())
            .ToList();

        await Assert.That(required).Contains("overall");
        await Assert.That(required).Contains("strengths");
        await Assert.That(required).Contains("issues");
        await Assert.That(required).Contains("suggestions");
    }

    [Test]
    public async Task RetrospectiveJsonSchema_suggestions_maxItems_is_five() {
        using var doc = JsonDocument.Parse(EvalService.GetRetrospectiveJsonSchema());
        var root      = doc.RootElement;

        var maxItems = root
            .GetProperty("properties")
            .GetProperty("suggestions")
            .GetProperty("maxItems")
            .GetInt32();

        await Assert.That(maxItems).IsEqualTo(5);
    }
}
