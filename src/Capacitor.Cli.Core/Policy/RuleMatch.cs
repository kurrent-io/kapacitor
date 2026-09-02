namespace Capacitor.Cli.Core.Policy;

/// <summary>
/// Matches one rule against one action component. The binder guarantees a matcher only
/// carries fields legal for its kind, so per-kind arms need not re-check foreign fields.
/// An absent field list matches anything; a present one must hit.
/// </summary>
internal static class RuleMatch {
    internal static bool Restrictive(PolicyRule rule, CanonicalAction action, ActionComponent component) {
        var m = rule.Match;
        if (m.Kind != action.Kind) return false;
        return component switch {
            ShellSegmentComponent s => MatchesArgv(m, s.Segment.Argv, restrictive: true),
            RawShellComponent raw => MatchesRaw(m, raw.Command),
            PathComponent p => AnyOrEmpty(m.Path, p.Path),
            HostComponent h => AnyOrEmpty(m.Host, h.Host) && (m.Port is null || m.Port == h.Port),
            McpToolComponent t => AnyOrEmpty(m.Server, t.Server) && AnyOrEmpty(m.Tool, t.Tool),
            OtherToolComponent o => AnyOrEmpty(m.Tool, o.ToolName),
            SentinelComponent => HasNoFieldConstraints(m),
            _ => false,
        };
    }

    internal static bool Covers(PolicyRule rule, ActionComponent component, ActionKind kind) {
        var m = rule.Match;
        if (m.Kind != kind) return false;
        return component switch {
            ShellSegmentComponent s => MatchesArgv(m, s.Segment.Argv, restrictive: false),
            PathComponent p => AnyOrEmpty(m.Path, p.Path),
            HostComponent h => AnyOrEmpty(m.Host, h.Host) && (m.Port is null || m.Port == h.Port),
            McpToolComponent t => AnyOrEmpty(m.Server, t.Server) && AnyOrEmpty(m.Tool, t.Tool),
            OtherToolComponent o => AnyOrEmpty(m.Tool, o.ToolName),
            _ => false,   // RawShellComponent and SentinelComponent are never in a coverage set
        };
    }

    static bool MatchesArgv(RuleMatcher m, IReadOnlyList<string> argv, bool restrictive) {
        if (m.Command.Count == 0) return true;
        foreach (var pattern in m.Command) {
            if (ShellTokenPattern.Parse(pattern) is not { } p) continue;
            if (restrictive ? p.MatchesRestrictive(argv, m.Exact) : p.MatchesAllow(argv)) return true;
        }
        return false;
    }

    static bool MatchesRaw(RuleMatcher m, string raw) {
        if (m.Command.Count == 0) return true;
        var fragments = ShellFragmentLexer.Lex(raw);
        foreach (var pattern in m.Command) {
            if (fragments.Count > 0 && ShellTokenPattern.Parse(pattern) is { } p
                && p.MatchesRestrictive(fragments, m.Exact)) return true;
            if (GlobPattern.IsMatch($"*{pattern}*", raw)) return true;
        }
        return false;
    }

    static bool AnyOrEmpty(IReadOnlyList<string> patterns, string value) {
        if (patterns.Count == 0) return true;
        foreach (var p in patterns)
            if (GlobPattern.IsMatch(p, value)) return true;
        return false;
    }

    static bool HasNoFieldConstraints(RuleMatcher m) =>
        m.Command.Count == 0 && m.Path.Count == 0 && m.Host.Count == 0
        && m.Server.Count == 0 && m.Tool.Count == 0 && m.Port is null;
}
