namespace Capacitor.Cli.Harness.Claude;

using System.Text.Json;
using System.Text.Json.Nodes;
using Capacitor.Cli.Core;
using Capacitor.Cli.Core.Config;
using Capacitor.Cli.Core.Harness.Claude;
using Capacitor.Cli.Core.Policy;
using Capacitor.Cli.Policy;

/// <summary>
/// Evaluates a Claude tool call against the session's approval policy and answers the PreToolUse
/// hook. Fail-open throughout: an unparseable payload, an unusable field or an ungoverned session
/// exits 0 with no output, because any non-zero exit renders Claude's opaque hook-error banner.
/// </summary>
internal sealed class ClaudePolicySeam(ConfigRoot config, ProfileContext profiles) {
    /// <summary>False degrades a policy ask to pass-through: nothing is written to Claude, and the
    /// decision event records requested=ask against effective=pass_through so the gap stays
    /// visible rather than silent.</summary>
    internal const bool PreToolUseAskEnabled = true;

    const string DefaultReason = "kcap approval policy";

    public async Task<int> HandlePreToolUseAsync(string body, string sessionId, bool renderedAgent, TextWriter stdout) {
        JsonNode? node;
        string? toolName, callId, cwd;
        JsonElement? toolInput;
        try {
            node = JsonNode.Parse(body);
            if (node is null) return 0;
            toolName = node["tool_name"]?.GetValue<string>();
            callId = node["tool_use_id"]?.GetValue<string>();
            cwd = node["cwd"]?.GetValue<string>();
            toolInput = node["tool_input"] is { } ti
                ? JsonDocument.Parse(ti.ToJsonString()).RootElement.Clone()
                : null;
        } catch { return 0; }

        var snapshot = new PolicySnapshotStore(config)
            .LoadOrBuild(sessionId, cwd is null ? null : GitRepository.FindRoot(cwd));
        // A session with no policy is ungoverned, not "pass-through": no output, no counter, no
        // event, no network — the no-policy world pays nothing for the seam being installed.
        if (snapshot.IsEmpty) return 0;

        var journal = new PolicyDecisionJournal(config);
        var action = ClaudeActionNormalizer.Normalize(toolName, toolInput, cwd);
        var mode = renderedAgent ? EvaluationMode.TightenOnly : EvaluationMode.Full;
        var eval = PolicyEngine.Evaluate(snapshot, action, mode);
        var inputHash = PolicyInputHash.Compute(toolName, toolInput);
        var reason = (eval.MatchedRules.Count > 0 ? eval.MatchedRules[0].Reason : null) ?? DefaultReason;

        // stdout before the emit: Claude reads it once the process exits, while the emit is bounded
        // only by the poster's spool fallback.
        switch (eval.Outcome) {
            case PolicyOutcome.Deny:
                stdout.Write(BuildPreToolUseDecision("deny", reason));
                if (callId is { Length: > 0 }) journal.RecordTerminal(sessionId, callId, "deny", inputHash);
                await Emit(eval, "deny", "deny");
                break;
            case PolicyOutcome.Ask: {
                // Read into a local rather than branching on the constant directly: `case Ask when
                // PreToolUseAskEnabled` makes the degrade arm unreachable (CS8120), and `if
                // (PreToolUseAskEnabled)` does the same to whichever arm the constant excludes.
                var askEnabled = PreToolUseAskEnabled;
                if (askEnabled) {
                    stdout.Write(BuildPreToolUseDecision("ask", reason));
                    journal.RecordAsk(sessionId, callId, inputHash);
                }
                await Emit(eval, "ask", askEnabled ? "ask" : "pass_through");
                break;
            }
            case PolicyOutcome.Allow:
                stdout.Write(BuildPreToolUseDecision("allow", reason));
                if (callId is { Length: > 0 }) journal.RecordTerminal(sessionId, callId, "allow", inputHash);
                await Emit(eval, "allow", "allow");
                break;
            // None under TightenOnly records nothing at all: the daemon owns the rendered session's
            // full evaluation, so nothing was decided here.
            case PolicyOutcome.None when mode == EvaluationMode.Full:
                journal.IncrementPassThrough(sessionId);
                break;
        }

        return 0;

        Task Emit(PolicyEvaluation e, string requested, string effective) =>
            new PolicyDecisionEmitter(config, profiles).EmitAsync(new PolicyDecisionEventV1(
                sessionId, node["agent_id"]?.GetValue<string>(), "claude", PolicySeams.ClaudePreToolUse,
                snapshot.Id, PolicyEngine.Version,
                mode == EvaluationMode.Full ? "full" : "tighten_only", requested, effective,
                PolicyWire.ToWire(action), PolicyWire.ToWire(e.MatchedRules),
                snapshot.Degraded, null, callId, CorrelationAmbiguous: callId is null,
                DateTimeOffset.UtcNow.ToString("O")), snapshot);
    }

    // camelCase keys are Claude's own PreToolUse hook contract, outside kcap's snake_case
    // convention — the same exemption LocalPermissionBridge.BuildClaudeResponse takes.
    internal static string BuildPreToolUseDecision(string decision, string? reason) =>
        new JsonObject {
            ["hookSpecificOutput"] = new JsonObject {
                ["hookEventName"] = "PreToolUse",
                ["permissionDecision"] = decision,
                ["permissionDecisionReason"] = reason ?? DefaultReason,
            },
        }.ToJsonString();
}
