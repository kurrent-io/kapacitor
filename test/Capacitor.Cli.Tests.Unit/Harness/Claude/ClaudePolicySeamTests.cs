namespace Capacitor.Cli.Tests.Unit.Harness.Claude;

using System.Text.Json.Nodes;
using Capacitor.Cli.Core.Policy;
using Capacitor.Cli.Harness.Claude;
using Capacitor.Cli.Tests.Unit.Policy;

public class ClaudePolicySeamTests {
    [TempDir] public required TempDir Tmp { get; init; }
    [TempConfigRoot] public required TempConfigRoot Config { get; init; }

    const string Sid = "9dc2775376454e4691ecc2d69973c152";

    ClaudePolicySeam Seam => new(Config.Root);

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

    List<JsonNode> Decisions() => SpooledPolicyEvents.Decisions(Config.Root, Sid);

    static string HashOf(string toolInputJson) => PolicyInputHash.Compute("Bash",
        System.Text.Json.JsonDocument.Parse(toolInputJson).RootElement.Clone());

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

        var events = Decisions();
        await Assert.That(events.Count).IsEqualTo(1);
        await Assert.That(events[0]["requested_outcome"]!.GetValue<string>()).IsEqualTo("deny");
        await Assert.That(events[0]["effective_outcome"]!.GetValue<string>()).IsEqualTo("deny");
        await Assert.That(events[0]["seam"]!.GetValue<string>()).IsEqualTo("claude_pre_tool_use");
        // The snapshot the decision names must reach the server too, or the id is unresolvable.
        await Assert.That(SpooledPolicyEvents.Snapshots(Config.Root, Sid).Count).IsEqualTo(1);
    }

    /// <summary>Without a vendor call id no terminal may be journaled: an entry parked under the
    /// ask-only fallback would be unreachable to every later <c>Consume</c>.</summary>
    [Test]
    public async Task Deny_without_a_call_id_journals_no_terminal() {
        WriteUserPolicy("version: 1\nrules:\n  - match: { kind: shell, command: \"git push --force*\" }\n    outcome: deny\n");
        await Seam.HandlePreToolUseAsync(
            Body("Bash", """{"command":"git push --force"}"""), Sid, false, new StringWriter());
        var consumed = new PolicyDecisionJournal(Config.Root)
            .Consume(Sid, null, HashOf("""{"command":"git push --force"}"""));
        await Assert.That(consumed.ExactOutcome).IsNull();
        await Assert.That(consumed.PendingAsk).IsFalse();
    }

    [Test]
    public async Task Fully_covered_allow_answers_allow() {
        WriteUserPolicy("version: 1\nrules:\n  - match: { kind: shell, command: \"git status *\" }\n    outcome: allow\n");
        var stdout = new StringWriter();
        await Seam.HandlePreToolUseAsync(Body("Bash", """{"command":"git status"}"""), Sid, false, stdout);
        var hso = JsonNode.Parse(stdout.ToString())!["hookSpecificOutput"]!;
        await Assert.That(hso["permissionDecision"]!.GetValue<string>()).IsEqualTo("allow");

        var events = Decisions();
        await Assert.That(events.Count).IsEqualTo(1);
        await Assert.That(events[0]["requested_outcome"]!.GetValue<string>()).IsEqualTo("allow");
        await Assert.That(events[0]["effective_outcome"]!.GetValue<string>()).IsEqualTo("allow");
        await Assert.That(events[0]["evaluation_mode"]!.GetValue<string>()).IsEqualTo("full");
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
        var consumed = new PolicyDecisionJournal(Config.Root)
            .Consume(Sid, null, HashOf("""{"command":"gh pr merge"}"""));
        await Assert.That(consumed.PendingAsk).IsTrue();
    }

    /// <summary>With a call id the ask goes to the exact lane instead of the FIFO one, so the later
    /// seam correlates it without the ambiguity a hash-only match carries.</summary>
    [Test]
    public async Task Ask_with_a_call_id_journals_the_exact_lane() {
        WriteUserPolicy("version: 1\nrules:\n  - match: { kind: shell, command: \"gh pr merge\" }\n    outcome: ask\n");
        await Seam.HandlePreToolUseAsync(
            Body("Bash", """{"command":"gh pr merge"}""", callId: "toolu_02Y"), Sid, false, new StringWriter());
        var consumed = new PolicyDecisionJournal(Config.Root)
            .Consume(Sid, "toolu_02Y", HashOf("""{"command":"gh pr merge"}"""));
        await Assert.That(consumed.PendingAsk).IsTrue();
        await Assert.That(consumed.Ambiguous).IsFalse();
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
        // Nothing was decided, so nothing is recorded: the daemon owns the rendered session's full
        // evaluation, and a pass-through counted here would double-count it.
        await Assert.That(Decisions()).IsEmpty();
        await Assert.That(new PolicyDecisionJournal(Config.Root).TakePassThroughCount(Sid)).IsEqualTo(0);

        var denied = new StringWriter();
        await Seam.HandlePreToolUseAsync(Body("Bash", """{"command":"git push --force"}"""), Sid, renderedAgent: true, denied);
        await Assert.That(denied.ToString()).Contains("\"deny\"");            // deny still bites
        await Assert.That(Decisions().Count).IsEqualTo(1);
    }

    [Test]
    public async Task No_policy_files_means_zero_output_and_zero_events() {
        var stdout = new StringWriter();
        await Seam.HandlePreToolUseAsync(Body("Bash", """{"command":"anything"}"""), Sid, false, stdout);
        await Assert.That(stdout.ToString()).IsEmpty();
        await Assert.That(Decisions()).IsEmpty();
        await Assert.That(SpooledPolicyEvents.Snapshots(Config.Root, Sid)).IsEmpty();
    }

    [Test]
    public async Task Unmatched_action_counts_a_pass_through() {
        WriteUserPolicy("version: 1\nrules:\n  - match: { kind: shell, command: \"git push --force*\" }\n    outcome: deny\n");
        var stdout = new StringWriter();
        await Seam.HandlePreToolUseAsync(Body("Bash", """{"command":"cargo build"}"""), Sid, false, stdout);
        await Assert.That(stdout.ToString()).IsEmpty();
        await Assert.That(Decisions()).IsEmpty();
        await Assert.That(new PolicyDecisionJournal(Config.Root).TakePassThroughCount(Sid)).IsEqualTo(1);
    }

    [Test]
    public async Task Call_id_journals_terminal_decisions_exactly() {
        WriteUserPolicy("version: 1\nrules:\n  - match: { kind: shell, command: \"git push --force*\" }\n    outcome: deny\n");
        await Seam.HandlePreToolUseAsync(
            Body("Bash", """{"command":"git push --force"}""", callId: "toolu_01X"), Sid, false, new StringWriter());
        var consumed = new PolicyDecisionJournal(Config.Root)
            .Consume(Sid, "toolu_01X", HashOf("""{"command":"git push --force"}"""));
        await Assert.That(consumed.ExactOutcome).IsEqualTo("deny");
        await Assert.That(consumed.Ambiguous).IsFalse();
    }
}
