namespace Capacitor.App.Services;

using System.Text;

/// Rewrites the SGR forms the embedded emulator mis-parses before they reach it. XTerm.NET has
/// no arm for the underline-colour selectors 58 and 59, so their numeric arguments are read as
/// attribute codes — a 4 among them underlines every later cell, and the renderers agents use
/// close styles one at a time and never send the full reset that would clear it — and it drops
/// any parameter carrying colon sub-parameters, losing 4:0 (underline off) and the colon
/// truecolour form. One instance per stream: a sequence cut by a frame boundary is held until
/// its final byte arrives.
public sealed class TerminalFeedSanitizer {
    const char Esc = (char)27;
    const int HoldCap = 64;

    readonly StringBuilder _held = new();

    public string Sanitize(string text) {
        if (_held.Length == 0 && text.IndexOf(Esc) < 0) return text;

        var input = _held.Length == 0 ? text : _held.Append(text).ToString();
        _held.Clear();
        var output = new StringBuilder(input.Length);
        var i = 0;
        while (i < input.Length) {
            if (input[i] != Esc) { output.Append(input[i]); i++; continue; }
            if (i + 1 == input.Length) { _held.Append(Esc); break; }
            if (input[i + 1] != '[') { output.Append(Esc); i++; continue; }

            var j = i + 2;
            while (j < input.Length && input[j] is >= (char)0x20 and <= (char)0x3F) j++;
            if (j == input.Length) {
                if (input.Length - i <= HoldCap) _held.Append(input, i, input.Length - i);
                else output.Append(input, i, input.Length - i);
                break;
            }

            var parameters = input.AsSpan(i + 2, j - i - 2);
            var isSgr = input[j] == 'm' && (parameters.IsEmpty || parameters[0] is >= '0' and <= '9' or ';' or ':');
            if (isSgr) output.Append(RewriteSgr(parameters));
            else output.Append(input, i, j - i + 1);
            i = j + 1;
        }
        return output.ToString();
    }

    static string RewriteSgr(ReadOnlySpan<char> parameters) {
        if (parameters.IsEmpty) return Esc + "[m";
        var groups = parameters.ToString().Split(';');
        var kept = new List<string>(groups.Length);
        for (var k = 0; k < groups.Length; k++) {
            var group = groups[k];
            var colon = group.IndexOf(':');
            var head = colon < 0 ? group : group[..colon];
            switch (head) {
                case "58":
                    if (colon < 0) k += ArgumentCount(k + 1 < groups.Length ? groups[k + 1] : "");
                    continue;
                case "59":
                    continue;
            }
            if (colon < 0) { kept.Add(group); continue; }

            var subs = group[(colon + 1)..].Split(':');
            switch (head) {
                case "4":
                    kept.Add(subs[0] == "0" ? "24" : "4");
                    break;
                case "38" or "48" when subs[0] == "2" && subs.Length >= 4:
                    // 38:2::r:g:b carries a colour-space id (empty in practice); 38:2:r:g:b omits it.
                    var rgb = subs.Length >= 5 ? subs[2..5] : subs[1..4];
                    kept.Add($"{head};2;{rgb[0]};{rgb[1]};{rgb[2]}");
                    break;
                case "38" or "48" when subs[0] == "5" && subs.Length >= 2:
                    kept.Add($"{head};5;{subs[1]}");
                    break;
                default:
                    kept.Add(head);
                    break;
            }
        }
        return kept.Count == 0 ? "" : Esc + "[" + string.Join(';', kept) + "m";
    }

    // The semicolon form's argument count: 58;2;r;g;b or 58;5;n.
    static int ArgumentCount(string kind) => kind switch { "2" => 4, "5" => 2, _ => 0 };
}
