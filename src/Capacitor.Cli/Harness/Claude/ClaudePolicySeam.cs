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

    /// <summary>The hook payload read field by field, before any policy work. The journal
    /// correlates on <paramref name="CallId"/> and <paramref name="InputHash"/> alone, so both are
    /// available even when the evaluation below cannot run at all.</summary>
    sealed record SeamFields(
        string? ToolName, string? CallId, string? Cwd, string? AgentId, JsonElement? ToolInput, string InputHash);

    /// <summary>Test-only: throws from inside the evaluation region, so the arm that must still
    /// spend an already-consumed ask is driven by a real exception. Every production step in that
    /// region is fail-open by construction today, which is exactly why nothing else can raise it.
    /// Null in production.</summary>
    internal Action? BeforeSnapshotLoadForTest;

    public async Task<int> HandlePreToolUseAsync(string body, string sessionId, bool renderedAgent, TextWriter stdout) {
        JsonNode? node;
        try { node = JsonNode.Parse(body); } catch { return 0; }
        if (node is not JsonObject payload) return 0;

        var mode     = renderedAgent ? EvaluationMode.TightenOnly : EvaluationMode.Full;
        var fields   = ReadFields(payload);
        var snapshot = LoadSnapshot(sessionId, fields.Cwd);
        if (snapshot.IsEmpty) return 0;
        var ctx = Build(fields, sessionId, PolicySeams.ClaudePreToolUse, snapshot, mode);

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
        if (node is not JsonObject payload) return SeamAnswer.NotAnswered;

        var fields  = ReadFields(payload);
        var journal = new PolicyDecisionJournal(config);
        // Ahead of the evaluation, not after it: the head entry is spent whatever this invocation
        // goes on to decide, or a failure here would leave it for the next identical request to
        // take — a second prompt for a question already put to the human once.
        var consumed = journal.Consume(sessionId, fields.CallId, fields.InputHash);

        PolicySnapshot? snapshot = null;
        SeamContext ctx;
        try {
            BeforeSnapshotLoadForTest?.Invoke();
            snapshot = LoadSnapshot(sessionId, fields.Cwd);
            if (snapshot.IsEmpty) return SeamAnswer.NotAnswered;
            ctx = Build(fields, sessionId, PolicySeams.ClaudePermissionRequest, snapshot, EvaluationMode.Full);
        } catch {
            // The prompt stands, and a consumed ask is the only thing the record would otherwise
            // lose: with none, silence matches the fail-open PreToolUse takes for the same failure.
            if (consumed.PendingAsk) await EmitEvaluationError(sessionId, fields, snapshot, consumed);
            return SeamAnswer.NotAnswered;
        }

        // Both halves ride every event this seam emits: requested=ask alone cannot say whether the
        // guard held a stale ask over a fresh allow, or the policy asked for itself.
        var fresh = ctx.Eval.Outcome.ToString().ToLowerInvariant();

        // A deny subsumes any ask that consume just cleared — it is the most restrictive answer
        // either source can produce, so nothing below could tighten it further.
        if (ctx.Eval.Outcome == PolicyOutcome.Deny) {
            stdout.Write(BuildPermissionRequestDecision("deny"));
            await Emit(ctx, "deny", "deny", pendingAskConsumed: consumed.PendingAsk, freshOutcome: fresh);
            return SeamAnswer.Answered;
        }

        // A prompt the policy's own ask forced belongs to the human it was raised for: outranking
        // it with a fresh allow would auto-answer the very question the ask exists to pose.
        if (consumed.PendingAsk) {
            await Emit(ctx, "ask", "prompt_stands", consumed.Ambiguous, consumed.PendingAsk, fresh);
            return SeamAnswer.NotAnswered;
        }

        switch (ctx.Eval.Outcome) {
            case PolicyOutcome.Allow:
                stdout.Write(BuildPermissionRequestDecision("allow"));
                await Emit(ctx, "allow", "allow", pendingAskConsumed: consumed.PendingAsk, freshOutcome: fresh);
                return SeamAnswer.Answered;
            // At an already-raised prompt, leaving it standing *is* the ask.
            case PolicyOutcome.Ask:
                await Emit(ctx, "ask", "prompt_stands", pendingAskConsumed: consumed.PendingAsk, freshOutcome: fresh);
                return SeamAnswer.NotAnswered;
            case PolicyOutcome.None:
                journal.IncrementPassThrough(sessionId);
                return SeamAnswer.NotAnswered;
            // Deny returned above; a later-added outcome must not fall into the counter, which
            // would report a decision as an ungoverned call.
            default:
                return SeamAnswer.NotAnswered;
        }
    }

    /// <summary>Reads the hook payload field by field: one wrong-typed optional must not cost the
    /// evaluation, or a payload the vendor still acts on would slip past a deny that matches it. An
    /// unusable tool_input normalizes to an Other-kind action, which rules can still match — never
    /// to no policy.</summary>
    static SeamFields ReadFields(JsonObject body) {
        var toolName = Str(body, "tool_name");
        JsonElement? toolInput;
        try {
            toolInput = body["tool_input"] is { } ti
                ? JsonDocument.Parse(ti.ToJsonString()).RootElement.Clone()
                : null;
        } catch { toolInput = null; }

        return new(
            toolName,
            Str(body, "tool_use_id"),
            Str(body, "cwd"),
            // The seam runs ahead of the hook command's own id normalization, so it strips the
            // dashes itself — every kcap event carries the dashless form.
            Str(body, "agent_id")?.Replace("-", ""),
            toolInput,
            PolicyInputHash.Compute(toolName, toolInput));
    }

    /// <summary>An empty snapshot means the session is ungoverned, not "pass-through": no output,
    /// no counter, no event, no network. The no-policy world pays nothing for the seam being
    /// installed.</summary>
    PolicySnapshot LoadSnapshot(string sessionId, string? cwd) => new PolicySnapshotStore(config)
        .LoadOrBuild(sessionId, cwd is null ? null : GitRepository.FindRoot(cwd));

    static SeamContext Build(SeamFields f, string sessionId, string seam, PolicySnapshot snapshot, EvaluationMode mode) {
        var action = ClaudeActionNormalizer.Normalize(f.ToolName, f.ToolInput, f.Cwd);
        var eval   = PolicyEngine.Evaluate(snapshot, action, mode);
        var reason = (eval.MatchedRules.Count > 0 ? eval.MatchedRules[0].Reason : null) ?? DefaultReason;
        return new(sessionId, seam, f.AgentId, f.CallId, snapshot, mode, action, eval, f.InputHash, reason);
    }

    /// <summary>The provenance for a prompt that stands because the evaluation failed, not because
    /// a policy asked for it. Emitted only when an ask was consumed, so the record can still
    /// account for the entry this invocation spent.</summary>
    Task EmitEvaluationError(string sessionId, SeamFields f, PolicySnapshot? snapshot, PolicyJournalConsume consumed) {
        string? rawPayload;
        try { rawPayload = f.ToolInput?.GetRawText(); } catch { rawPayload = null; }

        var action = new CanonicalAction {
            Kind = ActionKind.Other, Vendor = "claude", Cwd = f.Cwd,
            RawToolName = string.IsNullOrEmpty(f.ToolName) ? null : f.ToolName,
            RawPayloadJson = rawPayload,
        };

        return new PolicyDecisionEmitter(config).EmitAsync(new PolicyDecisionEventV1(
            sessionId, f.AgentId, "claude", PolicySeams.ClaudePermissionRequest,
            snapshot?.Id ?? "unknown", PolicyEngine.Version, "full", "ask", "prompt_stands",
            PolicyWire.ToWire(action), [], snapshot?.Degraded ?? false, "evaluation_error",
            f.CallId, consumed.Ambiguous, DateTimeOffset.UtcNow.ToString("O"),
            PendingAskConsumed: true, FreshOutcome: "error"), snapshot);
    }

    /// <summary><see cref="JsonNode.GetValue{T}"/> throws on a value of another type, which would
    /// take the whole evaluation down with one wrong-typed optional field.</summary>
    static string? Str(JsonObject body, string property) =>
        body[property] is JsonValue v && v.TryGetValue<string>(out var s) ? s : null;

    Task Emit(SeamContext ctx, string requested, string effective, bool? ambiguous = null,
              bool? pendingAskConsumed = null, string? freshOutcome = null) =>
        new PolicyDecisionEmitter(config).EmitAsync(new PolicyDecisionEventV1(
            ctx.SessionId, ctx.AgentId, "claude", ctx.Seam, ctx.Snapshot.Id, PolicyEngine.Version,
            ctx.Mode == EvaluationMode.Full ? "full" : "tighten_only", requested, effective,
            PolicyWire.ToWire(ctx.Action), PolicyWire.ToWire(ctx.Eval.MatchedRules),
            ctx.Snapshot.Degraded, null, ctx.CallId, ambiguous ?? (ctx.CallId is null),
            DateTimeOffset.UtcNow.ToString("O"), pendingAskConsumed, freshOutcome), ctx.Snapshot);

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
