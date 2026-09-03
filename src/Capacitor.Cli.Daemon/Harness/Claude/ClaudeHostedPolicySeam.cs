namespace Capacitor.Cli.Daemon.Harness.Claude;

using System.Text.Json;
using Capacitor.Cli.Core.Harness.Claude;
using Capacitor.Cli.Core.Policy;

/// <summary>What the session's policy made of one hosted permission request: the outcome the caller
/// acts on, and the event that records it.</summary>
internal sealed record ClaudeHostedPolicyResult(PolicyOutcome Outcome, PolicyDecisionEventV1 Event);

/// <summary>
/// The Claude half of the hosted permission seam: maps the vendor's tool call onto the policy
/// vocabulary, judges it against the launch-bound snapshot, and builds the decision event. Null when
/// no rule answered — that request belongs to the human lane and nothing is recorded against it.
/// </summary>
internal static class ClaudeHostedPolicySeam {
    internal static ClaudeHostedPolicyResult? Evaluate(
            string sessionId, string agentId, PolicySnapshot snapshot,
            string? toolName, JsonElement? toolInput, string? cwd) {
        var action     = ClaudeActionNormalizer.Normalize(toolName, toolInput, cwd);
        var evaluation = PolicyEngine.Evaluate(snapshot, action, EvaluationMode.Full);

        // An ask raises the prompt it would have asked for anyway, so what it effects is a park.
        return evaluation.Outcome switch {
            PolicyOutcome.Allow => Result(PolicyOutcome.Allow, "allow", "allow"),
            PolicyOutcome.Deny  => Result(PolicyOutcome.Deny,  "deny",  "deny"),
            PolicyOutcome.Ask   => Result(PolicyOutcome.Ask,   "ask",   "parked"),
            _ => null,
        };

        // One evaluation per raised prompt, so there is nothing to correlate a decision against and
        // nothing ambiguous about which call it answers.
        ClaudeHostedPolicyResult Result(PolicyOutcome outcome, string requested, string effective) =>
            new(outcome, new PolicyDecisionEventV1(
                sessionId, agentId, "claude", PolicySeams.HostedClaudePermission, snapshot.Id,
                PolicyEngine.Version, "full", requested, effective, PolicyWire.ToWire(action),
                PolicyWire.ToWire(evaluation.MatchedRules), snapshot.Degraded, null, null, false,
                DateTimeOffset.UtcNow.ToString("O")));
    }
}
