namespace Capacitor.Cli.Core.Policy;

/// <summary>
/// A shell pattern split on whitespace into per-token globs. A final bare "*" is a rest
/// token: it matches zero or more remaining argv tokens and is the only way an allow
/// pattern accepts extra argv.
/// </summary>
public sealed record ShellTokenPattern(IReadOnlyList<string> Tokens, bool HasRestToken) {
    public static ShellTokenPattern? Parse(string pattern) {
        var tokens = pattern.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (tokens.Length == 0) return null;
        var rest = tokens[^1] == "*";
        return new(rest ? tokens[..^1] : tokens, rest);
    }

    public bool MatchesAllow(IReadOnlyList<string> argv) {
        if (HasRestToken ? argv.Count < Tokens.Count : argv.Count != Tokens.Count) return false;
        for (var i = 0; i < Tokens.Count; i++)
            if (!GlobPattern.IsMatch(Tokens[i], argv[i])) return false;
        return true;
    }

    public bool MatchesRestrictive(IReadOnlyList<string> argv, bool exact) {
        if (exact) return MatchesAllow(argv);
        if (Tokens.Count == 0) return true;
        for (var start = 0; start + Tokens.Count <= argv.Count; start++) {
            var all = true;
            for (var i = 0; i < Tokens.Count; i++)
                if (!GlobPattern.IsMatch(Tokens[i], argv[start + i])) { all = false; break; }
            if (all) return true;
        }
        return false;
    }
}
