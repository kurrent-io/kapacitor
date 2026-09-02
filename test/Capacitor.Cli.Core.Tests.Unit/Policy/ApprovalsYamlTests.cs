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
}
