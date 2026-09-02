namespace Capacitor.Cli.Tests.Unit.Harness.Claude;

using System.Text.Json.Nodes;
using Capacitor.Cli.Core.Config;
using Capacitor.Cli.Core.Policy;
using Capacitor.Cli.Harness.Claude;
using WireMock.RequestBuilders;
using WireMock.ResponseBuilders;
using WireMock.Server;

public class ClaudePolicySeamTests : IDisposable {
    [TempDir] public required TempDir Tmp { get; init; }
    [TempConfigRoot] public required TempConfigRoot Config { get; init; }
    readonly WireMockServer _server = WireMockServer.Start();
    public void Dispose() => _server.Stop();

    const string Sid = "9dc2775376454e4691ecc2d69973c152";

    ClaudePolicySeam Seam => new(Config.Root, Resolutions.Of(new Profile(), serverUrl: _server.Url!));

    string Body(string toolName, string toolInputJson, string? callId = null) {
        var repo = Tmp.PathTo("repo");
        var node = new JsonObject {
            ["hook_event_name"] = "PreToolUse", ["session_id"] = Sid,
            ["tool_name"] = toolName, ["tool_input"] = JsonNode.Parse(toolInputJson),
            ["cwd"] = repo,
        };
        if (callId is not null) node["tool_use_id"] = callId;
        return node.ToJsonString();
    }

    void WriteUserPolicy(string yaml) => File.WriteAllText(Config.Root.Path("approvals.yaml"), yaml);

    [Before(Test)]
    public void Ok200() =>
        _server.Given(Request.Create().UsingPost()).RespondWith(Response.Create().WithStatusCode(200));

    [Test]
    public async Task Deny_rule_answers_deny_with_the_reason() {
        WriteUserPolicy("version: 1\nrules:\n  - match: { kind: shell, command: \"git push --force*\" }\n    outcome: deny\n    reason: use the PR lane\n");
        var stdout = new StringWriter();
        var exit = await Seam.HandlePreToolUseAsync(
            Body("Bash", """{"command":"git push --force"}"""), Sid, renderedAgent: false, stdout);
        await Assert.That(exit).IsEqualTo(0);
        var output = JsonNode.Parse(stdout.ToString())!;
        var hso = output["hookSpecificOutput"]!;
        await Assert.That(hso["hookEventName"]!.GetValue<string>()).IsEqualTo("PreToolUse");
        await Assert.That(hso["permissionDecision"]!.GetValue<string>()).IsEqualTo("deny");
        await Assert.That(hso["permissionDecisionReason"]!.GetValue<string>()).IsEqualTo("use the PR lane");
        var events = _server.FindLogEntries(Request.Create().WithPath("/hooks/policy-decision").UsingPost());
        await Assert.That(events.Count).IsEqualTo(1);
        var evt = JsonNode.Parse(events[0].RequestMessage.Body!)!;
        await Assert.That(evt["requested_outcome"]!.GetValue<string>()).IsEqualTo("deny");
        await Assert.That(evt["effective_outcome"]!.GetValue<string>()).IsEqualTo("deny");
        await Assert.That(evt["seam"]!.GetValue<string>()).IsEqualTo("claude_pre_tool_use");
    }

    [Test]
    public async Task Fully_covered_allow_answers_allow() {
        WriteUserPolicy("version: 1\nrules:\n  - match: { kind: shell, command: \"git status *\" }\n    outcome: allow\n");
        var stdout = new StringWriter();
        await Seam.HandlePreToolUseAsync(Body("Bash", """{"command":"git status"}"""), Sid, false, stdout);
        var hso = JsonNode.Parse(stdout.ToString())!["hookSpecificOutput"]!;
        await Assert.That(hso["permissionDecision"]!.GetValue<string>()).IsEqualTo("allow");
    }

    [Test]
    public async Task Redirection_evades_allow_but_stays_silent_not_denied() {
        WriteUserPolicy("version: 1\nrules:\n  - match: { kind: shell, command: \"git status *\" }\n    outcome: allow\n");
        var stdout = new StringWriter();
        await Seam.HandlePreToolUseAsync(Body("Bash", """{"command":"git status > pwn.yml"}"""), Sid, false, stdout);
        await Assert.That(stdout.ToString()).IsEmpty();
    }

    [Test]
    public async Task Ask_rule_forces_the_prompt_and_journals_a_pending_ask() {
        WriteUserPolicy("version: 1\nrules:\n  - match: { kind: shell, command: \"gh pr merge\" }\n    outcome: ask\n");
        var stdout = new StringWriter();
        await Seam.HandlePreToolUseAsync(Body("Bash", """{"command":"gh pr merge"}"""), Sid, false, stdout);
        var hso = JsonNode.Parse(stdout.ToString())!["hookSpecificOutput"]!;
        await Assert.That(hso["permissionDecision"]!.GetValue<string>()).IsEqualTo("ask");
        var hash = PolicyInputHash.Compute("Bash",
            System.Text.Json.JsonDocument.Parse("""{"command":"gh pr merge"}""").RootElement.Clone());
        var consumed = new PolicyDecisionJournal(Config.Root).Consume(Sid, null, hash);
        await Assert.That(consumed.PendingAsk).IsTrue();
    }

    [Test]
    public async Task Rendered_session_is_tighten_only() {
        WriteUserPolicy("""
            version: 1
            rules:
              - match: { kind: shell, command: "git status *" }
                outcome: allow
              - match: { kind: shell, command: "git push --force*" }
                outcome: deny
            """);
        var allowed = new StringWriter();
        await Seam.HandlePreToolUseAsync(Body("Bash", """{"command":"git status"}"""), Sid, renderedAgent: true, allowed);
        await Assert.That(allowed.ToString()).IsEmpty();                      // no allow is ever computed
        var denied = new StringWriter();
        await Seam.HandlePreToolUseAsync(Body("Bash", """{"command":"git push --force"}"""), Sid, renderedAgent: true, denied);
        await Assert.That(denied.ToString()).Contains("\"deny\"");            // deny still bites
    }

    [Test]
    public async Task No_policy_files_means_zero_output_and_zero_events() {
        var stdout = new StringWriter();
        await Seam.HandlePreToolUseAsync(Body("Bash", """{"command":"anything"}"""), Sid, false, stdout);
        await Assert.That(stdout.ToString()).IsEmpty();
        var events = _server.FindLogEntries(Request.Create().WithPath("/hooks/policy-decision").UsingPost());
        await Assert.That(events.Count).IsEqualTo(0);
    }

    [Test]
    public async Task Unmatched_action_counts_a_pass_through() {
        WriteUserPolicy("version: 1\nrules:\n  - match: { kind: shell, command: \"git push --force*\" }\n    outcome: deny\n");
        var stdout = new StringWriter();
        await Seam.HandlePreToolUseAsync(Body("Bash", """{"command":"cargo build"}"""), Sid, false, stdout);
        await Assert.That(stdout.ToString()).IsEmpty();
        await Assert.That(new PolicyDecisionJournal(Config.Root).TakePassThroughCount(Sid)).IsEqualTo(1);
    }

    [Test]
    public async Task Call_id_journals_terminal_decisions_exactly() {
        WriteUserPolicy("version: 1\nrules:\n  - match: { kind: shell, command: \"git push --force*\" }\n    outcome: deny\n");
        await Seam.HandlePreToolUseAsync(
            Body("Bash", """{"command":"git push --force"}""", callId: "toolu_01X"), Sid, false, new StringWriter());
        var hash = PolicyInputHash.Compute("Bash",
            System.Text.Json.JsonDocument.Parse("""{"command":"git push --force"}""").RootElement.Clone());
        var consumed = new PolicyDecisionJournal(Config.Root).Consume(Sid, "toolu_01X", hash);
        await Assert.That(consumed.ExactOutcome).IsEqualTo("deny");
        await Assert.That(consumed.Ambiguous).IsFalse();
    }
}
