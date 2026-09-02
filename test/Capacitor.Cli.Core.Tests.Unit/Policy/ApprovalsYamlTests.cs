namespace Capacitor.Cli.Core.Tests.Unit.Policy;

using Capacitor.Cli.Core.Policy;

public class ApprovalsYamlTests {
    // The spec's own example document must parse: it is the reference input for the binder.
    const string SpecExample = """
        version: 1
        rules:
          - match: { kind: shell, command: "git push --force*" }
            outcome: deny
            reason: force-push goes through the PR lane
          - match: { kind: shell, command: ["git status *", "git diff *", "dotnet build *"] }
            outcome: allow           # the trailing * is the visible opt-in to arbitrary extra argv
          - match: { kind: mcp_tool, server: "kcap-*" }
            outcome: allow
          - match: { kind: shell, command: "gh pr merge" }
            outcome: ask
        judge:
          mode: unmatched
          prompt: |
            Approve routine read-only git and build commands anywhere in the repo.
            Escalate anything touching CI config or release tags to ask.
        """;

    [Test]
    public async Task Spec_example_parses() {
        var root = ApprovalsYaml.Parse(SpecExample);
        await Assert.That(((YamlScalar)root["version"]!).Value).IsEqualTo("1");
        var rules = (YamlSequence)root["rules"]!;
        await Assert.That(rules.Items.Count).IsEqualTo(4);
        var first = (YamlMapping)rules.Items[0];
        var match = (YamlMapping)first["match"]!;
        await Assert.That(((YamlScalar)match["kind"]!).Value).IsEqualTo("shell");
        await Assert.That(((YamlScalar)match["command"]!).Value).IsEqualTo("git push --force*");
        await Assert.That(((YamlScalar)first["outcome"]!).Value).IsEqualTo("deny");
        var second = (YamlMapping)rules.Items[1];
        var patterns = (YamlSequence)((YamlMapping)second["match"]!)["command"]!;
        await Assert.That(patterns.Items.Count).IsEqualTo(3);
        await Assert.That(((YamlScalar)patterns.Items[1]).Value).IsEqualTo("git diff *");
        var judge = (YamlMapping)root["judge"]!;
        await Assert.That(((YamlScalar)judge["prompt"]!).Value)
            .Contains("Escalate anything touching CI config");
    }

    [Test]
    public async Task Literal_block_preserves_lines_and_dedents() {
        var root = ApprovalsYaml.Parse("judge:\n  prompt: |\n    line one\n    line two\n");
        var prompt = ((YamlScalar)((YamlMapping)root["judge"]!)["prompt"]!).Value;
        await Assert.That(prompt).IsEqualTo("line one\nline two\n");
    }

    [Test]
    public async Task Quoted_scalars_resolve_escapes() {
        var root = ApprovalsYaml.Parse("a: 'it''s'\nb: \"x\\\"y\"\n");
        await Assert.That(((YamlScalar)root["a"]!).Value).IsEqualTo("it's");
        await Assert.That(((YamlScalar)root["b"]!).Value).IsEqualTo("x\"y");
    }

    [Test]
    [Arguments("version: 1\nrules: *anchor\n", "alias")]
    [Arguments("---\nversion: 1\n", "document")]
    [Arguments("a: !!str x\n", "tag")]
    [Arguments("a: >\n  folded\n", "folded")]
    [Arguments("a: 1\na: 2\n", "duplicate")]
    [Arguments("\ta: 1\n", "tab")]
    [Arguments("a: 'unterminated\n", "quote")]
    [Arguments("a:\n", "value")]
    public async Task Unsupported_constructs_throw_with_a_line_number(string text, string _) {
        var ex = Assert.Throws<ApprovalsYamlException>(() => ApprovalsYaml.Parse(text));
        await Assert.That(ex.Line).IsGreaterThan(0);
    }

    [Test]
    public async Task Comments_and_blank_lines_are_ignored() {
        var root = ApprovalsYaml.Parse("# header\n\nversion: 1   # trailing\n");
        await Assert.That(((YamlScalar)root["version"]!).Value).IsEqualTo("1");
    }

    [Test]
    public async Task Hash_inside_quotes_is_not_a_comment() {
        var root = ApprovalsYaml.Parse("a: \"x # y\"\n");
        await Assert.That(((YamlScalar)root["a"]!).Value).IsEqualTo("x # y");
    }

    [Test]
    public async Task Literal_block_stops_before_an_under_indented_comment() {
        var text = "judge:\n  prompt: |\n    line one\n   # TODO\n    line two\n";
        var ex = Assert.Throws<ApprovalsYamlException>(() => ApprovalsYaml.Parse(text));
        await Assert.That(ex.Line).IsEqualTo(4);
    }

    [Test]
    public async Task Literal_block_stops_before_an_under_indented_sibling() {
        var text = "judge:\n  prompt: |\n    body text\n   mode: unmatched\n";
        var ex = Assert.Throws<ApprovalsYamlException>(() => ApprovalsYaml.Parse(text));
        await Assert.That(ex.Line).IsGreaterThan(0);
    }

    [Test]
    public async Task Literal_block_preserves_comment_like_lines_verbatim() {
        var root = ApprovalsYaml.Parse("prompt: |\n  first\n  # heading\n  more # not a comment\n");
        var value = ((YamlScalar)root["prompt"]!).Value;
        await Assert.That(value).Contains("# heading");
        await Assert.That(value).Contains("more # not a comment");
    }

    [Test]
    public async Task Literal_block_preserves_a_document_marker_line() {
        var root = ApprovalsYaml.Parse("prompt: |\n  first\n  ---\n  second\n");
        var value = ((YamlScalar)root["prompt"]!).Value;
        await Assert.That(value).Contains("---");
    }

    [Test]
    public async Task Apostrophe_mid_word_is_plain_text() {
        var root = ApprovalsYaml.Parse("reason: don't force-push\n");
        await Assert.That(((YamlScalar)root["reason"]!).Value).IsEqualTo("don't force-push");
    }

    [Test]
    public async Task Comma_in_block_context_is_plain_text() {
        var root = ApprovalsYaml.Parse("a: allow git, gh and dotnet\n");
        await Assert.That(((YamlScalar)root["a"]!).Value).IsEqualTo("allow git, gh and dotnet");
    }

    [Test]
    public async Task Mid_scalar_colon_stays_legal() {
        var root = ApprovalsYaml.Parse("reason: use x: y\n");
        await Assert.That(((YamlScalar)root["reason"]!).Value).IsEqualTo("use x: y");
    }

    [Test]
    public async Task Flow_mapping_still_splits_on_comma() {
        var root = ApprovalsYaml.Parse("m: { a: x, b: y }\n");
        var m = (YamlMapping)root["m"]!;
        await Assert.That(((YamlScalar)m["a"]!).Value).IsEqualTo("x");
        await Assert.That(((YamlScalar)m["b"]!).Value).IsEqualTo("y");
    }

    [Test]
    [Arguments("list:\n  - - a\n")]
    [Arguments("a: : x\n")]
    public async Task Ambiguous_leading_dash_or_colon_throws(string text) {
        var ex = Assert.Throws<ApprovalsYamlException>(() => ApprovalsYaml.Parse(text));
        await Assert.That(ex.Line).IsGreaterThan(0);
    }

    [Test]
    public async Task Flow_nesting_beyond_depth_cap_throws() {
        var nested = new string('[', 40) + new string(']', 40);
        var ex = Assert.Throws<ApprovalsYamlException>(() => ApprovalsYaml.Parse($"a: {nested}\n"));
        await Assert.That(ex.Line).IsEqualTo(1);
    }

    [Test]
    public async Task Block_nesting_beyond_depth_cap_throws() {
        var nested = string.Concat(Enumerable.Range(0, 100).Select(i => new string(' ', 2 * i) + "a:\n"))
            + new string(' ', 200) + "b: 1\n";
        Assert.Throws<ApprovalsYamlException>(() => ApprovalsYaml.Parse(nested));
        await Task.CompletedTask;
    }

    [Test]
    public async Task Chomped_literal_block_has_no_trailing_newline() {
        var root = ApprovalsYaml.Parse("a: |-\n  line one\n  line two\n");
        await Assert.That(((YamlScalar)root["a"]!).Value).IsEqualTo("line one\nline two");
    }

    [Test]
    public async Task Quoted_flag_reflects_quoting() {
        var root = ApprovalsYaml.Parse("a: 1\nb: \"1\"\n");
        await Assert.That(((YamlScalar)root["a"]!).Quoted).IsFalse();
        await Assert.That(((YamlScalar)root["b"]!).Quoted).IsTrue();
    }

    [Test]
    public async Task Alias_failure_reports_its_line() {
        var ex = Assert.Throws<ApprovalsYamlException>(
            () => ApprovalsYaml.Parse("version: 1\nrules: *anchor\n"));
        await Assert.That(ex.Line).IsEqualTo(2);
    }
}
