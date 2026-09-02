namespace Capacitor.Cli.Core.Policy;

using System.Text;

public abstract record YamlNode;
public sealed record YamlScalar(string Value, bool Quoted) : YamlNode;
public sealed record YamlSequence(IReadOnlyList<YamlNode> Items) : YamlNode;
public sealed record YamlMapping(IReadOnlyList<KeyValuePair<string, YamlNode>> Entries) : YamlNode {
    public YamlNode? this[string key] {
        get {
            foreach (var e in Entries)
                if (e.Key == key) return e.Value;
            return null;
        }
    }
}

public sealed class ApprovalsYamlException(int line, string message)
    : Exception($"line {line}: {message}") {
    public int Line { get; } = line;
}

/// <summary>
/// Strict parser for the approvals-policy YAML subset. Anything outside the subset throws:
/// a construct we cannot fully understand must invalidate the document loudly rather than
/// be half-applied.
/// </summary>
public static class ApprovalsYaml {
    readonly record struct Line(int LineNo, int Indent, string Content, int RawIndex);

    public static YamlMapping Parse(string text) {
        var raw = text.Split('\n');
        var lines = new List<Line>();
        for (var r = 0; r < raw.Length; r++) {
            var full = raw[r].TrimEnd('\r');
            var indent = 0;
            while (indent < full.Length && full[indent] == ' ') indent++;
            if (indent < full.Length && full[indent] == '\t')
                throw new ApprovalsYamlException(r + 1, "tab in indentation");
            var content = StripComment(full[indent..], r + 1);
            if (content.Length == 0) continue;
            if (content is "---" or "...")
                throw new ApprovalsYamlException(r + 1, "multi-document YAML is not supported");
            lines.Add(new Line(r + 1, indent, content, r));
        }
        if (lines.Count == 0) return new YamlMapping([]);
        var i = 0;
        var map = ParseMapping(raw, lines, ref i, lines[0].Indent);
        if (i != lines.Count)
            throw new ApprovalsYamlException(lines[i].LineNo, "content outside the root mapping");
        return map;
    }

    static YamlMapping ParseMapping(string[] raw, List<Line> lines, ref int i, int indent) {
        var entries = new List<KeyValuePair<string, YamlNode>>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        while (i < lines.Count && lines[i].Indent == indent && !lines[i].Content.StartsWith("- ", StringComparison.Ordinal) && lines[i].Content != "-") {
            var line = lines[i];
            var (key, rest) = SplitKey(line);
            if (!seen.Add(key)) throw new ApprovalsYamlException(line.LineNo, $"duplicate key '{key}'");
            i++;
            entries.Add(new(key, ParseValue(raw, lines, ref i, line, rest, indent)));
        }
        if (entries.Count == 0)
            throw new ApprovalsYamlException(lines[Math.Min(i, lines.Count - 1)].LineNo, "expected a mapping");
        return new YamlMapping(entries);
    }

    static YamlNode ParseValue(string[] raw, List<Line> lines, ref int i, Line keyLine, string rest, int indent) {
        if (rest is "|" or "|-") return ParseLiteralBlock(raw, lines, ref i, keyLine, chompFinal: rest == "|-");
        if (rest.Length > 0) {
            var pos = 0;
            var node = ParseFlow(rest, ref pos, keyLine.LineNo);
            if (pos != rest.Length)
                throw new ApprovalsYamlException(keyLine.LineNo, "trailing content after value");
            return node;
        }
        if (i >= lines.Count || lines[i].Indent <= indent)
            throw new ApprovalsYamlException(keyLine.LineNo, "missing value");
        var childIndent = lines[i].Indent;
        return lines[i].Content.StartsWith("- ", StringComparison.Ordinal) || lines[i].Content == "-"
            ? ParseSequence(raw, lines, ref i, childIndent)
            : ParseMapping(raw, lines, ref i, childIndent);
    }

    static YamlSequence ParseSequence(string[] raw, List<Line> lines, ref int i, int indent) {
        var items = new List<YamlNode>();
        while (i < lines.Count && lines[i].Indent == indent
               && (lines[i].Content.StartsWith("- ", StringComparison.Ordinal) || lines[i].Content == "-")) {
            var line = lines[i];
            var body = line.Content == "-" ? "" : line.Content[2..].TrimStart();
            if (body.Length == 0) throw new ApprovalsYamlException(line.LineNo, "empty sequence item");
            // An item whose body is "key: …" is a block mapping starting on the dash line:
            // rewrite the dash line as its first key line at indent+2 and re-enter ParseMapping.
            if (TrySplitKey(body, out _, out _)) {
                lines[i] = line with { Indent = indent + 2, Content = body };
                items.Add(ParseMapping(raw, lines, ref i, indent + 2));
            }
            else {
                var pos = 0;
                var node = ParseFlow(body, ref pos, line.LineNo);
                if (pos != body.Length)
                    throw new ApprovalsYamlException(line.LineNo, "trailing content after sequence item");
                items.Add(node);
                i++;
            }
        }
        return new YamlSequence(items);
    }

    static YamlScalar ParseLiteralBlock(string[] raw, List<Line> lines, ref int i, Line keyLine, bool chompFinal) {
        var collected = new List<string>();
        var last = keyLine.RawIndex;
        while (i < lines.Count && lines[i].Indent > keyLine.Indent) { last = lines[i].RawIndex; i++; }
        for (var r = keyLine.RawIndex + 1; r <= last; r++) collected.Add(raw[r].TrimEnd('\r'));
        while (collected.Count > 0 && collected[^1].Trim().Length == 0) collected.RemoveAt(collected.Count - 1);
        if (collected.Count == 0) throw new ApprovalsYamlException(keyLine.LineNo, "empty literal block");
        var dedent = collected.Where(l => l.Trim().Length > 0).Min(l => l.TakeWhile(c => c == ' ').Count());
        var body = string.Join('\n', collected.Select(l => l.Length >= dedent ? l[dedent..] : ""));
        return new YamlScalar(chompFinal ? body : body + "\n", Quoted: false);
    }

    static YamlNode ParseFlow(string s, ref int pos, int lineNo) {
        SkipSpaces(s, ref pos);
        if (pos >= s.Length) throw new ApprovalsYamlException(lineNo, "missing value");
        switch (s[pos]) {
            case '[': {
                pos++;
                var items = new List<YamlNode>();
                SkipSpaces(s, ref pos);
                if (pos < s.Length && s[pos] == ']') { pos++; return new YamlSequence(items); }
                while (true) {
                    items.Add(ParseFlow(s, ref pos, lineNo));
                    SkipSpaces(s, ref pos);
                    if (pos < s.Length && s[pos] == ',') { pos++; continue; }
                    if (pos < s.Length && s[pos] == ']') { pos++; return new YamlSequence(items); }
                    throw new ApprovalsYamlException(lineNo, "unterminated flow sequence");
                }
            }
            case '{': {
                pos++;
                var entries = new List<KeyValuePair<string, YamlNode>>();
                var seen = new HashSet<string>(StringComparer.Ordinal);
                SkipSpaces(s, ref pos);
                if (pos < s.Length && s[pos] == '}') { pos++; return new YamlMapping(entries); }
                while (true) {
                    SkipSpaces(s, ref pos);
                    var keyStart = pos;
                    while (pos < s.Length && (char.IsAsciiLetterOrDigit(s[pos]) || s[pos] is '_' or '-')) pos++;
                    var key = s[keyStart..pos];
                    if (key.Length == 0 || pos >= s.Length || s[pos] != ':')
                        throw new ApprovalsYamlException(lineNo, "expected 'key:' in flow mapping");
                    if (!seen.Add(key)) throw new ApprovalsYamlException(lineNo, $"duplicate key '{key}'");
                    pos++;
                    entries.Add(new(key, ParseFlow(s, ref pos, lineNo)));
                    SkipSpaces(s, ref pos);
                    if (pos < s.Length && s[pos] == ',') { pos++; continue; }
                    if (pos < s.Length && s[pos] == '}') { pos++; return new YamlMapping(entries); }
                    throw new ApprovalsYamlException(lineNo, "unterminated flow mapping");
                }
            }
            case '\'': return ParseSingleQuoted(s, ref pos, lineNo);
            case '"': return ParseDoubleQuoted(s, ref pos, lineNo);
            default: return ParsePlain(s, ref pos, lineNo);
        }
    }

    static YamlScalar ParseSingleQuoted(string s, ref int pos, int lineNo) {
        var sb = new StringBuilder();
        pos++;
        while (pos < s.Length) {
            if (s[pos] == '\'') {
                if (pos + 1 < s.Length && s[pos + 1] == '\'') { sb.Append('\''); pos += 2; continue; }
                pos++;
                return new YamlScalar(sb.ToString(), Quoted: true);
            }
            sb.Append(s[pos++]);
        }
        throw new ApprovalsYamlException(lineNo, "unterminated single-quoted scalar");
    }

    static YamlScalar ParseDoubleQuoted(string s, ref int pos, int lineNo) {
        var sb = new StringBuilder();
        pos++;
        while (pos < s.Length) {
            var c = s[pos];
            if (c == '"') { pos++; return new YamlScalar(sb.ToString(), Quoted: true); }
            if (c == '\\') {
                if (pos + 1 >= s.Length) break;
                sb.Append(s[pos + 1] switch {
                    '"' => '"', '\\' => '\\', 'n' => '\n', 't' => '\t',
                    _ => throw new ApprovalsYamlException(lineNo, $"unsupported escape '\\{s[pos + 1]}'"),
                });
                pos += 2;
                continue;
            }
            sb.Append(c);
            pos++;
        }
        throw new ApprovalsYamlException(lineNo, "unterminated double-quoted scalar");
    }

    static YamlScalar ParsePlain(string s, ref int pos, int lineNo) {
        var start = pos;
        while (pos < s.Length && s[pos] is not (',' or ']' or '}')) pos++;
        var value = s[start..pos].Trim();
        if (value.Length == 0) throw new ApprovalsYamlException(lineNo, "missing value");
        if (value[0] is '&' or '*' or '!' or '?' or '>' or '|' or '%' or '@' or '`')
            throw new ApprovalsYamlException(lineNo, $"unsupported YAML construct at '{value[0]}'");
        return new YamlScalar(value, Quoted: false);
    }

    static void SkipSpaces(string s, ref int pos) { while (pos < s.Length && s[pos] == ' ') pos++; }

    static (string Key, string Tail) SplitKey(Line line) =>
        TrySplitKey(line.Content, out var key, out var rest)
            ? (key, rest)
            : throw new ApprovalsYamlException(line.LineNo, "expected 'key:'");

    static bool TrySplitKey(string content, out string key, out string rest) {
        key = "";
        rest = "";
        var pos = 0;
        while (pos < content.Length && (char.IsAsciiLetterOrDigit(content[pos]) || content[pos] is '_' or '-')) pos++;
        if (pos == 0 || pos >= content.Length || content[pos] != ':') return false;
        if (pos + 1 < content.Length && content[pos + 1] != ' ') return false;
        key = content[..pos];
        rest = pos + 1 < content.Length ? content[(pos + 1)..].Trim() : "";
        return true;
    }

    static string StripComment(string content, int lineNo) {
        var inSingle = false;
        var inDouble = false;
        for (var i = 0; i < content.Length; i++) {
            var c = content[i];
            if (c == '\'' && !inDouble) inSingle = !inSingle;
            else if (c == '"' && !inSingle && (i == 0 || content[i - 1] != '\\')) inDouble = !inDouble;
            else if (c == '#' && !inSingle && !inDouble && (i == 0 || content[i - 1] is ' ' or '\t'))
                return content[..i].TrimEnd();
        }
        if (inSingle || inDouble) throw new ApprovalsYamlException(lineNo, "unterminated quoted scalar");
        return content.TrimEnd();
    }
}
