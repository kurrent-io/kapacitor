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

    /// <summary>A wrong-typed optional field cannot suppress a matching deny: each field is read on
    /// its own and degrades to null alone.</summary>
    [Test]
    public async Task A_wrong_typed_agent_id_still_answers_the_deny() {
        WriteUserPolicy("version: 1\nrules:\n  - match: { kind: shell, command: \"git push --force*\" }\n    outcome: deny\n");
        var node = new JsonObject {
            ["hook_event_name"] = "PreToolUse", ["session_id"] = Sid, ["agent_id"] = 42,
            ["tool_name"] = "Bash", ["tool_input"] = JsonNode.Parse("""{"command":"git push --force"}"""),
            ["cwd"] = Tmp.PathTo("repo"),
        };

        var stdout = new StringWriter();
        await Seam.HandlePreToolUseAsync(node.ToJsonString(), Sid, false, stdout);

        await Assert.That(JsonNode.Parse(stdout.ToString())!["hookSpecificOutput"]!["permissionDecision"]!
            .GetValue<string>()).IsEqualTo("deny");
        await Assert.That(Decisions().Count).IsEqualTo(1);
    }

    /// <summary>An unusable tool_input still normalizes — to an Other-kind action rules can match —
    /// rather than reading as a call no policy governs.</summary>
    [Test]
    public async Task A_non_object_tool_input_is_denied_as_other() {
        WriteUserPolicy("version: 1\nrules:\n  - match: { kind: other }\n    outcome: deny\n");
        var node = new JsonObject {
            ["hook_event_name"] = "PreToolUse", ["session_id"] = Sid,
            ["tool_name"] = "Bash", ["tool_input"] = "not an object",
            ["cwd"] = Tmp.PathTo("repo"),
        };

        var stdout = new StringWriter();
        await Seam.HandlePreToolUseAsync(node.ToJsonString(), Sid, false, stdout);

        await Assert.That(JsonNode.Parse(stdout.ToString())!["hookSpecificOutput"]!["permissionDecision"]!
            .GetValue<string>()).IsEqualTo("deny");
        await Assert.That(Decisions()[0]["action"]!["kind"]!.GetValue<string>()).IsEqualTo("other");
    }

    JsonNode PermissionNode(string toolName, string toolInputJson, string? callId = null) {
        var node = new JsonObject {
            ["hook_event_name"] = "PermissionRequest", ["session_id"] = Sid,
            ["tool_name"] = toolName, ["tool_input"] = JsonNode.Parse(toolInputJson),
            ["cwd"] = Tmp.PathTo("repo"),
        };
        if (callId is not null) node["tool_use_id"] = callId;
        return node;
    }

    [Test]
    public async Task Permission_request_deny_rule_answers_the_prompt_deny() {
        WriteUserPolicy("version: 1\nrules:\n  - match: { kind: shell, command: \"git push --force*\" }\n    outcome: deny\n");
        var stdout = new StringWriter();
        var answer = await Seam.HandlePermissionRequestAsync(
            PermissionNode("Bash", """{"command":"git push --force"}"""), Sid, stdout);
        await Assert.That(answer).IsEqualTo(SeamAnswer.Answered);
        var hso = JsonNode.Parse(stdout.ToString())!["hookSpecificOutput"]!;
        await Assert.That(hso["hookEventName"]!.GetValue<string>()).IsEqualTo("PermissionRequest");
        await Assert.That(hso["decision"]!["behavior"]!.GetValue<string>()).IsEqualTo("deny");

        var events = Decisions();
        await Assert.That(events.Count).IsEqualTo(1);
        await Assert.That(events[0]["seam"]!.GetValue<string>()).IsEqualTo("claude_permission_request");
        await Assert.That(events[0]["requested_outcome"]!.GetValue<string>()).IsEqualTo("deny");
        await Assert.That(events[0]["effective_outcome"]!.GetValue<string>()).IsEqualTo("deny");
    }

    [Test]
    public async Task Permission_request_allow_rule_answers_allow() {
        WriteUserPolicy("version: 1\nrules:\n  - match: { kind: shell, command: \"git status *\" }\n    outcome: allow\n");
        var stdout = new StringWriter();
        var answer = await Seam.HandlePermissionRequestAsync(
            PermissionNode("Bash", """{"command":"git status"}"""), Sid, stdout);
        await Assert.That(answer).IsEqualTo(SeamAnswer.Answered);
        await Assert.That(JsonNode.Parse(stdout.ToString())!["hookSpecificOutput"]!["decision"]!["behavior"]!
            .GetValue<string>()).IsEqualTo("allow");
        await Assert.That(Decisions()[0]["effective_outcome"]!.GetValue<string>()).IsEqualTo("allow");
    }

    /// <summary>A prompt the policy's own ask forced belongs to the human it was raised for, so the
    /// allow the same files would grant may not answer it.</summary>
    [Test]
    public async Task Pending_ask_suppresses_a_fresh_allow() {
        WriteUserPolicy("version: 1\nrules:\n  - match: { kind: shell, command: \"git status *\" }\n    outcome: allow\n");
        new PolicyDecisionJournal(Config.Root).RecordAsk(Sid, null, HashOf("""{"command":"git status"}"""));
        var stdout = new StringWriter();
        var answer = await Seam.HandlePermissionRequestAsync(
            PermissionNode("Bash", """{"command":"git status"}"""), Sid, stdout);
        await Assert.That(answer).IsEqualTo(SeamAnswer.NotAnswered);
        await Assert.That(stdout.ToString()).IsEmpty();

        var events = Decisions();
        await Assert.That(events.Count).IsEqualTo(1);
        await Assert.That(events[0]["requested_outcome"]!.GetValue<string>()).IsEqualTo("ask");
        await Assert.That(events[0]["effective_outcome"]!.GetValue<string>()).IsEqualTo("prompt_stands");
        await Assert.That(events[0]["correlation_ambiguous"]!.GetValue<bool>()).IsTrue();
        // Both halves, or the record cannot tell this apart from an ask the policy itself produced.
        await Assert.That(events[0]["pending_ask_consumed"]!.GetValue<bool>()).IsTrue();
        await Assert.That(events[0]["fresh_outcome"]!.GetValue<string>()).IsEqualTo("allow");
    }

    [Test]
    public async Task Pending_ask_on_the_exact_lane_is_not_ambiguous() {
        WriteUserPolicy("version: 1\nrules:\n  - match: { kind: shell, command: \"git status *\" }\n    outcome: allow\n");
        new PolicyDecisionJournal(Config.Root).RecordAsk(Sid, "toolu_03Z", HashOf("""{"command":"git status"}"""));
        var answer = await Seam.HandlePermissionRequestAsync(
            PermissionNode("Bash", """{"command":"git status"}""", callId: "toolu_03Z"), Sid, new StringWriter());
        await Assert.That(answer).IsEqualTo(SeamAnswer.NotAnswered);
        await Assert.That(Decisions()[0]["correlation_ambiguous"]!.GetValue<bool>()).IsFalse();
    }

    [Test]
    public async Task Fresh_deny_wins_even_with_a_pending_ask() {
        WriteUserPolicy("version: 1\nrules:\n  - match: { kind: shell, command: \"rm -rf*\" }\n    outcome: deny\n");
        new PolicyDecisionJournal(Config.Root).RecordAsk(Sid, null, HashOf("""{"command":"rm -rf /"}"""));
        var stdout = new StringWriter();
        var answer = await Seam.HandlePermissionRequestAsync(
            PermissionNode("Bash", """{"command":"rm -rf /"}"""), Sid, stdout);
        await Assert.That(answer).IsEqualTo(SeamAnswer.Answered);
        await Assert.That(stdout.ToString()).Contains("\"deny\"");
        var denied = Decisions()[0];
        await Assert.That(denied["effective_outcome"]!.GetValue<string>()).IsEqualTo("deny");
        await Assert.That(denied["fresh_outcome"]!.GetValue<string>()).IsEqualTo("deny");
        await Assert.That(denied["pending_ask_consumed"]!.GetValue<bool>()).IsTrue();
        // The deny subsumed the ask rather than stepping around it: leaving the entry behind would
        // let it stand against the next prompt for the same input.
        await Assert.That(new PolicyDecisionJournal(Config.Root)
            .Consume(Sid, null, HashOf("""{"command":"rm -rf /"}""")).PendingAsk).IsFalse();
    }

    /// <summary>An ask journaled for an action no rule matches still stands: the journal tightens a
    /// fresh outcome it cannot produce itself, and a governed prompt is never a pass-through.</summary>
    [Test]
    public async Task Pending_ask_stands_over_an_unmatched_action_without_counting_a_pass_through() {
        WriteUserPolicy("version: 1\nrules:\n  - match: { kind: shell, command: \"git push --force*\" }\n    outcome: deny\n");
        new PolicyDecisionJournal(Config.Root).RecordAsk(Sid, null, HashOf("""{"command":"cargo build"}"""));
        var stdout = new StringWriter();
        var answer = await Seam.HandlePermissionRequestAsync(
            PermissionNode("Bash", """{"command":"cargo build"}"""), Sid, stdout);
        await Assert.That(answer).IsEqualTo(SeamAnswer.NotAnswered);
        await Assert.That(stdout.ToString()).IsEmpty();
        await Assert.That(Decisions()[0]["effective_outcome"]!.GetValue<string>()).IsEqualTo("prompt_stands");
        await Assert.That(new PolicyDecisionJournal(Config.Root).TakePassThroughCount(Sid)).IsEqualTo(0);
    }

    [Test]
    public async Task Pending_ask_on_the_exact_lane_stands_over_an_unmatched_action() {
        WriteUserPolicy("version: 1\nrules:\n  - match: { kind: shell, command: \"git push --force*\" }\n    outcome: deny\n");
        new PolicyDecisionJournal(Config.Root).RecordAsk(Sid, "toolu_04A", HashOf("""{"command":"cargo build"}"""));
        var stdout = new StringWriter();
        var answer = await Seam.HandlePermissionRequestAsync(
            PermissionNode("Bash", """{"command":"cargo build"}""", callId: "toolu_04A"), Sid, stdout);
        await Assert.That(answer).IsEqualTo(SeamAnswer.NotAnswered);
        await Assert.That(stdout.ToString()).IsEmpty();
        var events = Decisions();
        await Assert.That(events[0]["effective_outcome"]!.GetValue<string>()).IsEqualTo("prompt_stands");
        await Assert.That(events[0]["correlation_ambiguous"]!.GetValue<bool>()).IsFalse();
        await Assert.That(new PolicyDecisionJournal(Config.Root).TakePassThroughCount(Sid)).IsEqualTo(0);
    }

    [Test]
    public async Task Fresh_ask_leaves_the_prompt_standing() {
        WriteUserPolicy("version: 1\nrules:\n  - match: { kind: shell, command: \"gh pr merge\" }\n    outcome: ask\n");
        var stdout = new StringWriter();
        var answer = await Seam.HandlePermissionRequestAsync(
            PermissionNode("Bash", """{"command":"gh pr merge"}"""), Sid, stdout);
        await Assert.That(answer).IsEqualTo(SeamAnswer.NotAnswered);
        await Assert.That(stdout.ToString()).IsEmpty();
        var events = Decisions();
        await Assert.That(events.Count).IsEqualTo(1);
        await Assert.That(events[0]["requested_outcome"]!.GetValue<string>()).IsEqualTo("ask");
        await Assert.That(events[0]["effective_outcome"]!.GetValue<string>()).IsEqualTo("prompt_stands");
    }

    /// <summary>With no guard consumed an ungoverned session costs the seam nothing at all — the
    /// counterpart to the empty snapshot that DID spend one, which records instead.</summary>
    [Test]
    public async Task Permission_request_without_a_policy_defers_to_record_only() {
        var stdout = new StringWriter();
        var answer = await Seam.HandlePermissionRequestAsync(
            PermissionNode("Bash", """{"command":"ls"}"""), Sid, stdout);
        await Assert.That(answer).IsEqualTo(SeamAnswer.NotAnswered);
        await Assert.That(stdout.ToString()).IsEmpty();
        await Assert.That(Decisions()).IsEmpty();
        await Assert.That(new PolicyDecisionJournal(Config.Root).TakePassThroughCount(Sid)).IsEqualTo(0);
    }

    /// <summary>The head entry is spent whatever the invocation goes on to decide. An evaluation
    /// that fails after the consume must still say so, or the record shows a prompt standing with
    /// nothing to explain why and the entry is gone unaccounted for.</summary>
    [Test]
    public async Task An_evaluation_error_stands_the_prompt_and_records_the_consumed_ask() {
        WriteUserPolicy("version: 1\nrules:\n  - match: { kind: shell, command: \"git status *\" }\n    outcome: allow\n");
        new PolicyDecisionJournal(Config.Root).RecordAsk(Sid, null, HashOf("""{"command":"git status"}"""));

        var seam = new ClaudePolicySeam(Config.Root) {
            BeforeSnapshotLoadForTest = () => throw new InvalidOperationException("policy store unavailable"),
        };
        var stdout = new StringWriter();
        var answer = await seam.HandlePermissionRequestAsync(
            PermissionNode("Bash", """{"command":"git status"}"""), Sid, stdout);

        await Assert.That(answer).IsEqualTo(SeamAnswer.NotAnswered);
        await Assert.That(stdout.ToString()).IsEmpty();

        var events = Decisions();
        await Assert.That(events.Count).IsEqualTo(1);
        await Assert.That(events[0]["requested_outcome"]!.GetValue<string>()).IsEqualTo("ask");
        await Assert.That(events[0]["effective_outcome"]!.GetValue<string>()).IsEqualTo("prompt_stands");
        await Assert.That(events[0]["failure_class"]!.GetValue<string>()).IsEqualTo("evaluation_error");
        await Assert.That(events[0]["fresh_outcome"]!.GetValue<string>()).IsEqualTo("error");
        await Assert.That(events[0]["pending_ask_consumed"]!.GetValue<bool>()).IsTrue();
        await Assert.That(events[0]["correlation_ambiguous"]!.GetValue<bool>()).IsTrue();
        await Assert.That(events[0]["snapshot_id"]!.GetValue<string>()).IsEqualTo("unknown");
        await Assert.That(events[0]["action"]!["kind"]!.GetValue<string>()).IsEqualTo("other");

        // Spent, not stranded: the next identical request finds nothing to hold a prompt for.
        await Assert.That(new PolicyDecisionJournal(Config.Root)
            .Consume(Sid, null, HashOf("""{"command":"git status"}""")).PendingAsk).IsFalse();
    }

    /// <summary>With no ask consumed the failure costs nothing anyone can act on, so it stays as
    /// silent as the same failure at PreToolUse.</summary>
    [Test]
    public async Task An_evaluation_error_without_a_pending_ask_records_nothing() {
        WriteUserPolicy("version: 1\nrules:\n  - match: { kind: shell, command: \"git status *\" }\n    outcome: allow\n");
        var seam = new ClaudePolicySeam(Config.Root) {
            BeforeSnapshotLoadForTest = () => throw new InvalidOperationException("policy store unavailable"),
        };

        var answer = await seam.HandlePermissionRequestAsync(
            PermissionNode("Bash", """{"command":"git status"}"""), Sid, new StringWriter());

        await Assert.That(answer).IsEqualTo(SeamAnswer.NotAnswered);
        await Assert.That(Decisions()).IsEmpty();
    }

    /// <summary>Consume runs ahead of the snapshot, so a session whose policy files are gone still
    /// spends the entry an earlier policy journaled — and an entry spent is an entry the record has
    /// to account for. An empty policy is a fresh <c>none</c>, which cannot outrank the ask, so the
    /// prompt stands exactly as it does over any other unmatched action.</summary>
    [Test]
    public async Task An_empty_snapshot_records_the_consumed_ask_it_spent() {
        new PolicyDecisionJournal(Config.Root).RecordAsk(Sid, null, HashOf("""{"command":"git status"}"""));

        var stdout = new StringWriter();
        var answer = await Seam.HandlePermissionRequestAsync(
            PermissionNode("Bash", """{"command":"git status"}"""), Sid, stdout);

        await Assert.That(answer).IsEqualTo(SeamAnswer.NotAnswered);
        await Assert.That(stdout.ToString()).IsEmpty();

        var events = Decisions();
        await Assert.That(events.Count).IsEqualTo(1);
        await Assert.That(events[0]["requested_outcome"]!.GetValue<string>()).IsEqualTo("ask");
        await Assert.That(events[0]["effective_outcome"]!.GetValue<string>()).IsEqualTo("prompt_stands");
        await Assert.That(events[0]["pending_ask_consumed"]!.GetValue<bool>()).IsTrue();
        await Assert.That(events[0]["fresh_outcome"]!.GetValue<string>()).IsEqualTo("none");
        await Assert.That(events[0]["correlation_ambiguous"]!.GetValue<bool>()).IsTrue();
        // The empty snapshot's own id, not the evaluation-error placeholder: nothing failed here.
        await Assert.That(events[0]["snapshot_id"]!.GetValue<string>())
            .IsEqualTo(new PolicySnapshotStore(Config.Root).TryLoad(Sid)!.Id);
        // A governed prompt is never a pass-through, empty policy or not.
        await Assert.That(new PolicyDecisionJournal(Config.Root).TakePassThroughCount(Sid)).IsEqualTo(0);

        await Assert.That(new PolicyDecisionJournal(Config.Root)
            .Consume(Sid, null, HashOf("""{"command":"git status"}""")).PendingAsk).IsFalse();
    }

    [Test]
    public async Task Permission_request_unmatched_action_counts_a_pass_through() {
        WriteUserPolicy("version: 1\nrules:\n  - match: { kind: shell, command: \"git push --force*\" }\n    outcome: deny\n");
        var stdout = new StringWriter();
        var answer = await Seam.HandlePermissionRequestAsync(
            PermissionNode("Bash", """{"command":"cargo build"}"""), Sid, stdout);
        await Assert.That(answer).IsEqualTo(SeamAnswer.NotAnswered);
        await Assert.That(stdout.ToString()).IsEmpty();
        await Assert.That(Decisions()).IsEmpty();
        await Assert.That(new PolicyDecisionJournal(Config.Root).TakePassThroughCount(Sid)).IsEqualTo(1);
    }
}
