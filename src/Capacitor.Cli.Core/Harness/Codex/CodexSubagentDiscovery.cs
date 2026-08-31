using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Capacitor.Cli.Core.Harness.Codex;

/// <summary>
/// Discovery + lifecycle-payload glue for Codex collab subagent rollouts (Codex CLI 0.146+,
/// <c>multi_agent_version: v2</c>). A parent session's <c>spawn_agent</c> forks each subagent
/// into its OWN rollout file under the shared <c>~/.codex/sessions/YYYY/MM/DD/</c> tree; the
/// child's opening <c>session_meta</c> carries the linkage — <c>thread_source: "subagent"</c>,
/// <c>parent_thread_id</c>, <c>agent_path</c> (e.g. <c>/root/spec_quality</c>) and
/// <c>agent_nickname</c>. TRAP: in a child's <c>session_meta</c> the <c>session_id</c> field
/// holds the PARENT's id — the child's own id lives in <c>id</c> (and the filename suffix).
///
/// Shared by the live watcher (<c>WatchCommand.ScanCodexSubagents</c>), the parent-exit
/// teardown (<c>CodexSubagentTeardown</c>) and the historical import
/// (<c>SessionImporter.ImportSessionAsync</c>), so all three speak one wire contract —
/// mirroring <c>GeminiSubagentDiscovery</c> / <c>OpenCodeSubagentDiscovery</c>.
/// </summary>
public static class CodexSubagentDiscovery {
    /// <summary>One discovered subagent rollout belonging to a given parent thread.</summary>
    public readonly record struct SubagentRollout(
        string  FilePath,
        string  ChildDashlessId,
        string? AgentPath,
        string? AgentNickname);

    /// <summary>
    /// Linkage facts parsed from a rollout's opening <c>session_meta</c> line.
    /// <see cref="IdDashless"/> is the rollout's OWN id (payload <c>id</c>, never
    /// <c>session_id</c> — see the class doc trap).
    /// </summary>
    public readonly record struct RolloutMeta(
        string? IdDashless,
        string? ParentThreadIdDashless,
        string? AgentPath,
        string? AgentNickname);

    /// <summary>
    /// Tri-state classification of a rollout's header. The Subagent/NotSubagent verdicts are
    /// DEFINITIVE (a header, once written, never changes) and safe to cache;
    /// <see cref="Indeterminate"/> means the header could not be judged YET (empty file,
    /// truncated first line still being written, I/O error) and must be retried — never cached.
    /// </summary>
    public enum RolloutHeader {
        /// <summary>A collab subagent rollout (thread_source/source.subagent linkage present).</summary>
        Subagent,
        /// <summary>Definitively not a subagent: a top-level session's header, or a COMPLETE
        /// first line that is not a parseable <c>session_meta</c> at all (a permanently
        /// malformed file must not be re-opened on every polling tick).</summary>
        NotSubagent,
        /// <summary>Header mid-write or unreadable — retry, don't cache.</summary>
        Indeterminate,
    }

    /// <summary>Outcome + parsed linkage of one header read. <see cref="Meta"/> is only
    /// meaningful for <see cref="RolloutHeader.Subagent"/>/<see cref="RolloutHeader.NotSubagent"/>
    /// verdicts that actually parsed a <c>session_meta</c>; default otherwise.</summary>
    public readonly record struct HeaderReadResult(RolloutHeader Outcome, RolloutMeta Meta);

    /// <summary>
    /// Ceiling on how many bytes of a rollout are scanned for the first newline. A real
    /// <c>session_meta</c> line (even with large embedded <c>base_instructions</c>) is tens of
    /// KB; a file whose first "line" exceeds this is pathological and classified
    /// <see cref="RolloutHeader.NotSubagent"/> rather than re-read forever.
    /// </summary>
    const int MaxHeaderBytes = 4 * 1024 * 1024;

    /// <summary>
    /// Reads and classifies the first non-blank line of <paramref name="rolloutPath"/>.
    /// The completeness of the line decides how a parse failure is treated: a
    /// newline-terminated line that isn't a parseable <c>session_meta</c> is DEFINITIVELY
    /// <see cref="RolloutHeader.NotSubagent"/> (permanently malformed files must be cacheable
    /// as ruled-out), while an EOF-truncated line is <see cref="RolloutHeader.Indeterminate"/>
    /// (mid-write — retry). A truncated line that nonetheless parses as a complete
    /// <c>session_meta</c> is judged on its content (the writer just hasn't flushed the
    /// newline yet).
    /// </summary>
    public static HeaderReadResult ReadHeader(string rolloutPath) {
        string? line;
        bool    lineComplete;

        try {
            (line, lineComplete) = ReadFirstNonBlankLine(rolloutPath);
        } catch {
            return new(RolloutHeader.Indeterminate, default); // missing/locked — retry
        }

        if (line is null) return new(RolloutHeader.Indeterminate, default); // empty file — just created

        try {
            using var doc  = JsonDocument.Parse(line);
            var       root = doc.RootElement;

            if (root.Str("type") != "session_meta" || root.Obj("payload") is not { } payload) {
                // Parsed fine but the wrong shape — a definitive non-header, not a mid-write.
                return new(RolloutHeader.NotSubagent, default);
            }

            // thread_source is the primary signal; the source.subagent object is the
            // belt-and-braces fallback in case a future Codex drops one but not the other.
            var isSubagent = payload.Str("thread_source") == "subagent"
                          || payload.Obj("source")?.Obj("subagent") is not null;

            var meta = new RolloutMeta(
                IdDashless:             Dashless(payload.Str("id")),
                ParentThreadIdDashless: Dashless(payload.Str("parent_thread_id")),
                AgentPath:              payload.Str("agent_path"),
                AgentNickname:          payload.Str("agent_nickname"));

            return new(isSubagent ? RolloutHeader.Subagent : RolloutHeader.NotSubagent, meta);
        } catch (JsonException) {
            return new(lineComplete ? RolloutHeader.NotSubagent : RolloutHeader.Indeterminate, default);
        }
    }

    /// <summary>
    /// First non-blank line of the file plus whether it was newline-terminated (Complete) or
    /// ended at EOF (possibly mid-write). Reads with ReadWrite sharing — Codex is appending.
    /// A line that hits <see cref="MaxHeaderBytes"/> without a newline reports Complete so the
    /// caller classifies it definitively instead of retrying forever.
    /// </summary>
    static (string? Line, bool Complete) ReadFirstNonBlankLine(string path) {
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var ms = new MemoryStream();

        var buf       = new byte[8192];
        var lineStart = 0;
        var scanned   = 0;

        while (ms.Length < MaxHeaderBytes) {
            var n = fs.Read(buf, 0, buf.Length);
            if (n == 0) break;

            ms.Write(buf, 0, n);
            var arr = ms.GetBuffer();

            for (var i = scanned; i < ms.Length; i++) {
                if (arr[i] != (byte)'\n') continue;

                var text = Encoding.UTF8.GetString(arr, lineStart, i - lineStart).TrimEnd('\r');

                if (text.AsSpan().Trim().Length == 0) {
                    lineStart = i + 1; // blank line — keep looking
                    continue;
                }

                return (text, true);
            }

            scanned = (int)ms.Length;
        }

        var tail = Encoding.UTF8.GetString(ms.GetBuffer(), lineStart, (int)ms.Length - lineStart).TrimEnd('\r');

        if (tail.AsSpan().Trim().Length == 0) return (null, false);

        // Cap hit without a newline: report Complete so the pathological file is judged
        // definitively (it can never become a valid header by growing further).
        return (tail, ms.Length >= MaxHeaderBytes);
    }

    /// <summary>
    /// Enumerates rollouts in the shared sessions tree that are subagent children of
    /// <paramref name="parentDashlessId"/>. Scans day directories from the parent rollout's own
    /// date forward (children are always spawned after the parent started; the walk reuses
    /// <see cref="CodexPaths.Discover"/>'s date pruning so an old history is never re-walked).
    /// Definitive non-children (a top-level session, or another parent's child) are added to
    /// <paramref name="ruledOut"/> so a polling caller re-reads each foreign header exactly
    /// once; an unreadable/mid-write header is skipped WITHOUT caching so it is retried.
    /// Single-level: a grandchild (child of a child) is ruled out here — the live watcher
    /// nests one level, mirroring Gemini; the import path recurses via
    /// <see cref="EnumerateDescendantRollouts"/>.
    /// </summary>
    public static List<SubagentRollout> EnumerateSubagentRollouts(
            string        parentTranscriptPath,
            string        parentDashlessId,
            ISet<string>? ruledOut = null
        ) {
        var results = new List<SubagentRollout>();

        if (SessionsRootFor(parentTranscriptPath) is not { } root) return results;

        var since = DayDirDate(parentTranscriptPath);

        foreach (var (childId, filePath, _) in CodexPaths.Discover(root, since)) {
            if (filePath == parentTranscriptPath) continue;
            if (ruledOut?.Contains(filePath) == true) continue;

            var (outcome, meta) = ReadHeader(filePath);

            switch (outcome) {
                case RolloutHeader.Subagent when meta.ParentThreadIdDashless == parentDashlessId:
                    results.Add(new SubagentRollout(filePath, meta.IdDashless ?? childId, meta.AgentPath, meta.AgentNickname));
                    break;

                case RolloutHeader.Subagent:    // another parent's child
                case RolloutHeader.NotSubagent: // top-level session, or permanently malformed header
                    ruledOut?.Add(filePath); // a header, once written, never changes — definitive
                    break;

                // Indeterminate: header mid-write — retry next tick, never cache.
            }
        }

        return results;
    }

    /// <summary>Import-side recursion depth cap, mirroring
    /// <c>GeminiSubagentDiscovery.MaxDescendantDepth</c> — a descendant beyond this is skipped.</summary>
    public const int MaxDescendantDepth = 8;

    /// <summary>
    /// Recursively discovers every TRANSITIVE descendant subagent rollout of the given root —
    /// the root's own children, then each child's children, and so on (deterministic BFS,
    /// visited-set cycle guard, depth-capped at <see cref="MaxDescendantDepth"/>). Used by the
    /// import path, which — like Gemini's — imports every descendant as a DIRECT subagent of
    /// the top-level root (the server's <c>AgentSubsession-{sid}-{agentId}</c> model is flat).
    /// </summary>
    public static List<SubagentRollout> EnumerateDescendantRollouts(string rootTranscriptPath, string rootDashlessId) {
        var results  = new List<SubagentRollout>();
        var visited  = new HashSet<string>(StringComparer.Ordinal) { rootDashlessId };
        var frontier = new Queue<(string Path, string Id, int Depth)>();
        frontier.Enqueue((rootTranscriptPath, rootDashlessId, 0));

        while (frontier.Count > 0) {
            var (path, id, depth) = frontier.Dequeue();
            if (depth >= MaxDescendantDepth) continue;

            foreach (var sub in EnumerateSubagentRollouts(path, id).OrderBy(s => s.FilePath, StringComparer.Ordinal)) {
                if (!visited.Add(sub.ChildDashlessId)) continue;

                results.Add(sub);
                frontier.Enqueue((sub.FilePath, sub.ChildDashlessId, depth + 1));
            }
        }

        return results;
    }

    /// <summary>
    /// Display/type label for a subagent: the last segment of its <c>agent_path</c>
    /// (<c>/root/spec_quality</c> → <c>spec_quality</c>), else its nickname, else
    /// <c>"subagent"</c> — the same fallback family Gemini/OpenCode use.
    /// </summary>
    public static string AgentTypeFrom(string? agentPath, string? agentNickname) {
        var leaf = agentPath?.TrimEnd('/').Split('/').LastOrDefault();

        if (!string.IsNullOrWhiteSpace(leaf)) return leaf;
        if (!string.IsNullOrWhiteSpace(agentNickname)) return agentNickname;

        return "subagent";
    }

    // ── Hook payloads (the vendor-agnostic /hooks/subagent-start|stop wire shape) ──

    /// <summary><c>/hooks/subagent-start</c> body. cwd is "" — the child inherits the parent
    /// session's cwd; the server's HookBase merely requires the (non-null) field
    /// (same posture as Gemini/OpenCode subagents).</summary>
    public static JsonObject BuildStartPayload(string parentSessionId, string agentId, string agentType, string subagentTranscriptPath) =>
        new() {
            ["hook_event_name"] = "subagent_start",
            ["session_id"]      = parentSessionId,
            ["agent_id"]        = agentId,
            ["agent_type"]      = agentType,
            ["transcript_path"] = subagentTranscriptPath,
            ["cwd"]             = "",
        };

    /// <summary><c>/hooks/subagent-stop</c> body.</summary>
    public static JsonObject BuildStopPayload(string parentSessionId, string agentId, string agentType, string subagentTranscriptPath) =>
        new() {
            ["hook_event_name"]        = "subagent_stop",
            ["session_id"]             = parentSessionId,
            ["agent_id"]               = agentId,
            ["agent_type"]             = agentType,
            ["transcript_path"]        = subagentTranscriptPath,
            ["cwd"]                    = "",
            ["stop_hook_active"]       = false,
            ["agent_transcript_path"]  = subagentTranscriptPath,
            ["last_assistant_message"] = "",
        };

    /// <summary>The <c>~/.codex/sessions</c> root above a <c>YYYY/MM/DD/rollout-*.jsonl</c> path
    /// (three levels up), or null when the path is too shallow to carry the day tree.</summary>
    static string? SessionsRootFor(string rolloutPath) =>
        Path.GetDirectoryName(rolloutPath) is { } day
     && Path.GetDirectoryName(day) is { } month
     && Path.GetDirectoryName(month) is { } year
            ? Path.GetDirectoryName(year)
            : null;

    /// <summary>
    /// The rollout's local date parsed from its <c>YYYY/MM/DD</c> day-directory components —
    /// the <c>since</c> prune bound for the child scan. Codex names both the day folders and
    /// the filename stamp in LOCAL time, so the directory components are authoritative. Null
    /// (scan everything) when the path doesn't sit in a well-formed day tree.
    /// </summary>
    static DateOnly? DayDirDate(string rolloutPath) {
        var day   = Path.GetDirectoryName(rolloutPath);
        var month = Path.GetDirectoryName(day);
        var year  = Path.GetDirectoryName(month);

        return int.TryParse(Path.GetFileName(day), out var d)
            && int.TryParse(Path.GetFileName(month), out var m)
            && int.TryParse(Path.GetFileName(year), out var y)
            && y is >= 1 and <= 9999 && m is >= 1 and <= 12 && d is >= 1 and <= 31
                ? DateOnly.TryParse($"{y:D4}-{m:D2}-{d:D2}", out var date) ? date : null
                : null;
    }

    /// <summary>Normalizes a dashed or dashless GUID string to dashless lowercase; null in → null out.</summary>
    static string? Dashless(string? id) =>
        id is not null && Guid.TryParse(id, out var guid) ? guid.ToString("N") : null;
}
