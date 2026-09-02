namespace Capacitor.Cli.Core.Policy;

using System.Text;

/// <summary>
/// Best-effort lexing of a raw (unanalyzed) command into literal fragments so deny/ask
/// token runs match across spacing and quoting differences. This is a matching aid, not
/// a parser: obfuscation can evade it, but evasion only forfeits the tighten outcomes —
/// it can never earn an allow.
/// </summary>
public static class ShellFragmentLexer {
    public static IReadOnlyList<string> Lex(string command) {
        var frags = new List<string>();
        var cur = new StringBuilder();
        var inFrag = false;
        for (var i = 0; i < command.Length; i++) {
            var c = command[i];
            if (c == '\'') {
                var close = command.IndexOf('\'', i + 1);
                if (close < 0) return [];
                cur.Append(command, i + 1, close - i - 1);
                inFrag = true;
                i = close;
            }
            else if (c == '"') {
                var j = i + 1;
                while (j < command.Length && command[j] != '"') {
                    if (command[j] == '\\' && j + 1 < command.Length && command[j + 1] is '"' or '\\') {
                        cur.Append(command[j + 1]);
                        j += 2;
                    }
                    else cur.Append(command[j++]);
                }
                if (j >= command.Length) return [];
                inFrag = true;
                i = j;
            }
            else if (char.IsWhiteSpace(c)) {
                if (inFrag) {
                    if (cur.Length > 0) frags.Add(cur.ToString());
                    cur.Clear();
                    inFrag = false;
                }
            }
            else { cur.Append(c); inFrag = true; }
        }
        if (inFrag && cur.Length > 0) frags.Add(cur.ToString());
        return frags;
    }
}
