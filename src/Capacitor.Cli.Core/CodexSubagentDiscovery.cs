using System.Text.Json;
using System.Text.Json.Nodes;

namespace Capacitor.Cli.Core;

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
    /// <c>session_id</c> — see the class doc trap). Null when the first non-blank line is not
    /// yet a parseable <c>session_meta</c> (file mid-write) — callers must retry, not cache.
    /// </summary>
    public readonly record struct RolloutMeta(
        string? IdDashless,
        string? ParentThreadIdDashless,
        bool    IsSubagent,
        string? AgentPath,
        string? AgentNickname);

    /// <summary>
    /// Reads the first non-blank line of <paramref name="rolloutPath"/> and extracts the
    /// subagent linkage facts. Returns null when the file is missing/unreadable or the line
    /// is not (yet) a parseable <c>session_meta</c> envelope — a rollout whose header is still
    /// being written must be retried, never negatively cached.
    /// </summary>
    public static RolloutMeta? TryReadMeta(string rolloutPath) {
        try {
            using var stream = new FileStream(rolloutPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var reader = new StreamReader(stream);

            while (reader.ReadLine() is { } line) {
                if (string.IsNullOrWhiteSpace(line)) continue;

                using var doc  = JsonDocument.Parse(line);
                var       root = doc.RootElement;

                if (root.Str("type") != "session_meta" || root.Obj("payload") is not { } payload) return null;

                // thread_source is the primary signal; the source.subagent object is the
                // belt-and-braces fallback in case a future Codex drops one but not the other.
                var isSubagent = payload.Str("thread_source") == "subagent"
                              || payload.Obj("source")?.Obj("subagent") is not null;

                return new RolloutMeta(
                    IdDashless:             Dashless(payload.Str("id")),
                    ParentThreadIdDashless: Dashless(payload.Str("parent_thread_id")),
                    IsSubagent:             isSubagent,
                    AgentPath:              payload.Str("agent_path"),
                    AgentNickname:          payload.Str("agent_nickname"));
            }
        } catch {
            // Missing, locked, or header mid-write — caller retries next tick.
        }

        return null;
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

        foreach (var (childId, filePath, _) in CodexPaths.Discover(sessionsDir: root, since: since)) {
            if (filePath == parentTranscriptPath) continue;
            if (ruledOut?.Contains(filePath) == true) continue;

            if (TryReadMeta(filePath) is not { } meta) continue; // header mid-write — retry next tick

            if (meta.IsSubagent && meta.ParentThreadIdDashless == parentDashlessId) {
                results.Add(new SubagentRollout(filePath, meta.IdDashless ?? childId, meta.AgentPath, meta.AgentNickname));
            } else {
                ruledOut?.Add(filePath); // a header, once written, never changes — definitive
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
