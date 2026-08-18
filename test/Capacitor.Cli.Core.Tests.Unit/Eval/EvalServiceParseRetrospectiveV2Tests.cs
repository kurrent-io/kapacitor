using Capacitor.Cli.Core.Eval;

namespace Capacitor.Cli.Core.Tests.Unit.Eval;

public class EvalServiceParseRetrospectiveV2Tests {
    [Test]
    public async Task Parses_structured_suggestions_with_audience() {
        var json = """
            {
              "overall": "fine",
              "strengths": [],
              "issues": [],
              "suggestions": [
                { "text": "Run tests", "audience": "agent" },
                { "text": "Discuss with team", "audience": "human" }
              ]
            }
            """;

        var retro = EvalService.ParseRetrospectiveV2(json);

        await Assert.That(retro).IsNotNull();
        await Assert.That(retro!.Suggestions[0].Text).IsEqualTo("Run tests");
        await Assert.That(retro!.Suggestions[0].Audience).IsEqualTo("agent");
        await Assert.That(retro!.Suggestions[1].Audience).IsEqualTo("human");
    }

    [Test]
    public async Task Coerces_unknown_audience_to_human() {
        var json = """
            { "overall": "x", "strengths": [], "issues": [],
              "suggestions": [ { "text": "y", "audience": "bot" } ] }
            """;
        var retro = EvalService.ParseRetrospectiveV2(json);
        await Assert.That(retro!.Suggestions[0].Audience).IsEqualTo("human");
    }

    [Test]
    public async Task Coerces_missing_audience_to_human() {
        var json = """
            { "overall": "x", "strengths": [], "issues": [],
              "suggestions": [ { "text": "y" } ] }
            """;
        var retro = EvalService.ParseRetrospectiveV2(json);
        await Assert.That(retro!.Suggestions[0].Audience).IsEqualTo("human");
    }

    [Test]
    public async Task Coerces_legacy_string_suggestion_to_human() {
        var json = """
            { "overall": "x", "strengths": [], "issues": [],
              "suggestions": [ "Run tests" ] }
            """;
        var retro = EvalService.ParseRetrospectiveV2(json);
        await Assert.That(retro!.Suggestions[0].Text).IsEqualTo("Run tests");
        await Assert.That(retro!.Suggestions[0].Audience).IsEqualTo("human");
    }

    [Test]
    public async Task Strips_code_fences() {
        var json = """
            ```json
            { "overall": "x", "suggestions": [] }
            ```
            """;
        var retro = EvalService.ParseRetrospectiveV2(json);
        await Assert.That(retro).IsNotNull();
        await Assert.That(retro!.OverallSummary).IsEqualTo("x");
    }

    [Test]
    public async Task Returns_null_on_malformed_json() {
        await Assert.That(EvalService.ParseRetrospectiveV2("{not json")).IsNull();
        await Assert.That(EvalService.ParseRetrospectiveV2("")).IsNull();
    }

    [Test]
    public async Task Parser_returns_null_when_root_field_is_wrong_type() {
        // `overall` as a number, not a string — must not throw, parser returns
        // a retrospective with overall = "" rather than throwing.
        var json = """
            { "overall": 42, "strengths": [], "issues": [], "suggestions": [] }
            """;
        var retro = EvalService.ParseRetrospectiveV2(json);
        await Assert.That(retro).IsNotNull();
        await Assert.That(retro!.OverallSummary).IsEqualTo("");
    }

    [Test]
    public async Task Parser_tolerates_suggestion_text_wrong_type() {
        // `text` as a boolean — must not throw; coerce to empty text.
        var json = """
            { "overall": "x", "strengths": [], "issues": [],
              "suggestions": [ { "text": false, "audience": "agent" } ] }
            """;
        var retro = EvalService.ParseRetrospectiveV2(json);
        await Assert.That(retro!.Suggestions[0].Text).IsEqualTo("");
    }

    [Test]
    public async Task Parser_normalizes_case_in_audience() {
        var json = """
            { "overall": "x", "strengths": [], "issues": [],
              "suggestions": [ { "text": "y", "audience": "Agent" } ] }
            """;
        var retro = EvalService.ParseRetrospectiveV2(json);
        await Assert.That(retro!.Suggestions[0].Audience).IsEqualTo("agent");
    }

    [Test]
    public async Task Parser_normalizes_whitespace_in_audience() {
        var json = """
            { "overall": "x", "strengths": [], "issues": [],
              "suggestions": [ { "text": "y", "audience": " human " } ] }
            """;
        var retro = EvalService.ParseRetrospectiveV2(json);
        await Assert.That(retro!.Suggestions[0].Audience).IsEqualTo("human");
    }

    [Test]
    public async Task Parser_normalizes_uppercase_audience() {
        var json = """
            { "overall": "x", "strengths": [], "issues": [],
              "suggestions": [ { "text": "y", "audience": "AGENT" } ] }
            """;
        var retro = EvalService.ParseRetrospectiveV2(json);
        await Assert.That(retro!.Suggestions[0].Audience).IsEqualTo("agent");
    }

    [Test]
    public async Task Parser_tolerates_audience_wrong_type() {
        var json = """
            { "overall": "x", "strengths": [], "issues": [],
              "suggestions": [ { "text": "y", "audience": 1 } ] }
            """;
        var retro = EvalService.ParseRetrospectiveV2(json);
        await Assert.That(retro!.Suggestions[0].Audience).IsEqualTo("human");
    }

    [Test]
    public async Task Mixed_input_parses_all_three_audience_paths() {
        var json = """
            {
              "overall": "x",
              "strengths": [],
              "issues": [],
              "suggestions": [
                "legacy string",
                { "text": "tagged agent", "audience": "agent" },
                { "text": "unknown audience", "audience": "bot" }
              ]
            }
            """;
        var retro = EvalService.ParseRetrospectiveV2(json);

        await Assert.That(retro).IsNotNull();
        await Assert.That(retro!.Suggestions.Count).IsEqualTo(3);
        await Assert.That(retro!.Suggestions[0].Audience).IsEqualTo("human"); // legacy
        await Assert.That(retro!.Suggestions[1].Audience).IsEqualTo("agent"); // tagged
        await Assert.That(retro!.Suggestions[2].Audience).IsEqualTo("human"); // unknown → human
    }
}
