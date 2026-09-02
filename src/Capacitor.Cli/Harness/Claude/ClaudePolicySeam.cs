namespace Capacitor.Cli.Harness.Claude;

using System.Text.Json;
using System.Text.Json.Nodes;
using Capacitor.Cli.Core;
using Capacitor.Cli.Core.Harness.Claude;
using Capacitor.Cli.Core.Policy;
using Capacitor.Cli.Policy;

/// <summary>Whether the seam produced the vendor's answer. <c>NotAnswered</c> leaves the caller's
/// own handling — the prompt, the record-only post — exactly as it would be without the seam.</summary>
internal enum SeamAnswer { Answered, NotAnswered }

/// <summary>
/// Evaluates a Claude tool call against the session's approval policy and answers the PreToolUse
/// and PermissionRequest hooks. Fail-open throughout: an unparseable payload, an unusable field or
/// an ungoverned session exits 0 with no output, because any non-zero exit renders Claude's opaque
/// hook-error banner.
/// </summary>
internal sealed class ClaudePolicySeam(ConfigRoot config) {
    /// <summary>False degrades a policy ask to pass-through: nothing is written to Claude, and the
    /// decision event records requested=ask against effective=pass_through so the gap stays
    /// visible rather than silent.</summary>
    internal const bool PreToolUseAskEnabled = true;

    const string DefaultReason = "kcap approval policy";

    /// <summary>One seam invocation's evaluated payload: what the vendor asked for, what the
    /// session's policy makes of it, and the correlation keys the journal and the event share.</summary>
    sealed record SeamContext(
        string SessionId, string Seam, string? AgentId, string? CallId, PolicySnapshot Snapshot,
        EvaluationMode Mode, CanonicalAction Action, PolicyEvaluation Eval, string InputHash, string Reason);

    public async Task<int> HandlePreToolUseAsync(string body, string sessionId, bool renderedAgent, TextWriter stdout) {
        JsonNode? node;
        try { node = JsonNode.Parse(body); } catch { return 0; }
        if (node is null) return 0;

        var mode = renderedAgent ? EvaluationMode.TightenOnly : EvaluationMode.Full;
        if (Prepare(node, sessionId, PolicySeams.ClaudePreToolUse, mode) is not { } ctx) return 0;

        var journal = new PolicyDecisionJournal(config);

        // stdout is the only thing Claude acts on; the event below is a local spool append that a
        // later drain delivers, so no path here waits on the network.
        switch (ctx.Eval.Outcome) {
            case PolicyOutcome.Deny:
                stdout.Write(BuildPreToolUseDecision("deny", ctx.Reason));
                if (ctx.CallId is { Length: > 0 }) journal.RecordTerminal(sessionId, ctx.CallId, "deny", ctx.InputHash);
                await Emit(ctx, "deny", "deny");
                break;
            case PolicyOutcome.Ask: {
                // Read into a local rather than branching on the constant directly: `case Ask when
                // PreToolUseAskEnabled` makes the degrade arm unreachable (CS8120), and `if
                // (PreToolUseAskEnabled)` does the same to whichever arm the constant excludes.
                var askEnabled = PreToolUseAskEnabled;
                if (askEnabled) {
                    stdout.Write(BuildPreToolUseDecision("ask", ctx.Reason));
                    journal.RecordAsk(sessionId, ctx.CallId, ctx.InputHash);
                }
                await Emit(ctx, "ask", askEnabled ? "ask" : "pass_through");
                break;
            }
            case PolicyOutcome.Allow:
                stdout.Write(BuildPreToolUseDecision("allow", ctx.Reason));
                if (ctx.CallId is { Length: > 0 }) journal.RecordTerminal(sessionId, ctx.CallId, "allow", ctx.InputHash);
                await Emit(ctx, "allow", "allow");
                break;
            // None under TightenOnly records nothing at all: the daemon owns the rendered session's
            // full evaluation, so nothing was decided here.
            case PolicyOutcome.None when mode == EvaluationMode.Full:
                journal.IncrementPassThrough(sessionId);
                break;
        }

        return 0;
    }

    /// <summary>
    /// Answers a raised permission prompt. The fresh evaluation always runs; the journal of what
    /// earlier seams decided for the same call can only tighten it, never loosen it.
    /// </summary>
    public async Task<SeamAnswer> HandlePermissionRequestAsync(JsonNode node, string sessionId, TextWriter stdout) {
        if (Prepare(node, sessionId, PolicySeams.ClaudePermissionRequest, EvaluationMode.Full) is not { } ctx)
            return SeamAnswer.NotAnswered;

        var journal = new PolicyDecisionJournal(config);
        var consumed = journal.Consume(sessionId, ctx.CallId, ctx.InputHash);

        // A deny subsumes any ask that consume just cleared — it is the most restrictive answer
        // either source can produce, so nothing below could tighten it further.
        if (ctx.Eval.Outcome == PolicyOutcome.Deny) {
            stdout.Write(BuildPermissionRequestDecision("deny"));
            await Emit(ctx, "deny", "deny");
            return SeamAnswer.Answered;
        }

        // A prompt the policy's own ask forced belongs to the human it was raised for: outranking
        // it with a fresh allow would auto-answer the very question the ask exists to pose.
        if (consumed.PendingAsk) {
            await Emit(ctx, "ask", "prompt_stands", consumed.Ambiguous);
            return SeamAnswer.NotAnswered;
        }

        switch (ctx.Eval.Outcome) {
            case PolicyOutcome.Allow:
                stdout.Write(BuildPermissionRequestDecision("allow"));
                await Emit(ctx, "allow", "allow");
                return SeamAnswer.Answered;
            // At an already-raised prompt, leaving it standing *is* the ask.
            case PolicyOutcome.Ask:
                await Emit(ctx, "ask", "prompt_stands");
                return SeamAnswer.NotAnswered;
            default:
                journal.IncrementPassThrough(sessionId);
                return SeamAnswer.NotAnswered;
        }
    }

    /// <summary>Reads the hook payload and evaluates it. Null for anything the seam must stay out
    /// of: an unusable payload, or a session with no policy — ungoverned, not "pass-through", so no
    /// output, no counter, no event, no network. The no-policy world pays nothing for the seam
    /// being installed.</summary>
    SeamContext? Prepare(JsonNode node, string sessionId, string seam, EvaluationMode mode) {
        string? toolName, callId, cwd, agentId;
        JsonElement? toolInput;
        try {
            toolName = node["tool_name"]?.GetValue<string>();
            callId = node["tool_use_id"]?.GetValue<string>();
            cwd = node["cwd"]?.GetValue<string>();
            // The seam runs ahead of the hook command's own id normalization, so it strips the
            // dashes itself — every kcap event carries the dashless form.
            agentId = node["agent_id"]?.GetValue<string>()?.Replace("-", "");
            toolInput = node["tool_input"] is { } ti
                ? JsonDocument.Parse(ti.ToJsonString()).RootElement.Clone()
                : null;
        } catch { return null; }

        var snapshot = new PolicySnapshotStore(config)
            .LoadOrBuild(sessionId, cwd is null ? null : GitRepository.FindRoot(cwd));
        if (snapshot.IsEmpty) return null;

        var action = ClaudeActionNormalizer.Normalize(toolName, toolInput, cwd);
        var eval = PolicyEngine.Evaluate(snapshot, action, mode);
        var reason = (eval.MatchedRules.Count > 0 ? eval.MatchedRules[0].Reason : null) ?? DefaultReason;
        return new(sessionId, seam, agentId, callId, snapshot, mode, action, eval,
            PolicyInputHash.Compute(toolName, toolInput), reason);
    }

    Task Emit(SeamContext ctx, string requested, string effective, bool? ambiguous = null) =>
        new PolicyDecisionEmitter(config).EmitAsync(new PolicyDecisionEventV1(
            ctx.SessionId, ctx.AgentId, "claude", ctx.Seam, ctx.Snapshot.Id, PolicyEngine.Version,
            ctx.Mode == EvaluationMode.Full ? "full" : "tighten_only", requested, effective,
            PolicyWire.ToWire(ctx.Action), PolicyWire.ToWire(ctx.Eval.MatchedRules),
            ctx.Snapshot.Degraded, null, ctx.CallId, ambiguous ?? (ctx.CallId is null),
            DateTimeOffset.UtcNow.ToString("O")), ctx.Snapshot);

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

    internal static string BuildPermissionRequestDecision(string behavior) =>
        new JsonObject {
            ["hookSpecificOutput"] = new JsonObject {
                ["hookEventName"] = "PermissionRequest",
                ["decision"] = new JsonObject { ["behavior"] = behavior },
            },
        }.ToJsonString();
}
