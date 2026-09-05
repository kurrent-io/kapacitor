namespace Capacitor.Models.Transcripts.Harness.Codex;

/// <summary>
/// Result of classifying a Codex shell command.
/// Mirrors the <c>ParsedCommand</c> enum shape Codex's Rust parser emits on
/// <c>event_msg.exec_command_end.parsed_cmd</c> — see
/// <c>codex-rs/protocol/src/parse_command.rs</c> in <c>openai/codex</c>.
/// </summary>
/// <param name="Type">One of <c>"read"</c>, <c>"search"</c>, <c>"list_files"</c>.</param>
public sealed record CodexCommandHint(string Type, string? Path = null, string? Name = null, string? Query = null);

/// <summary>
/// Port of (a useful subset of) Codex's <c>parse_command</c> classifier, used
/// to label shell exec calls with the same Read/Search/List iconography
/// Codex's own TUI uses, even when the rollout doesn't include the
/// <c>event_msg.exec_command_end</c> envelope that carries the structured
/// <c>parsed_cmd</c>.
///
/// Codex 0.5.x rollouts emit just <c>function_call</c>/<c>function_call_output</c>
/// (no <c>exec_command_end</c>), so the canonical <c>extensions.codex.exec.parsed_cmd</c>
/// is empty and every shell call would render as a generic "Shell". Running
/// this classifier on the <c>arguments.cmd</c> string at render time recovers
/// the structured kind.
///
/// Not exhaustive: we cover the high-traffic commands (sed/nl/cat/head/tail/
/// less/bat/more/awk; rg/rga; grep/egrep/fgrep; ag/ack/pt; git grep / git
/// ls-files; ls/eza/exa/tree/du/fd/find). For everything else we return null
/// and the caller falls back to the raw shell rendering — same outcome as
/// Codex emitting <c>type:"unknown"</c>.
/// </summary>
public static class CodexCommandClassifier {
    /// <summary>
    /// Classify a shell command. Strips <c>bash -c</c>/<c>bash -lc</c>/
    /// <c>zsh -c</c>/<c>zsh -lc</c> wrappers, splits on <c>|</c>/<c>&amp;&amp;</c>/
    /// <c>||</c>/<c>;</c>, drops small formatting helpers (per upstream's
    /// <c>drop_small_formatting_commands</c>), and runs <see cref="ClassifySingle"/>
    /// on each remaining segment. Returns null when any non-helper segment is
    /// unknown — matching Codex's "collapse any-unknown pipeline to a single
    /// Unknown" rule. Helper-only pipelines (e.g. <c>head -n 5</c> alone)
    /// also return null.
    /// </summary>
    public static CodexCommandHint? Classify(string? cmd) {
        if (string.IsNullOrWhiteSpace(cmd)) return null;

        // Outer-shell redirection has to be detected against the raw input —
        // unwrapping bash -lc would hide a trailing `>out` or `2>&1` that sits
        // OUTSIDE the wrapped script and is parsed by the spawning shell, not
        // the wrapped one.
        if (ContainsUnquotedRedirection(cmd)) return null;

        var tokens = ShellTokenizer.Split(cmd);

        if (tokens.Count == 0) return null;

        // Only peel `bash/zsh -c/-lc <script>` when that's the WHOLE invocation —
        // any extra tokens after the script are either positional params for
        // the wrapped shell ($0/$1 inside the script can mean anything) or a
        // suffix we can't reason about. Falling through with the outer tokens
        // makes the classifier treat `bash` as the head (no match) and collapse
        // the whole thing to Shell, which is the safe rendering.
        var scriptForScan = cmd;

        if (tokens.Count == 3
         && (tokens[0] == "bash" || tokens[0] == "zsh")
         && (tokens[1] == "-c"   || tokens[1] == "-lc")) {
            scriptForScan = tokens[2];
            tokens        = ShellTokenizer.Split(scriptForScan);

            if (tokens.Count == 0) return null;

            // Re-scan the inner script — the outer `"..."` hides any `>`/`<`
            // inside the wrapped command from the first scan (those are
            // re-interpreted by the shell bash -lc spawns).
            if (ContainsUnquotedRedirection(scriptForScan)) return null;
        }

        var segments = SplitOnConnectors(tokens);

        if (segments.Count == 0) return null;

        // Drop small formatting helpers (head/tail/sed/awk/wc/cut/sort/uniq/tee/…
        // without file operands, plus echo, true, and `nl` with only flags) so
        // common Codex pipelines like `rg --files | head -n 50` or
        // `nl -ba file | sed -n '1,80p'` keep their primary classification
        // instead of collapsing to Shell. Mirrors `is_small_formatting_command`
        // / `simplify_once` in openai/codex codex-rs/shell-command/src/parse_command.rs.
        var meaningful = segments
            .Where(seg => seg.Count > 0 && seg[0] != "cd" && !IsFormattingHelper(seg))
            .ToList();

        if (meaningful.Count == 0) return null;

        CodexCommandHint? first = null;

        foreach (var segment in meaningful) {
            var hint = ClassifySingle(segment);

            // A non-helper segment with no classification = unknown ⇒ collapse to Shell.
            if (hint is null) return null;

            first ??= hint;
        }

        return first;
    }

    /// <summary>
    /// Quote-aware scan for shell I/O redirection characters. Returns true on
    /// the first unquoted <c>&gt;</c> or <c>&lt;</c> encountered. Catches:
    /// <list type="bullet">
    ///   <item>spaced operators: <c>cmd &gt; out</c>, <c>cmd 2&gt; err</c>;</item>
    ///   <item>glued operators: <c>cmd &gt;out</c>, <c>cmd 2&gt;err</c>,
    ///     <c>foo.txt&gt;bar.txt</c>;</item>
    ///   <item>combined fd dups: <c>cmd &gt; out 2&gt;&amp;1</c>;</item>
    /// </list>
    /// while still respecting single/double quoting so <c>rg '&gt;' src</c>
    /// and <c>grep "&lt;tag&gt;" file</c> stay classified as Search.
    /// </summary>
    static bool ContainsUnquotedRedirection(string input) {
        var quote = '\0';

        for (var i = 0; i < input.Length; i++) {
            var c = input[i];

            if (quote != '\0') {
                if (c == '\\' && quote == '"' && i + 1 < input.Length) {
                    i++;

                    continue;
                }

                if (c == quote) quote = '\0';

                continue;
            }

            switch (c) {
                case '\'' or '"':
                    quote = c;

                    continue;
                case '\\' when i + 1 < input.Length:
                    i++;

                    continue;
                case '>' or '<':
                    return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Mirrors upstream's <c>is_small_formatting_command</c>: returns true for
    /// the well-known pipeline helpers (<c>wc</c>/<c>tr</c>/<c>cut</c>/<c>sort</c>/
    /// <c>uniq</c>/<c>tee</c>/<c>column</c>/<c>yes</c>/<c>printf</c>; <c>head</c>/
    /// <c>tail</c>/<c>sed</c>/<c>awk</c>/<c>nl</c> when they have no file operand;
    /// <c>echo</c>, the boolean <c>true</c>, and <c>xargs</c> with a known
    /// display-only subcommand).
    ///
    /// <c>xargs</c> is treated as Unknown (i.e. not a helper) by default —
    /// upstream only flags a narrow set of mutating subcommands, which misses
    /// obvious destructive forms (<c>xargs rm</c>, <c>xargs mv</c>,
    /// <c>xargs sh -c '…'</c>). Defaulting to Unknown collapses any such
    /// pipeline back to Shell so the row surfaces the full command.
    /// </summary>
    static bool IsFormattingHelper(List<string> tokens) {
        if (tokens.Count == 0) return false;

        var head = tokens[0];
        var tail = tokens.Skip(1).ToList();

        return head switch {
            "wc" or "tr" or "cut" or "sort" or "uniq" or "tee" or "column" or "yes" or "printf" => true,
            "echo"                                                                              => true,
            "true"                                                                              => tokens.Count == 1,
            "xargs"                                                                             => IsSafeDisplayOnlyXargs(tokens),
            // awk: helper unless there's a clear data-file operand. We don't fully
            // emulate Codex's awk_data_file_operand; treat as helper when no bare
            // non-flag operand follows the script.
            "awk" => tail.SkipWhile(t => t.StartsWith('-') || t.StartsWith('\'') || t.StartsWith('"'))
                .Skip(1)
                .All(t => t.StartsWith('-')),
            "head" => HeadTailIsHelper(tail, allowPlus: false),
            "tail" => HeadTailIsHelper(tail, allowPlus: true),
            // sed: helper unless it would classify as a Read (`sed -n '<range>p' file`);
            // otherwise it's a pipe formatter.
            "sed" => SedRead(tail) is null,
            // nl: helper when no file operand (only flags).
            "nl" => NlRead(tail) is null,
            _    => false
        };
    }

    static bool HeadTailIsHelper(List<string> tail, bool allowPlus) {
        return tail.Count switch {
            // `head` / `tail` alone, or with a single flag/option, or with `-n COUNT` / `-c COUNT`
            // and no trailing file operand — all formatters.
            0                                                                          => true,
            1                                                                          => tail[0].StartsWith('-'),
            2 when (tail[0] == "-n" || tail[0] == "-c") && IsCount(tail[1], allowPlus) => true,
            _                                                                          => false
        };
    }

    static bool IsCount(string s, bool allowPlus) {
        if (s.Length == 0) return false;

        var body = allowPlus && s[0] == '+' ? s[1..] : s;

        return body.Length > 0 && body.All(char.IsAsciiDigit);
    }

    /// <summary>
    /// True only when the xargs subcommand is one of a strict allowlist of
    /// display-only programs (<c>cat</c>/<c>head</c>/<c>tail</c>/<c>less</c>/
    /// <c>more</c>/<c>bat</c>/<c>wc</c>/<c>file</c>/<c>ls</c>/<c>du</c>/
    /// <c>echo</c>/<c>printf</c>/<c>stat</c>) — and only when that subcommand
    /// itself looks display-only (no in-place flag on sed, no <c>--replace</c>
    /// on rg, none of the obvious destructive verbs <c>rm</c>/<c>mv</c>/
    /// <c>cp</c>/<c>rmdir</c>/<c>sh</c>/<c>bash</c>/<c>perl</c>/<c>ruby</c>/
    /// <c>python</c>/<c>git</c>/<c>chmod</c>/<c>chown</c>/<c>kill</c>).
    /// Everything else collapses the pipeline to Shell so the row surfaces the
    /// full command instead of hiding a destructive xargs side effect.
    /// </summary>
    static bool IsSafeDisplayOnlyXargs(List<string> tokens) {
        // Skip xargs's own flags first.
        var i = 1;

        while (i < tokens.Count) {
            var t = tokens[i];

            if (t == "--") {
                i++;

                break;
            }

            if (!t.StartsWith('-')) break;

            var takesValue = t is "-E" or "-e" or "-I" or "-L" or "-n" or "-P" or "-s";
            i += takesValue && t.Length == 2 ? 2 : 1;
        }

        // Bare xargs (no subcommand) defaults to echo — safe.
        if (i >= tokens.Count) return true;

        var sub = tokens[i];

        return sub switch {
            "cat" or "head" or "tail" or "less" or "more" or "bat" or "batcat"
                or "wc" or "file" or "ls" or "du" or "echo" or "printf" or "stat"
                => true,
            _ => false
        };
    }

    static List<List<string>> SplitOnConnectors(IReadOnlyList<string> tokens) {
        var result  = new List<List<string>>();
        var current = new List<string>();

        foreach (var t in tokens) {
            if (t is "|" or "&&" or "||" or ";") {
                if (current.Count > 0) result.Add(current);
                current = [];
            } else {
                current.Add(t);
            }
        }

        if (current.Count > 0) result.Add(current);

        return result;
    }

    static CodexCommandHint? ClassifySingle(List<string> tokens) {
        if (tokens.Count == 0) return null;

        var head = tokens[0];
        var tail = tokens.Skip(1).ToList();

        return head switch {
            "sed" => SedRead(tail),
            "nl" => NlRead(tail),
            "cat" or "more" => SingleFileRead(tail, []),
            "bat" or "batcat" => SingleFileRead(tail, ["--theme", "--language", "--style", "--terminal-width", "--tabs", "--line-range", "--map-syntax"]),
            "less" => SingleFileRead(tail, ["-p", "-P", "-x", "-y", "-z", "-j", "--pattern", "--prompt", "--tabs", "--shift", "--jump-target"]),
            "head" or "tail" => HeadTailRead(tail),
            "rg" or "rga" or "ripgrep-all" => RgClassify(tail),
            "grep" or "egrep" or "fgrep" => GrepLike(tail, []),
            "ag" or "ack" or "pt" => GrepLike(tail, ["-G", "-g", "--file-search-regex", "--ignore-dir", "--ignore-file", "--path-to-ignore"]),
            "ls" or "eza" or "exa" => ListFiles(tail, ["-I", "-w", "--block-size", "--format", "--time-style", "--color", "--quoting-style", "--ignore-glob", "--sort", "--time"]),
            "tree" => ListFiles(tail, ["-L", "-P", "-I", "--charset", "--filelimit", "--sort"]),
            "du" => ListFiles(tail, ["-d", "--max-depth", "-B", "--block-size", "--exclude", "--time-style"]),
            "fd" => FdClassify(tail),
            "find" => FindClassify(tail),
            "git" when tail.Count > 0 && tail[0] == "grep" => GrepLike(tail.Skip(1).ToList(), []),
            "git" when tail.Count > 0 && tail[0] == "ls-files" => ListFiles(tail.Skip(1).ToList(), ["--exclude", "--exclude-from", "--pathspec-from-file"]),
            _ => null
        };
    }

    static CodexCommandHint? SedRead(List<string> tail) {
        var trimmed = TrimAtConnector(tail);

        if (trimmed.All(t => t != "-n")) return null;

        // sed -n '1,220p' file  or  sed -n -e '1,220p' file
        var hasRange = false;

        for (var i = 0; i < trimmed.Count; i++) {
            var arg = trimmed[i];

            if (arg is "-e" or "--expression") {
                if (i + 1 < trimmed.Count && IsValidSedNArg(trimmed[i + 1])) hasRange = true;
                i++;

                continue;
            }

            if (arg is "-f" or "--file") {
                i++;

                continue;
            }

            if (!arg.StartsWith('-') && IsValidSedNArg(arg)) hasRange = true;
        }

        if (!hasRange) return null;

        var candidates = SkipFlagValues(trimmed, ["-e", "-f", "--expression", "--file"]);
        var nonFlags   = candidates.Where(a => !a.StartsWith('-')).ToList();

        // When the range arg sits among the positionals (sed -n '1,50p' file),
        // it appears before the file path; skip it. When it's the ONLY positional
        // (sed -n '1,50p' alone), there's no file → not a Read.
        string? path = nonFlags switch {
            []                                                     => null,
            [var only] when IsValidSedNArg(only)                   => null,
            [var first, var second, ..] when IsValidSedNArg(first) => second,
            [var first, ..]                                        => first,
        };

        if (path is null) return null;

        return new CodexCommandHint("read", Path: path, Name: ShortDisplayName(path));
    }

    static bool IsValidSedNArg(string s) {
        if (string.IsNullOrEmpty(s) || !s.EndsWith('p')) return false;

        var core  = s[..^1];
        var parts = core.Split(',');

        return parts.Length switch {
            1 => parts[0].Length > 0 && parts[0].All(char.IsAsciiDigit),
            2 => parts[0].Length > 0 && parts[1].Length > 0 && parts[0].All(char.IsAsciiDigit) && parts[1].All(char.IsAsciiDigit),
            _ => false
        };
    }

    static CodexCommandHint? NlRead(List<string> tail) {
        var candidates = SkipFlagValues(tail, ["-s", "-w", "-v", "-i", "-b"]);
        var path       = candidates.FirstOrDefault(a => !a.StartsWith('-'));

        return path is null
            ? null
            : new CodexCommandHint("read", Path: path, Name: ShortDisplayName(path));
    }

    static CodexCommandHint? SingleFileRead(List<string> tail, string[] flagsWithValues) {
        var candidates = SkipFlagValues(tail, flagsWithValues);
        var nonFlags   = candidates.Where(a => !a.StartsWith('-')).ToList();

        if (nonFlags.Count != 1) return null;

        var path = nonFlags[0];

        return new CodexCommandHint("read", Path: path, Name: ShortDisplayName(path));
    }

    static CodexCommandHint? HeadTailRead(List<string> tail) {
        // head -n 50 file  /  head -n50 file  /  head file
        var hasValidN = false;

        if (tail.Count > 0) {
            if (tail[0] == "-n" && tail.Count > 1) {
                var n = tail[1].TrimStart('+');
                hasValidN = n.Length > 0 && n.All(char.IsAsciiDigit);
            } else if (tail[0].StartsWith("-n", StringComparison.Ordinal)) {
                var n = tail[0][2..].TrimStart('+');
                hasValidN = n.Length > 0 && n.All(char.IsAsciiDigit);
            }
        }

        if (hasValidN) {
            var candidates = new List<string>();
            var i          = 0;

            while (i < tail.Count) {
                if (i == 0 && tail[i] == "-n" && i + 1 < tail.Count) {
                    var n = tail[i + 1].TrimStart('+');

                    if (n.Length > 0 && n.All(char.IsAsciiDigit)) {
                        i += 2;

                        continue;
                    }
                }

                candidates.Add(tail[i]);
                i++;
            }

            var path = candidates.FirstOrDefault(p => !p.StartsWith('-'));

            if (path is not null) return new CodexCommandHint("read", Path: path, Name: ShortDisplayName(path));
        }

        if (tail is [var single] && !single.StartsWith('-')) {
            return new CodexCommandHint("read", Path: single, Name: ShortDisplayName(single));
        }

        return null;
    }

    static CodexCommandHint RgClassify(List<string> tail) {
        var trimmed  = TrimAtConnector(tail);
        var hasFiles = trimmed.Any(a => a == "--files");

        var candidates = SkipFlagValues(
            trimmed,
            [
                "-g", "--glob", "--iglob", "-t", "--type", "--type-add", "--type-not",
                "-m", "--max-count", "-A", "-B", "-C", "--context", "--max-depth"
            ]
        );
        var nonFlags = candidates.Where(p => !p.StartsWith('-')).ToList();

        if (hasFiles) {
            var path = nonFlags.Count > 0 ? nonFlags[0] : null;

            return new CodexCommandHint("list_files", Path: path);
        }

        var query = nonFlags.Count > 0 ? nonFlags[0] : null;
        var pth   = nonFlags.Count > 1 ? nonFlags[1] : null;

        return new CodexCommandHint("search", Path: pth, Query: query);
    }

    static CodexCommandHint GrepLike(List<string> tail, string[] flagsWithValues) {
        var trimmed    = TrimAtConnector(tail);
        var candidates = SkipFlagValues(trimmed, flagsWithValues);
        var nonFlags   = candidates.Where(p => !p.StartsWith('-')).ToList();
        var query      = nonFlags.Count > 0 ? nonFlags[0] : null;
        var path       = nonFlags.Count > 1 ? nonFlags[1] : null;

        return new CodexCommandHint("search", Path: path, Query: query);
    }

    static CodexCommandHint ListFiles(List<string> tail, string[] flagsWithValues) {
        var trimmed    = TrimAtConnector(tail);
        var candidates = SkipFlagValues(trimmed, flagsWithValues);
        var path       = candidates.FirstOrDefault(p => !p.StartsWith('-'));

        return new CodexCommandHint("list_files", Path: path);
    }

    static CodexCommandHint FdClassify(List<string> tail) {
        var trimmed    = TrimAtConnector(tail);
        var candidates = SkipFlagValues(trimmed, ["-e", "--extension", "-t", "--type", "-d", "--max-depth", "-E", "--exclude"]);
        var nonFlags   = candidates.Where(p => !p.StartsWith('-')).ToList();
        var query      = nonFlags.Count > 0 ? nonFlags[0] : null;
        var path       = nonFlags.Count > 1 ? nonFlags[1] : null;

        return query is null
            ? new CodexCommandHint("list_files", Path: path)
            : new CodexCommandHint("search", Path: path, Query: query);
    }

    static CodexCommandHint FindClassify(List<string> tail) {
        // Very basic: first non-flag is the search path; `-name <pattern>` becomes query.
        string? path  = null;
        string? query = null;

        for (var i = 0; i < tail.Count; i++) {
            var arg = tail[i];

            if (arg is "-name" or "-iname") {
                if (i + 1 < tail.Count) query = tail[i + 1];
                i++;

                continue;
            }

            if (!arg.StartsWith('-') && path is null) path = arg;
        }

        return query is null
            ? new("list_files", Path: path)
            : new CodexCommandHint("search", Path: path, Query: query);
    }

    static List<string> TrimAtConnector(IReadOnlyList<string> tokens) {
        return tokens.TakeWhile(t => t is not ("|" or "&&" or "||" or ";")).ToList();
    }

    static List<string> SkipFlagValues(IReadOnlyList<string> tokens, string[] flagsWithValues) {
        var result = new List<string>();

        for (var i = 0; i < tokens.Count; i++) {
            var t = tokens[i];

            if (Array.IndexOf(flagsWithValues, t) >= 0) {
                i++; // skip the value following this flag

                continue;
            }

            result.Add(t);
        }

        return result;
    }

    static string ShortDisplayName(string path) {
        var lastSlash = path.LastIndexOfAny(['/', '\\']);

        return lastSlash >= 0 && lastSlash < path.Length - 1 ? path[(lastSlash + 1)..] : path;
    }
}

/// <summary>
/// Minimal POSIX-ish shell tokenizer — whitespace splits tokens, single and
/// double quotes group content, backslash escapes the next character.
/// Connectors (<c>|</c>, <c>&amp;&amp;</c>, <c>||</c>, <c>;</c>) come out as their own
/// tokens. Enough to classify the well-formed <c>cmd</c> strings Codex emits
/// without pulling in a full shell grammar (the upstream Rust impl uses
/// <c>shlex</c>; the rules below mirror its handling of quoted strings).
/// </summary>
internal static class ShellTokenizer {
    public static List<string> Split(string input) {
        var result   = new List<string>();
        var current  = new System.Text.StringBuilder();
        var quote    = '\0';
        var hasToken = false;

        void Flush() {
            if (hasToken) result.Add(current.ToString());
            current.Clear();
            hasToken = false;
        }

        for (var i = 0; i < input.Length; i++) {
            var c = input[i];

            if (quote != '\0') {
                if (c == quote) {
                    quote = '\0';
                } else if (c == '\\' && quote == '"' && i + 1 < input.Length) {
                    current.Append(input[++i]);
                } else {
                    current.Append(c);
                }

                hasToken = true;

                continue;
            }

            switch (c) {
                case '\'' or '"':
                    quote    = c;
                    hasToken = true;

                    continue;
                case '\\' when i + 1 < input.Length:
                    current.Append(input[++i]);
                    hasToken = true;

                    continue;
            }

            // An unquoted line break ends a command the way `;` does: folding it into whitespace
            // would read `rg foo src⏎rm -rf tmp` as one search and hide the mutation.
            if (c is '\n' or '\r') {
                Flush();
                result.Add(";");

                continue;
            }

            if (char.IsWhiteSpace(c)) {
                Flush();

                continue;
            }

            // Connectors.
            if (c == '|') {
                Flush();

                if (i + 1 < input.Length && input[i + 1] == '|') {
                    result.Add("||");
                    i++;
                } else {
                    result.Add("|");
                }

                continue;
            }

            if (c == '&' && i + 1 < input.Length && input[i + 1] == '&') {
                Flush();
                result.Add("&&");
                i++;

                continue;
            }

            if (c == ';') {
                Flush();
                result.Add(";");

                continue;
            }

            current.Append(c);
            hasToken = true;
        }

        Flush();

        return result;
    }
}
