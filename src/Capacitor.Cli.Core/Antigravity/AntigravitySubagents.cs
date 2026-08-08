using System.Text.Json;

namespace Capacitor.Cli.Core.Antigravity;

/// <summary>
/// Resolves Antigravity's subagent → parent conversation linkage for historical import. A
/// subagent is a SEPARATE conversation; the linkage is derived from the
/// parent transcript's <c>INVOKE_SUBAGENT</c> steps (the spawn-time signal — same source the
/// live watcher now uses, see <see cref="ChildConversationIdsFromLine"/>), which is available
/// even for a child that never reports back. This replaces the
/// child-reports-back scan of <c>brain/&lt;parent&gt;/.system_generated/messages/*.json</c>,
/// which silently dropped errored/never-reporting children (see d9956b89) and had the
/// sender/recipient direction inverted in some captures (see 7f8d9d93 → a1204f98). Roots are
/// conversations that never appear as an invoked child.
/// </summary>
public static class AntigravitySubagents {
    /// <summary>
    /// Builds child-conversation-id → parent-conversation-id by scanning every brain dir's
    /// <c>transcript_full.jsonl</c> for INVOKE_SUBAGENT steps. Best-effort: unreadable /
    /// truncated transcripts are skipped. If a child is invoked by MULTIPLE parents
    /// (pathological / non-tree data), the lexicographically-smallest parent wins so the map is
    /// deterministic regardless of directory enumeration order.
    /// </summary>
    public static IReadOnlyDictionary<string, string> BuildParentMap(
            string? home = null, string? geminiCliHome = null, CancellationToken ct = default)
        => BuildParentMapUnder(Path.Combine(AntigravityPaths.Root(home, geminiCliHome), "brain"), ct);

    /// <summary>
    /// <see cref="BuildParentMap(string?,string?,CancellationToken)"/> over an EXPLICIT brain root.
    /// Import scans each product root's brain separately: a subagent chain never crosses roots
    /// (child <c>messages/</c> linkage lives beside the parent under one root, and conversation ids
    /// are unique UUIDs), so per-root maps are complete and need no union.
    /// </summary>
    public static IReadOnlyDictionary<string, string> BuildParentMapUnder(
            string brainRoot, CancellationToken ct = default) {
        var map = new Dictionary<string, string>(StringComparer.Ordinal);

        if (!Directory.Exists(brainRoot)) return map;

        foreach (var brainDir in Directory.EnumerateDirectories(brainRoot)) {
            ct.ThrowIfCancellationRequested();
            var parent = Path.GetFileName(brainDir);
            var transcript = Path.Combine(brainDir, ".system_generated", "logs", "transcript_full.jsonl");
            if (!File.Exists(transcript)) continue;

            foreach (var line in EnumerateLinesSafe(transcript)) {
                ct.ThrowIfCancellationRequested();
                foreach (var child in ChildConversationIdsFromLine(line)) {
                    if (string.Equals(child, parent, StringComparison.Ordinal)) continue;   // self-guard
                    // First-wins on conflict, deterministic: keep the smallest parent (Ordinal).
                    if (!map.TryGetValue(child, out var existing)
                     || string.CompareOrdinal(parent, existing) < 0)
                        map[child] = parent;
                }
            }
        }

        return map;
    }

    static IEnumerable<string> EnumerateLinesSafe(string path) {
        IEnumerator<string> e;
        try { e = File.ReadLines(path).GetEnumerator(); }
        catch { yield break; }        // unreadable transcript — skip the whole file
        using (e) {
            while (true) {
                try { if (!e.MoveNext()) yield break; }
                catch { yield break; } // truncated/locked mid-read — stop, keep what we have
                yield return e.Current;
            }
        }
    }

    /// <summary>Parent conversation id for a child, or null when it is a root / unlinked.</summary>
    public static string? ResolveParent(string childConversationId, string? home = null, string? geminiCliHome = null) =>
        BuildParentMap(home, geminiCliHome).TryGetValue(childConversationId, out var parent) ? parent : null;

    /// <summary>
    /// Walks parent links up to the top-level ancestor (a conversation with no parent), so a
    /// deep chain P←C←G resolves G's ancestor to P. Cycle-safe: if a cycle is encountered
    /// (A↔B, or a child that is also its own ancestor) the starting conversation is returned as
    /// its own ancestor, so cycle members import standalone rather than being silently lost.
    /// A conversation with no parent returns itself.
    /// </summary>
    public static string ResolveTopLevelAncestor(string convId, IReadOnlyDictionary<string, string> parentMap) {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var cur  = convId;
        while (parentMap.TryGetValue(cur, out var parent)) {
            if (!seen.Add(cur)) return convId; // cycle → treat the origin as its own root
            cur = parent;
        }
        return cur;
    }

    /// <summary>
    /// Groups every conversation under its top-level ancestor: returns rootConvId → all
    /// transitive descendant convIds (children, grandchildren, …). Keys are the roots (import
    /// as sessions); each value list is imported as subagents of that root. Non-tree edges are
    /// handled by <see cref="ResolveTopLevelAncestor"/>. <paramref name="allConversationIds"/>
    /// is the full set of brain-dir conversation ids (a root with no messages still appears).
    /// </summary>
    public static IReadOnlyDictionary<string, List<string>> BuildRootDescendants(
            IEnumerable<string> allConversationIds, IReadOnlyDictionary<string, string> parentMap) {
        var byRoot = new Dictionary<string, List<string>>(StringComparer.Ordinal);

        foreach (var convId in allConversationIds) {
            var ancestor = ResolveTopLevelAncestor(convId, parentMap);
            if (!byRoot.TryGetValue(ancestor, out var list))
                byRoot[ancestor] = list = [];
            if (!string.Equals(convId, ancestor, StringComparison.Ordinal))
                list.Add(convId); // a descendant of its ancestor root
        }

        return byRoot;
    }

    /// <summary>
    /// The authoritative spawn-time parent→child signal. A parent's transcript
    /// records an INVOKE_SUBAGENT step whose <c>content</c> embeds JSON listing the child
    /// conversation(s) it spawned (<c>{ "conversationId": "&lt;child&gt;", … }</c>, one object or an
    /// array). Returns the child ids for a single transcript line — empty for any non-INVOKE_SUBAGENT,
    /// blank, or malformed line. Strict: the id is read ONLY from the step's content payload (parsed
    /// as JSON), not matched by a regex over the whole line, and each id must be GUID-shaped.
    /// </summary>
    public static IReadOnlyList<string> ChildConversationIdsFromLine(string line) {
        if (!TryReadInvokeContent(line, out var content)) return [];

        var raw = new List<string>();
        // The content can embed more than one top-level JSON value (e.g. several
        // "{ \"conversationId\": … }" objects separated by narrative text/newlines), so scan for
        // every balanced block rather than stopping at the first.
        foreach (var json in ExtractJsonBlocks(content)) {
            try {
                using var doc = JsonDocument.Parse(json);
                CollectConversationIds(doc.RootElement, raw);
            } catch {
                // Skip an unparsable block; a sibling block may still be valid.
            }
        }

        var seen   = new HashSet<string>(StringComparer.Ordinal);
        var result = new List<string>();
        foreach (var id in raw) {
            if (!Guid.TryParse(id, out _)) continue;   // GUID-shaped only; brain-dir ids are dashed
            if (seen.Add(id)) result.Add(id);
        }
        return result;
    }

    /// <summary>True iff the line is a structurally-valid INVOKE_SUBAGENT step (used by callers to
    /// log a drift diagnostic when such a step yields no parseable child id).</summary>
    public static bool IsInvokeSubagentLine(string line) => TryReadInvokeContent(line, out _);

    static bool TryReadInvokeContent(string line, out string content) {
        content = "";
        if (string.IsNullOrWhiteSpace(line)) return false;
        try {
            using var doc = JsonDocument.Parse(line);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return false;
            if (!root.TryGetProperty("type", out var t) || t.ValueKind != JsonValueKind.String
             || t.GetString() != "INVOKE_SUBAGENT") return false;
            if (!root.TryGetProperty("content", out var c) || c.ValueKind != JsonValueKind.String) {
                content = "";                 // an INVOKE step with no string content is still an invoke line
                return true;
            }
            content = c.GetString() ?? "";
            return true;
        } catch {
            return false;
        }
    }

    /// <summary>Every balanced {…} or […] block found in <paramref name="s"/>, scanning left to
    /// right (content can embed several sibling JSON values, not just one). String-aware so braces
    /// inside quoted text don't break balancing. An unbalanced/truncated trailing block is dropped
    /// rather than yielded partially.</summary>
    static List<string> ExtractJsonBlocks(string s) {
        var blocks = new List<string>();
        var i = 0;
        while (i < s.Length) {
            var start = s.IndexOfAny(['{', '['], i);
            if (start < 0) break;
            char open = s[start], close = open == '{' ? '}' : ']';
            int depth = 0; bool inStr = false, esc = false;
            var end = -1;
            for (var j = start; j < s.Length; j++) {
                var ch = s[j];
                if (inStr) {
                    if (esc) esc = false;
                    else if (ch == '\\') esc = true;
                    else if (ch == '"') inStr = false;
                    continue;
                }
                if (ch == '"') inStr = true;
                else if (ch == open) depth++;
                else if (ch == close && --depth == 0) { end = j; break; }
            }
            if (end < 0) break;   // unbalanced / truncated — stop scanning
            blocks.Add(s.Substring(start, end - start + 1));
            i = end + 1;
        }
        return blocks;
    }

    static void CollectConversationIds(JsonElement el, List<string> into) {
        switch (el.ValueKind) {
            case JsonValueKind.Object:
                foreach (var p in el.EnumerateObject()) {
                    if (p.NameEquals("conversationId") && p.Value.ValueKind == JsonValueKind.String)
                        into.Add(p.Value.GetString()!);
                    else
                        CollectConversationIds(p.Value, into);
                }
                break;
            case JsonValueKind.Array:
                foreach (var item in el.EnumerateArray()) CollectConversationIds(item, into);
                break;
        }
    }
}
