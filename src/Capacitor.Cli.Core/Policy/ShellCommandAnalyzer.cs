namespace Capacitor.Cli.Core.Policy;

using System.Buffers;
using System.Text;

public sealed record ShellAnalysis(bool Analyzed, IReadOnlyList<ShellSegment> Segments) {
    public static readonly ShellAnalysis Unanalyzed = new(false, []);
}

/// <summary>
/// Allowlist grammar: literal-token simple commands joined by top-level '&amp;&amp;', ';' or '|'.
/// Anything else — a nested shell, a compound statement, any word the grammar cannot read as a
/// literal — is unanalyzed and therefore never allow-eligible, the one guarantee obfuscation
/// cannot defeat. The nested-shell ban is a MAINTAINED LIST of known interpreter names matched
/// against literal argv tokens, not a claim to recognize every interpreter that exists. A wrapper
/// that re-splits one argument into a new command line can hide a name inside a single token, so
/// the known such form ('env -S' / '--split-string') is refused outright; an unknown program taking
/// a command-string argument is governed by the rules that govern that program. An interpreter the
/// list does not name stays analyzable and is allow-eligible only through the same visible grants
/// that authorize any program — a pattern naming it, or a deliberately universal allow
/// (command: "*", a field-less { kind: shell } matcher) that covers every analyzed command — with
/// deny/ask rules governing it like any other program.
/// When in doubt, return Unanalyzed.
/// </summary>
public static class ShellCommandAnalyzer {
    static readonly SearchValues<char> UnquotedForbidden = SearchValues.Create("$`<>(){}[]*?\\");
    static readonly HashSet<string> ForbiddenPrograms = new(StringComparer.OrdinalIgnoreCase)
        { "eval", "exec", "sh", "bash", "zsh", "dash", "ksh", "csh", "tcsh", "fish", "ash",
          "mksh", "yash", "busybox", "pwsh", "powershell", "cmd",
          "nu", "xonsh", "elvish", "osh", "oil", "rc", "es", "ion", "tclsh" };

    // Reserved only in command position, and case-sensitively so — `echo if` is an ordinary
    // argument and a program spelled `IF` is an ordinary program, but a segment STARTING with one
    // of these is a compound statement whose body the ';'-joined grammar reads as separate simple
    // commands. `if true; then rm -rf x; fi` would otherwise analyze as three of them.
    static readonly HashSet<string> ReservedWords = new(StringComparer.Ordinal)
        { "if", "then", "elif", "else", "fi", "for", "while", "until", "do", "done",
          "case", "esac", "in", "function", "select", "time", "coproc" };

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
            // Every token, not just argv[0]: a wrapper (`env bash -c`, `sudo sh`, `command bash`)
            // otherwise hides the nested shell behind a program the analyzer reads as ordinary. A
            // literal argument that merely names one (`echo bash`) loses analyzability with it —
            // that only withholds allow-eligibility, and deny/ask still match through fragments.
            if (LooksLikeAssignment(argv[0]) || ReservedWords.Contains(argv[0])
                || argv.Any(IsForbiddenProgram) || SplitsAnArgumentIntoACommandLine(argv)) return false;
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

    // `env -S 'bash -c x'` re-splits that one literal into a fresh command line, so the shell name
    // sits inside a token no per-token name check can see. `env` itself stays ordinary — it is the
    // split-string flag, in any later position of the same segment, that forfeits analyzability.
    static bool SplitsAnArgumentIntoACommandLine(List<string> argv) {
        for (var i = 0; i < argv.Count; i++) {
            if (!IsProgram(argv[i], static n => n.Equals("env", StringComparison.OrdinalIgnoreCase))) continue;
            for (var j = i + 1; j < argv.Count; j++)
                if (IsSplitStringFlag(argv[j])) return true;
        }
        return false;
    }

    // Every spelling getopt accepts for it: the long form with or without an attached value, the
    // short form alone, with its value attached, or bundled behind other short options.
    static bool IsSplitStringFlag(string word) =>
        word.StartsWith("--split-string", StringComparison.Ordinal)
        || (word.Length > 1 && word[0] == '-' && word[1] != '-' && word.Contains('S'));

    // A path-qualified or extension-carrying spelling names the same program: `C:\bash.exe` and
    // `/bin/bash` are both bash. Over-matching here only withholds allow-eligibility, which is the
    // safe direction; under-matching would let a nested shell through.
    static bool IsForbiddenProgram(string word) => IsProgram(word, ForbiddenPrograms.Contains);

    static bool IsProgram(string word, Func<string, bool> names) {
        var name = Basename(word);
        if (names(name)) return true;
        var dot = name.LastIndexOf('.');
        return dot > 0 && names(name[..dot]);
    }

    // Both separators regardless of host OS: a quoted Windows path reaches this analyzer as a
    // literal token on every platform.
    static string Basename(string word) {
        var sep = word.LastIndexOfAny(['/', '\\']);
        return sep < 0 ? word : word[(sep + 1)..];
    }
}
