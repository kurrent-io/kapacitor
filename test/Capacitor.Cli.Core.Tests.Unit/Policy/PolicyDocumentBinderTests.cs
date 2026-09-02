namespace Capacitor.Cli.Core.Tests.Unit.Policy;

using Capacitor.Cli.Core.Policy;

public class PolicyDocumentBinderTests {
    const string SpecExample = """
        version: 1
        rules:
          - match: { kind: shell, command: "git push --force*" }
            outcome: deny
            reason: force-push goes through the PR lane
          - match: { kind: shell, command: ["git status *", "git diff *", "dotnet build *"] }
            outcome: allow
          - match: { kind: mcp_tool, server: "kcap-*" }
            outcome: allow
          - match: { kind: shell, command: "gh pr merge" }
            outcome: ask
        judge:
          mode: unmatched
          prompt: |
            Approve routine read-only git and build commands anywhere in the repo.
        """;

    [Test]
    public async Task Spec_example_binds() {
        var doc = PolicyDocumentBinder.Bind(SpecExample, PolicyScope.Repo);
        await Assert.That(doc.Version).IsEqualTo(1);
        await Assert.That(doc.Rules.Count).IsEqualTo(4);
        await Assert.That(doc.Rules[0].Outcome).IsEqualTo(RuleOutcome.Deny);
        await Assert.That(doc.Rules[0].Match.Command).IsEquivalentTo(new[] { "git push --force*" });
        await Assert.That(doc.Rules[0].Reason).IsEqualTo("force-push goes through the PR lane");
        await Assert.That(doc.Rules[1].Match.Command.Count).IsEqualTo(3);
        await Assert.That(doc.Rules[2].Match.Kind).IsEqualTo(ActionKind.McpTool);
        await Assert.That(doc.Judge!.Mode).IsEqualTo("unmatched");
    }

    [Test]
    public async Task Exact_flag_and_scalar_or_list_fields_bind() {
        var doc = PolicyDocumentBinder.Bind("""
            version: 1
            rules:
              - match: { kind: shell, command: "gh pr merge", exact: true }
                outcome: ask
              - match: { kind: file_edit, path: ["/etc/*", "*.pem"] }
                outcome: deny
              - match: { kind: network, host: "*.internal.example", port: 443 }
                outcome: ask
              - match: { kind: other, tool: "TodoWrite" }
                outcome: allow
            """, PolicyScope.User);
        await Assert.That(doc.Rules[0].Match.Exact).IsTrue();
        await Assert.That(doc.Rules[1].Match.Path.Count).IsEqualTo(2);
        await Assert.That(doc.Rules[2].Match.Port).IsEqualTo(443);
        await Assert.That(doc.Rules[3].Match.Tool).IsEquivalentTo(new[] { "TodoWrite" });
    }

    [Test]
    public async Task Kind_only_matcher_is_legal() {
        var doc = PolicyDocumentBinder.Bind("version: 1\nrules:\n  - match: { kind: shell }\n    outcome: ask\n", PolicyScope.User);
        await Assert.That(doc.Rules[0].Match.Command).IsEmpty();
    }

    [Test]
    [Arguments("version: 2\nrules: []\n", "version")]
    [Arguments("version: 1\nruels: []\n", "unknown key")]
    [Arguments("version: 1\nrules:\n  - match: { kind: shell }\n    outcome: maybe\n", "outcome")]
    [Arguments("version: 1\nrules:\n  - match: { kind: teleport }\n    outcome: ask\n", "kind")]
    [Arguments("version: 1\nrules:\n  - match: { kind: shell, path: \"/x\" }\n    outcome: ask\n", "field for kind")]
    [Arguments("version: 1\nrules:\n  - match: { kind: shell, command: \"\" }\n    outcome: ask\n", "empty pattern")]
    [Arguments("version: 1\nrules:\n  - outcome: ask\n", "missing match")]
    [Arguments("version: 1\nrules:\n  - match: { kind: shell }\n", "missing outcome")]
    [Arguments("version: 1\nrules: []\ncaps: { narrower_widening: off }\n", "server-scope field")]
    [Arguments("version: 1\nrules: []\nenforcement: strict\n", "server-scope field")]
    [Arguments("version: 1\nrules: []\njudge: { mode: always }\n", "judge mode")]
    [Arguments("not yaml: [unterminated\n", "yaml error")]
    public async Task Invalid_documents_throw(string yaml, string _) {
        var ex = Assert.Throws<PolicyDocumentException>(
            () => PolicyDocumentBinder.Bind(yaml, PolicyScope.Repo));
        await Assert.That(ex.Message).IsNotEmpty();
    }

    [Test]
    public async Task Limits_are_enforced() {
        var many = string.Join("", Enumerable.Range(0, 501).Select(i =>
            $"  - match: {{ kind: shell, command: \"cmd{i}\" }}\n    outcome: ask\n"));
        Assert.Throws<PolicyDocumentException>(
            () => PolicyDocumentBinder.Bind($"version: 1\nrules:\n{many}", PolicyScope.User));
        var patterns = string.Join(", ", Enumerable.Range(0, 33).Select(i => $"\"p{i}\""));
        Assert.Throws<PolicyDocumentException>(
            () => PolicyDocumentBinder.Bind(
                $"version: 1\nrules:\n  - match: {{ kind: shell, command: [{patterns}] }}\n    outcome: ask\n",
                PolicyScope.User));
        await Task.CompletedTask;
    }
}
