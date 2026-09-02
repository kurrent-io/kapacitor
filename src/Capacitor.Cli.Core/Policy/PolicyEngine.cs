namespace Capacitor.Cli.Core.Policy;

public enum EvaluationMode { Full, TightenOnly }
public enum PolicyOutcome { Allow, Ask, Deny, None }

public sealed record MatchedRuleRef(PolicyScope Scope, int RuleIndex, RuleOutcome Outcome, string? Reason);
public sealed record PolicyEvaluation(PolicyOutcome Outcome, IReadOnlyList<MatchedRuleRef> MatchedRules) {
    public static readonly PolicyEvaluation None = new(PolicyOutcome.None, []);
}

public static class PolicyEngine {
    public const string Version = "1";
    static readonly PolicyScope[] ScopeOrder =
        [PolicyScope.Org, PolicyScope.Team, PolicyScope.Project, PolicyScope.Repo, PolicyScope.User];

    public static PolicyEvaluation Evaluate(PolicySnapshot snapshot, CanonicalAction action, EvaluationMode mode) {
        if (snapshot.Documents.Count == 0) return PolicyEvaluation.None;
        var restriction = PolicyComponents.RestrictionOf(action);
        foreach (var outcome in (RuleOutcome[])[RuleOutcome.Deny, RuleOutcome.Ask]) {
            if (FirstRestrictiveHit(snapshot, action, restriction, outcome) is { } hit)
                return new(outcome == RuleOutcome.Deny ? PolicyOutcome.Deny : PolicyOutcome.Ask, [hit]);
        }
        if (mode == EvaluationMode.TightenOnly) return PolicyEvaluation.None;

        var coverage = PolicyComponents.CoverageOf(action);
        if (coverage.Count == 0) return PolicyEvaluation.None;
        var covering = new List<MatchedRuleRef>();
        foreach (var component in coverage) {
            if (FirstCoveringAllow(snapshot, action, component) is not { } rule) return PolicyEvaluation.None;
            if (!covering.Contains(rule)) covering.Add(rule);
        }
        return new(PolicyOutcome.Allow, covering);
    }

    static MatchedRuleRef? FirstRestrictiveHit(
        PolicySnapshot snapshot, CanonicalAction action, IReadOnlyList<ActionComponent> restriction, RuleOutcome outcome) {
        foreach (var scope in ScopeOrder)
            foreach (var doc in snapshot.Documents)
                if (doc.Scope == scope)
                    for (var i = 0; i < doc.Document.Rules.Count; i++) {
                        var rule = doc.Document.Rules[i];
                        if (rule.Outcome != outcome) continue;
                        foreach (var component in restriction)
                            if (RuleMatch.Restrictive(rule, action, component))
                                return new(scope, i, outcome, rule.Reason);
                    }
        return null;
    }

    static MatchedRuleRef? FirstCoveringAllow(PolicySnapshot snapshot, CanonicalAction action, ActionComponent component) {
        foreach (var scope in ScopeOrder)
            foreach (var doc in snapshot.Documents)
                if (doc.Scope == scope)
                    for (var i = 0; i < doc.Document.Rules.Count; i++) {
                        var rule = doc.Document.Rules[i];
                        if (rule.Outcome == RuleOutcome.Allow && RuleMatch.Covers(rule, component, action.Kind))
                            return new(scope, i, RuleOutcome.Allow, rule.Reason);
                    }
        return null;
    }
}
