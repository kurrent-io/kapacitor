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
    const int MaxFlowDepth = 32;
    const int MaxBlockDepth = 64;

    enum ScalarContext { Block, Flow }

    readonly record struct Line(int LineNo, int Indent, string Content, int RawIndex);

    // Comment-stripping, quote checking and the ---/... rejection only apply to a line once it
    // is read as *structural* (a key or a dash item): a literal block's raw lines never go
    // through them, so a "# ..." or "---" inside a block is content, not syntax.
    sealed class Cursor {
        readonly string[] text;
        readonly int[] indent;
        int rawPos;
        Line? pendingOverride;

        public Cursor(string[] rawLines) {
            text = new string[rawLines.Length];
            indent = new int[rawLines.Length];
            for (var r = 0; r < rawLines.Length; r++) {
                var full = rawLines[r].TrimEnd('\r');
                text[r] = full;
                var ind = 0;
                while (ind < full.Length && full[ind] == ' ') ind++;
                if (ind < full.Length && full[ind] == '\t')
                    throw new ApprovalsYamlException(r + 1, "tab in indentation");
                indent[r] = ind;
            }
        }

        public Line? Peek() {
            if (pendingOverride is { } o) return o;
            while (rawPos < text.Length) {
                var content = StripComment(text[rawPos][indent[rawPos]..], rawPos + 1);
                if (content.Length == 0) { rawPos++; continue; }
                if (content is "---" or "...")
                    throw new ApprovalsYamlException(rawPos + 1, "multi-document YAML is not supported");
                return new Line(rawPos + 1, indent[rawPos], content, rawPos);
            }
            return null;
        }

        public Line Next() {
            var line = Peek() ?? throw new InvalidOperationException("Next() called at end of input");
            pendingOverride = null;
            rawPos = line.RawIndex + 1;
            return line;
        }

        // ParseSequence uses this to turn "- match: {…}" into the first key line of a mapping
        // at indent+2, without a real raw line to back it.
        public void PushOverride(Line line) => pendingOverride = line;

        public void Resume(int newRawPos) { pendingOverride = null; rawPos = newRawPos; }

        public bool IsBlankRaw(int r) => text[r].Length <= indent[r];
        public int RawIndent(int r) => indent[r];
        public string RawText(int r) => text[r];
        public int EofLine => Math.Max(1, text.Length);

        public (int Start, int End) LiteralBlockRange(int keyRawIndex, int keyIndent) {
            var start = keyRawIndex + 1;
            var end = start;
            while (end < text.Length) {
                if (!IsBlankRaw(end) && indent[end] <= keyIndent) break;
                end++;
            }
            return (start, end);
        }
    }

    public static YamlMapping Parse(string text) {
        var raw = text.Split('\n');
        var cursor = new Cursor(raw);
        if (cursor.Peek() is not { } first) return new YamlMapping([]);
        var map = ParseMapping(cursor, first.Indent, depth: 0);
        if (cursor.Peek() is { } trailing)
            throw new ApprovalsYamlException(trailing.LineNo, "content outside the root mapping");
        return map;
    }

    static YamlMapping ParseMapping(Cursor cursor, int indent, int depth) {
        if (depth > MaxBlockDepth)
            throw new ApprovalsYamlException(cursor.Peek()?.LineNo ?? cursor.EofLine, "block nesting exceeds the supported depth");
        var entries = new List<KeyValuePair<string, YamlNode>>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        while (cursor.Peek() is { } line && line.Indent == indent && !IsDashLine(line.Content)) {
            cursor.Next();
            var (key, rest) = SplitKey(line);
            if (!seen.Add(key)) throw new ApprovalsYamlException(line.LineNo, $"duplicate key '{key}'");
            entries.Add(new(key, ParseValue(cursor, line, rest, indent, depth)));
        }
        if (entries.Count == 0)
            throw new ApprovalsYamlException(cursor.Peek()?.LineNo ?? cursor.EofLine, "expected a mapping");
        return new YamlMapping(entries);
    }

    static YamlNode ParseValue(Cursor cursor, Line keyLine, string rest, int indent, int depth) {
        if (rest is "|" or "|-") return ParseLiteralBlock(cursor, keyLine, chompFinal: rest == "|-");
        if (rest.Length > 0) {
            var pos = 0;
            var node = ParseFlow(rest, ref pos, keyLine.LineNo, ScalarContext.Block, depth: 0);
            if (pos != rest.Length)
                throw new ApprovalsYamlException(keyLine.LineNo, "trailing content after value");
            return node;
        }
        if (cursor.Peek() is not { } next || next.Indent <= indent)
            throw new ApprovalsYamlException(keyLine.LineNo, "missing value");
        return IsDashLine(next.Content)
            ? ParseSequence(cursor, next.Indent, depth + 1)
            : ParseMapping(cursor, next.Indent, depth + 1);
    }

    static YamlSequence ParseSequence(Cursor cursor, int indent, int depth) {
        if (depth > MaxBlockDepth)
            throw new ApprovalsYamlException(cursor.Peek()?.LineNo ?? cursor.EofLine, "block nesting exceeds the supported depth");
        var items = new List<YamlNode>();
        while (cursor.Peek() is { } line && line.Indent == indent && IsDashLine(line.Content)) {
            cursor.Next();
            var body = line.Content == "-" ? "" : line.Content[2..].TrimStart();
            if (body.Length == 0) throw new ApprovalsYamlException(line.LineNo, "empty sequence item");
            // An item whose body is "key: …" is a block mapping starting on the dash line:
            // rewrite the dash line as its first key line at indent+2 and re-enter ParseMapping.
            if (TrySplitKey(body, out _, out _)) {
                cursor.PushOverride(line with { Indent = indent + 2, Content = body });
                items.Add(ParseMapping(cursor, indent + 2, depth + 1));
            }
            else {
                var pos = 0;
                var node = ParseFlow(body, ref pos, line.LineNo, ScalarContext.Block, depth: 0);
                if (pos != body.Length)
                    throw new ApprovalsYamlException(line.LineNo, "trailing content after sequence item");
                items.Add(node);
            }
        }
        return new YamlSequence(items);
    }

    static YamlScalar ParseLiteralBlock(Cursor cursor, Line keyLine, bool chompFinal) {
        var (start, end) = cursor.LiteralBlockRange(keyLine.RawIndex, keyLine.Indent);
        var contentEnd = end;
        while (contentEnd > start && cursor.IsBlankRaw(contentEnd - 1)) contentEnd--;

        var firstNonBlank = -1;
        for (var r = start; r < contentEnd; r++)
            if (!cursor.IsBlankRaw(r)) { firstNonBlank = r; break; }
        if (firstNonBlank < 0) throw new ApprovalsYamlException(keyLine.LineNo, "empty literal block");

        // The block's own indent is fixed by its first content line; a shallower non-blank line
        // is a mis-indented sibling that leaked into the block range, not more block content.
        var blockIndent = cursor.RawIndent(firstNonBlank);
        for (var r = start; r < contentEnd; r++)
            if (!cursor.IsBlankRaw(r) && cursor.RawIndent(r) < blockIndent)
                throw new ApprovalsYamlException(r + 1, "line is less indented than the literal block");

        var lines = new string[contentEnd - start];
        for (var r = start; r < contentEnd; r++) {
            var t = cursor.RawText(r);
            lines[r - start] = t.Length >= blockIndent ? t[blockIndent..] : "";
        }
        cursor.Resume(end);
        var body = string.Join('\n', lines);
        return new YamlScalar(chompFinal ? body : body + "\n", Quoted: false);
    }

    static YamlNode ParseFlow(string s, ref int pos, int lineNo, ScalarContext ctx, int depth) {
        if (depth > MaxFlowDepth)
            throw new ApprovalsYamlException(lineNo, "flow nesting exceeds the supported depth");
        SkipSpaces(s, ref pos);
        if (pos >= s.Length) throw new ApprovalsYamlException(lineNo, "missing value");
        switch (s[pos]) {
            case '[': {
                pos++;
                var items = new List<YamlNode>();
                SkipSpaces(s, ref pos);
                if (pos < s.Length && s[pos] == ']') { pos++; return new YamlSequence(items); }
                while (true) {
                    items.Add(ParseFlow(s, ref pos, lineNo, ScalarContext.Flow, depth + 1));
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
                    entries.Add(new(key, ParseFlow(s, ref pos, lineNo, ScalarContext.Flow, depth + 1)));
                    SkipSpaces(s, ref pos);
                    if (pos < s.Length && s[pos] == ',') { pos++; continue; }
                    if (pos < s.Length && s[pos] == '}') { pos++; return new YamlMapping(entries); }
                    throw new ApprovalsYamlException(lineNo, "unterminated flow mapping");
                }
            }
            case '\'': return ParseSingleQuoted(s, ref pos, lineNo);
            case '"': return ParseDoubleQuoted(s, ref pos, lineNo);
            default: return ParsePlain(s, ref pos, lineNo, ctx);
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

    static YamlScalar ParsePlain(string s, ref int pos, int lineNo, ScalarContext ctx) {
        var start = pos;
        if (ctx == ScalarContext.Flow) {
            while (pos < s.Length && s[pos] is not (',' or ']' or '}')) pos++;
        }
        else {
            pos = s.Length;
        }
        var value = s[start..pos].Trim();
        if (value.Length == 0) throw new ApprovalsYamlException(lineNo, "missing value");
        if (value == "-" || (value.Length > 1 && value[0] == '-' && value[1] == ' '))
            throw new ApprovalsYamlException(lineNo, "ambiguous '-' at the start of a plain scalar");
        if (value[0] == ':')
            throw new ApprovalsYamlException(lineNo, "ambiguous ':' at the start of a plain scalar");
        if (value[0] is '&' or '*' or '!' or '?' or '>' or '|' or '%' or '@' or '`')
            throw new ApprovalsYamlException(lineNo, $"unsupported YAML construct at '{value[0]}'");
        return new YamlScalar(value, Quoted: false);
    }

    static void SkipSpaces(string s, ref int pos) { while (pos < s.Length && s[pos] == ' ') pos++; }

    static bool IsDashLine(string content) => content.StartsWith("- ", StringComparison.Ordinal) || content == "-";

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
            if (inSingle) {
                if (c == '\'') {
                    if (i + 1 < content.Length && content[i + 1] == '\'') i++;
                    else inSingle = false;
                }
                continue;
            }
            if (inDouble) {
                if (c == '\\' && i + 1 < content.Length) i++;
                else if (c == '"') inDouble = false;
                continue;
            }
            // A quote only opens after a boundary character, so a mid-word apostrophe
            // ("don't") stays plain text instead of starting a quoted region.
            if (c == '\'' && CanOpenQuote(content, i)) { inSingle = true; continue; }
            if (c == '"' && CanOpenQuote(content, i)) { inDouble = true; continue; }
            if (c == '#' && (i == 0 || content[i - 1] is ' ' or '\t')) return content[..i].TrimEnd();
        }
        if (inSingle || inDouble) throw new ApprovalsYamlException(lineNo, "unterminated quoted scalar");
        return content.TrimEnd();
    }

    static bool CanOpenQuote(string content, int i) =>
        i == 0 || content[i - 1] is ' ' or '\t' or ':' or ',' or '[' or '{';
}
