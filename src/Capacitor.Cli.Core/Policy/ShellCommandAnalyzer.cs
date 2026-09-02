namespace Capacitor.Cli.Core.Policy;

using System.Buffers;
using System.Text;

public sealed record ShellAnalysis(bool Analyzed, IReadOnlyList<ShellSegment> Segments) {
    public static readonly ShellAnalysis Unanalyzed = new(false, []);
}

/// <summary>
/// Allowlist grammar: literal-token simple commands joined by top-level '&amp;&amp;', ';' or '|'.
/// Anything else is unanalyzed and therefore never allow-eligible — the one guarantee
/// obfuscation cannot defeat. When in doubt, return Unanalyzed.
/// </summary>
public static class ShellCommandAnalyzer {
    static readonly SearchValues<char> UnquotedForbidden = SearchValues.Create("$`<>(){}[]*?\\");
    static readonly HashSet<string> ForbiddenPrograms = new(StringComparer.OrdinalIgnoreCase)
        { "eval", "exec", "sh", "bash", "zsh", "dash", "ksh", "csh", "tcsh", "fish" };

    public static ShellAnalysis Analyze(string command) {
        var segments = new List<ShellSegment>();
        var argv = new List<string>();
        var token = new StringBuilder();
        var inToken = false;
        var tokenStartsQuoted = false;

        bool FlushToken() {
            if (!inToken) return true;
            var t = token.ToString();
            token.Clear();
            inToken = false;
            if (t.Length == 0) return false;
            if (t == "!" && argv.Count == 0) return false;
            if (!tokenStartsQuoted && t.StartsWith('~')) return false;
            tokenStartsQuoted = false;
            argv.Add(t);
            return true;
        }

        bool EndSegment() {
            if (!FlushToken() || argv.Count == 0) return false;
            if (LooksLikeAssignment(argv[0]) || ForbiddenPrograms.Contains(Basename(argv[0]))) return false;
            segments.Add(new ShellSegment([.. argv]));
            argv.Clear();
            return true;
        }

        for (var i = 0; i < command.Length; i++) {
            var c = command[i];
            switch (c) {
                case '\'': {
                    var close = command.IndexOf('\'', i + 1);
                    if (close < 0) return ShellAnalysis.Unanalyzed;
                    if (!inToken) tokenStartsQuoted = true;
                    token.Append(command, i + 1, close - i - 1);
                    inToken = true;
                    i = close;
                    break;
                }
                case '"': {
                    var close = command.IndexOf('"', i + 1);
                    if (close < 0) return ShellAnalysis.Unanalyzed;
                    var inner = command.AsSpan(i + 1, close - i - 1);
                    if (inner.ContainsAny('$', '`', '\\') || inner.Contains('\n')) return ShellAnalysis.Unanalyzed;
                    if (!inToken) tokenStartsQuoted = true;
                    token.Append(inner);
                    inToken = true;
                    i = close;
                    break;
                }
                case ' ' or '\t':
                    if (!FlushToken()) return ShellAnalysis.Unanalyzed;
                    break;
                case '\n' or '\r':
                    return ShellAnalysis.Unanalyzed;
                case '&':
                    if (i + 1 >= command.Length || command[i + 1] != '&') return ShellAnalysis.Unanalyzed;
                    if (!EndSegment()) return ShellAnalysis.Unanalyzed;
                    i++;
                    break;
                case '|':
                    if (i + 1 < command.Length && command[i + 1] == '|') return ShellAnalysis.Unanalyzed;
                    if (!EndSegment()) return ShellAnalysis.Unanalyzed;
                    break;
                case ';':
                    if (!EndSegment()) return ShellAnalysis.Unanalyzed;
                    break;
                case '#' when !inToken:
                    return ShellAnalysis.Unanalyzed;
                case '~' when !inToken:
                    return ShellAnalysis.Unanalyzed;
                default:
                    if (UnquotedForbidden.Contains(c)) return ShellAnalysis.Unanalyzed;
                    token.Append(c);
                    inToken = true;
                    break;
            }
        }
        if (!EndSegment()) return ShellAnalysis.Unanalyzed;
        return new ShellAnalysis(true, segments);
    }

    static bool LooksLikeAssignment(string word) {
        var eq = word.IndexOf('=');
        if (eq <= 0) return false;
        var nameEnd = eq;
        if (word[eq - 1] == '+') {
            nameEnd = eq - 1;
            if (nameEnd <= 0) return false;
        }
        if (!(char.IsAsciiLetter(word[0]) || word[0] == '_')) return false;
        for (var i = 1; i < nameEnd; i++)
            if (!(char.IsAsciiLetterOrDigit(word[i]) || word[i] == '_')) return false;
        return true;
    }

    static string Basename(string word) {
        var slash = word.LastIndexOf('/');
        return slash < 0 ? word : word[(slash + 1)..];
    }
}
