using System.Text.Json;
using System.Text.Json.Nodes;

namespace Capacitor.Cli.Core.Gemini;

/// <summary>
/// Discovery + lifecycle-payload glue for Gemini's nested subagent transcripts.
/// Gemini records each subagent in <c>chats/&lt;parentSessionId&gt;/&lt;subId&gt;.jsonl</c>;
/// the parent's <c>invoke_agent</c> tool call persists <c>agentId == subId</c> plus
/// <c>args.agent_name</c>. Shared by the historical import path (<c>GeminiImportSource</c>)
/// and the live watcher (<c>WatchCommand</c> / <c>GeminiHookCommand</c>) so both speak one
/// wire contract.
/// </summary>
public static class GeminiSubagentDiscovery {
    /// <summary>Dashed parent sessionId from a main transcript's header (first line), or null.</summary>
    public static string? ReadParentSessionId(string transcriptPath) {
        try {
            foreach (var line in File.ReadLines(transcriptPath)) {
                if (string.IsNullOrWhiteSpace(line)) continue;

                using var doc = JsonDocument.Parse(line);
                return doc.RootElement.Str("sessionId");
            }
        } catch { /* unreadable / malformed → no subagents */ }

        return null;
    }

    /// <summary>
    /// Nested subagent <c>*.jsonl</c> files recorded alongside a parent main transcript
    /// (empty when the session spawned none). Path: <c>chats/&lt;dashedParent&gt;/*.jsonl</c>.
    /// Single-level — shared by the live watcher/teardown paths, which stay single-level for
    /// now (the recursive fix is import-only; see <see cref="EnumerateDescendantFiles(string)"/>).
    /// </summary>
    public static IReadOnlyList<string> EnumerateSubagentFiles(string transcriptPath) {
        if (ReadParentSessionId(transcriptPath) is not { } dashedParent) return [];
        if (Path.GetDirectoryName(transcriptPath) is not { } chatsDir) return [];

        var subDir = GeminiPaths.SubagentDir(chatsDir, dashedParent);
        if (!Directory.Exists(subDir)) return [];

        return Directory.EnumerateFiles(subDir, "*.jsonl").ToList();
    }

    /// <summary>Import-side recursion depth cap — a descendant beyond this depth
    /// is neither imported nor promoted; the caller surfaces the count.</summary>
    public const int MaxDescendantDepth = 8;

    /// <summary>
    /// Hard ceiling on the total number of BELOW-CAP (omitted) nodes visited by the counting
    /// walk — mirrors <c>OpenCodeDb.MaxCountingNodes</c> for symmetry. This bounds ONLY the walk into
    /// descendants already beyond <see cref="MaxDescendantDepth"/> — it never applies to in-cap
    /// import discovery, which is always complete (see <see cref="EnumerateDescendantFiles(string)"/>).
    /// The visited-id set already prevents an infinite loop on a reachable cycle; this ceiling
    /// instead bounds the total work a pathologically huge or wide UNIMPORTED subtree could
    /// otherwise force onto a single import pass. A real Gemini subagent tree tops out at a
    /// handful of nodes, so this is a generous safety valve, not a realistic limit — if it's
    /// ever hit, <see cref="DescendantDiscoveryResult.CountTruncated"/> is set so the omitted
    /// count/ids are treated as a LOWER BOUND rather than silently corrupting anything.
    /// </summary>
    public const int MaxCountingNodes = 10_000;

    /// <summary>
    /// One recursively-discovered descendant subagent file. <c>ImmediateParentTranscriptPath</c>
    /// is THIS descendant's own immediate parent's transcript — NOT the root's — because a
    /// grandchild's <c>agent_name</c>/invocation-type mapping lives in its immediate parent's
    /// <c>invoke_agent</c> tool call, not the root's. <c>DashedId</c> is the
    /// descendant's own dashed session id (the filename stem), and <c>Depth</c> is 1 for a
    /// direct child of the root, 2 for a grandchild, etc.
    /// </summary>
    public readonly record struct DescendantFile(
        string File,
        string ImmediateParentTranscriptPath,
        string DashedId,
        int    Depth);

    /// <summary>Recursive descendant discovery result. <see cref="Files"/> holds EVERY
    /// descendant WITHIN <see cref="MaxDescendantDepth"/> — the import set — and is NEVER
    /// truncated by <see cref="MaxCountingNodes"/>: in-cap discovery is unbounded.
    /// <see cref="DescendantsOmitted"/> is the count of every descendant BELOW the cap, not
    /// merely the boundary children immediately past it — a chain through depths 9 and 10
    /// reports 2, not 1 — UNLESS <see cref="CountTruncated"/> is true, in which case it (and
    /// <see cref="OmittedDescendantIds"/>) is a LOWER BOUND: the below-cap counting walk hit
    /// <see cref="MaxCountingNodes"/> before it could finish, so the true omitted count/identity
    /// set may be larger. <see cref="UnreadableDescendantDirs"/> counts a descendant directory
    /// that exists but couldn't be read (permissions, I/O error) — its subtree is silently
    /// unexplored otherwise, so this makes that observable too. Never silent — the caller
    /// surfaces the count (and any truncation) in the import summary.</summary>
    public readonly record struct DescendantDiscoveryResult(
        IReadOnlyList<DescendantFile> Files,
        int                           DescendantsOmitted,
        IReadOnlyList<string>         OmittedDescendantIds,
        bool                          CountTruncated,
        int                           UnreadableDescendantDirs);

    /// <summary>
    /// Recursively discovers every descendant subagent transcript under
    /// <paramref name="rootTranscriptPath"/>: the root's own <c>chats/&lt;root&gt;/*.jsonl</c>,
    /// then — for each discovered subagent — that subagent's OWN <c>chats/&lt;sub&gt;/*.jsonl</c>,
    /// and so on. Child directories are always derived from the ROOT's <c>chats/</c> directory,
    /// never relative to a nested dir. Deterministic BFS (files ordered by filename at each
    /// level); a visited-id set guards against a reachable cycle (it simply stops re-descending,
    /// terminating the walk). In-cap discovery (depth &lt;= <see cref="MaxDescendantDepth"/>) is
    /// ALWAYS complete — never bounded by <see cref="MaxCountingNodes"/>. Depth &gt;
    /// <see cref="MaxDescendantDepth"/> is never imported, but the walk still recurses into it
    /// (same visited-set guard) purely to COUNT the whole omitted subtree accurately and surface
    /// its identities — this below-cap-only counting/signature walk is what
    /// <see cref="MaxCountingNodes"/> bounds; if it's hit,
    /// <see cref="DescendantDiscoveryResult.CountTruncated"/> is set and the omitted count/ids
    /// become a lower bound instead of silently under-reporting as complete. Import-only for now
    /// (see <see cref="EnumerateSubagentFiles"/>).
    ///
    /// <para>The in-cap and below-cap walks run over two INDEPENDENT frontiers, mirroring
    /// <c>OpenCodeDb.QueryDescendants</c>. A below-cap descendant's own descendants are always
    /// below-cap too (depth only grows), so the split is safe and lets the below-cap walk be
    /// abandoned the instant it truncates, without disturbing the in-cap walk's completeness
    /// guarantee: once truncation is established, the below-cap frontier is dropped rather than
    /// drained node-by-node.</para>
    ///
    /// <para>A below-cap parent's own directory read goes through a bounded enumerator capped to
    /// (remaining counting capacity + 1 sentinel entry) rather than the unbounded, full-sort
    /// enumerator used for in-cap parents — since a child is only ever enqueued onto the in-cap
    /// frontier when its own depth is &lt;= <see cref="MaxDescendantDepth"/>, a dequeued node at
    /// exactly that depth has children that are ALL necessarily below the cap, so a single parent
    /// with an enormous below-cap fan-out can't force listing (and sorting) its entire directory
    /// before truncation is even detectable.</para>
    ///
    /// <para>The bounded enumerator reports whether the RAW read actually reached <c>limit</c>
    /// entries, not merely how many of them turned out to be well-formed/unvisited — a malformed
    /// or already-visited entry landing inside the bounded window would otherwise consume the
    /// sole sentinel slot without itself counting as omitted, letting a genuinely-omitted
    /// descendant just past it go undetected. When the raw read hits the limit, truncation is
    /// asserted regardless of how many entries turned out valid; only a raw count strictly below
    /// the limit is definite EOF. This can over-report truncation in the rare case where a
    /// directory holds exactly <c>limit</c> raw entries and no more — safe, since a truncated
    /// result is already documented as a lower bound.</para>
    /// </summary>
    public static DescendantDiscoveryResult EnumerateDescendantFiles(string rootTranscriptPath) =>
        EnumerateDescendantFiles(rootTranscriptPath, onDequeueBelowCapNode: null);

    /// <summary>
    /// Internal-only overload with test probes into the below-cap walk.
    /// <paramref name="onDequeueBelowCapNode"/> fires once per below-cap frontier node actually
    /// dequeued and directory-enumerated, letting a regression test prove the walk stops
    /// expanding the below-cap frontier the instant <see cref="DescendantDiscoveryResult.CountTruncated"/>
    /// is established, instead of draining every already-enqueued below-cap node to completion.
    /// <paramref name="onBelowCapDirectoryRead"/> fires once per bounded below-cap directory read
    /// with the number of raw directory entries actually pulled from the OS enumeration to
    /// satisfy it, letting a regression test prove a single below-cap parent's own directory read
    /// never pulls more than (remaining counting capacity + 1) entries, no matter how many files
    /// that directory truly holds.
    /// </summary>
    internal static DescendantDiscoveryResult EnumerateDescendantFiles(
        string rootTranscriptPath, Action? onDequeueBelowCapNode, Action<int>? onBelowCapDirectoryRead = null) {
        if (ReadParentSessionId(rootTranscriptPath) is not { } rootDashedId) return new([], 0, [], false, 0);
        if (Path.GetDirectoryName(rootTranscriptPath) is not { } chatsDir) return new([], 0, [], false, 0);

        var files           = new List<DescendantFile>();
        var visited         = new HashSet<string>(StringComparer.Ordinal) { rootDashedId };
        var omittedIds      = new List<string>();
        var countTruncated  = false;
        var unreadableDirs  = 0;

        var inCapFrontier    = new Queue<(string TranscriptPath, string DashedId, int Depth)>();
        var belowCapFrontier = new Queue<(string TranscriptPath, string DashedId)>();
        inCapFrontier.Enqueue((rootTranscriptPath, rootDashedId, 0));

        // Reads (and orders) one node's own subagent directory, or null when it doesn't exist
        // or can't be read (a hostile/inaccessible nested dir must not abort the whole walk —
        // it's counted in `unreadableDirs` instead of dropping the subtree silently). Only for
        // an IN-CAP parent (depth < MaxDescendantDepth) — its children are always in-cap, so
        // this must stay unbounded/always-complete.
        List<string>? EnumerateChildFiles(string dashedId) {
            var subDir = GeminiPaths.SubagentDir(chatsDir, dashedId);
            if (!Directory.Exists(subDir)) return null;
            try {
                return Directory.EnumerateFiles(subDir, "*.jsonl").OrderBy(f => f, StringComparer.Ordinal).ToList();
            } catch {
                unreadableDirs++;
                return null;
            }
        }

        // Cap-aware variant for a parent whose children are NECESSARILY below
        // MaxDescendantDepth — every file under `dashedId` is beyond the import cap and can
        // never be imported, so take at most `limit` entries via a lazy Take() instead of
        // enumerating (and OrderBy-sorting) the WHOLE directory. A single pathologically large
        // below-cap directory can no longer force an unbounded read/allocation/sort before
        // CountTruncated is even detectable. Trade-off: the `limit`-sized sample is pulled in
        // the OS's (arbitrary) directory enumeration order and only sorted AFTER truncating to
        // `limit` entries, so which files land in an over-limit sample is OS-dependent —
        // acceptable because once CountTruncated is set the omitted-id set is already documented
        // as a lower bound, never a claimed-complete/exact enumeration.
        //
        // Returns null when the directory doesn't exist/can't be read (incrementing
        // `unreadableDirs` for the latter); otherwise the (possibly filtered downstream) file
        // list plus HitLimit: `touched == limit`, i.e. whether the RAW directory enumeration
        // actually supplied `limit` entries — NOT whether `limit` of them turned out to be
        // well-formed <GUID>.jsonl files. A non-GUID or already-visited entry landing inside the
        // bounded window can otherwise consume the sole sentinel slot without itself being
        // counted as omitted, masking a genuinely-omitted valid descendant just past it (caller
        // must treat HitLimit, not the returned/valid file count, as the signal that more
        // entries may exist beyond this fetch).
        (List<string> Files, bool HitLimit)? EnumerateChildFilesBounded(string dashedId, int limit) {
            var subDir = GeminiPaths.SubagentDir(chatsDir, dashedId);
            if (!Directory.Exists(subDir)) return null;
            try {
                var touched = 0;
                // The Select side effect counts exactly how many raw directory entries the OS
                // enumeration had to yield to satisfy Take(limit) — Take stops pulling from its
                // source the moment it has `limit` items, so `touched` never exceeds `limit`
                // regardless of the directory's true size.
                var taken = Directory.EnumerateFiles(subDir, "*.jsonl")
                    .Select(f => { touched++; return f; })
                    .Take(limit)
                    .OrderBy(f => f, StringComparer.Ordinal)
                    .ToList();
                onBelowCapDirectoryRead?.Invoke(touched);
                return (taken, touched >= limit);
            } catch {
                unreadableDirs++;
                return null;
            }
        }

        // In-cap walk: always run to completion, regardless of below-cap truncation state —
        // this is the unbounded, always-complete import set. Because a child is only ever
        // enqueued onto inCapFrontier when its own depth is <= MaxDescendantDepth, every
        // dequeued depth here is in 0..MaxDescendantDepth — so a node at exactly
        // MaxDescendantDepth has children that are ALL necessarily below the cap: that one
        // directory read is handled via the bounded enumerator below instead of the unbounded one.
        while (inCapFrontier.Count > 0) {
            var (transcriptPath, dashedId, depth) = inCapFrontier.Dequeue();

            if (depth < MaxDescendantDepth) {
                if (EnumerateChildFiles(dashedId) is not { } children) continue;

                foreach (var file in children) {
                    var subId = Path.GetFileNameWithoutExtension(file);
                    if (!Guid.TryParse(subId, out _)) continue; // only well-formed <subId>.jsonl

                    // IN-CAP: the import set — always fully discovered, never bounded by
                    // MaxCountingNodes.
                    var childDepth = depth + 1;
                    if (!visited.Add(subId)) continue; // cycle guard — already reached
                    files.Add(new DescendantFile(file, transcriptPath, subId, childDepth));
                    inCapFrontier.Enqueue((file, subId, childDepth));
                }
                continue;
            }

            // depth == MaxDescendantDepth: every child is necessarily BELOW the cap
            // (childDepth = MaxDescendantDepth + 1). Cap-aware fetch instead of enumerating
            // this parent's whole directory.
            if (countTruncated) continue; // already known to be a lower bound — skip the read entirely

            var remaining = MaxCountingNodes - omittedIds.Count;
            if (EnumerateChildFilesBounded(dashedId, remaining + 1) is not { } fetch) continue;
            var (belowChildren, hitLimit) = fetch;

            foreach (var file in belowChildren) {
                var subId = Path.GetFileNameWithoutExtension(file);
                if (!Guid.TryParse(subId, out _)) continue;
                if (!visited.Add(subId)) continue; // cycle guard — already reached

                if (omittedIds.Count >= MaxCountingNodes) {
                    countTruncated = true;
                    break; // stop scanning this parent's remaining fetched entries too
                }
                omittedIds.Add(subId);
                belowCapFrontier.Enqueue((file, subId));
            }
            // A non-GUID/already-visited entry inside the bounded window can consume the sole
            // sentinel slot without counting as omitted, so the valid-count threshold above can
            // under-report. HitLimit — the raw directory read actually reached `limit` entries —
            // is the only proof we lack; when true we cannot rule out further valid descendants
            // beyond this fetch, so treat it as truncation too (a safe over-report in the rare
            // exact-boundary+junk case; a raw count strictly below the limit is definite EOF and
            // needs no such fallback).
            if (hitLimit) countTruncated = true;
        }

        // Below-cap counting walk — every node here is already below-cap, so every one of its
        // children is too; bounded via EnumerateChildFilesBounded and stopped IMMEDIATELY once
        // truncated: any already-queued below-cap tail (up to MaxCountingNodes entries) is
        // abandoned rather than drained node-by-node, so a pathologically large omitted subtree
        // can't force thousands of further (or unbounded) directory enumerations once the count
        // is already known to be a lower bound.
        while (belowCapFrontier.Count > 0 && !countTruncated) {
            var (_, dashedId) = belowCapFrontier.Dequeue();
            onDequeueBelowCapNode?.Invoke();

            var remaining = MaxCountingNodes - omittedIds.Count;
            if (EnumerateChildFilesBounded(dashedId, remaining + 1) is not { } fetch) continue;
            var (children, hitLimit) = fetch;

            foreach (var file in children) {
                var subId = Path.GetFileNameWithoutExtension(file);
                if (!Guid.TryParse(subId, out _)) continue;
                if (!visited.Add(subId)) continue; // cycle guard — already reached

                if (omittedIds.Count >= MaxCountingNodes) {
                    countTruncated = true;
                    break; // stop scanning this node's remaining fetched entries too
                }
                omittedIds.Add(subId);
                belowCapFrontier.Enqueue((file, subId));
            }
            if (hitLimit) countTruncated = true; // see above
        }

        omittedIds.Sort(StringComparer.Ordinal);
        return new DescendantDiscoveryResult(files, omittedIds.Count, omittedIds, countTruncated, unreadableDirs);
    }

    /// <summary>
    /// Maps each subagent id (DASHED, the nested filename stem) to the <c>agent_name</c>
    /// from the parent's <c>invoke_agent</c> tool call — the subagent's only on-disk type
    /// signal. Best-effort; unmatched ids are absent (callers fall back to "subagent").
    /// </summary>
    public static IReadOnlyDictionary<string, string> ResolveAgentTypes(string parentTranscriptPath) {
        var map = new Dictionary<string, string>(StringComparer.Ordinal);

        try {
            foreach (var line in File.ReadLines(parentTranscriptPath)) {
                if (string.IsNullOrWhiteSpace(line)) continue;

                using var doc  = JsonDocument.Parse(line);
                var       root = doc.RootElement;

                if (root.Str("type") != "gemini" || root.Arr("toolCalls") is not { } tcs) continue;

                foreach (var tc in tcs.EnumerateArray()) {
                    if (tc.Str("name") != "invoke_agent" || tc.Str("agentId") is not { } aid) continue;

                    if (tc.Obj("args") is { } args && args.Str("agent_name") is { Length: > 0 } name)
                        map[aid] = name;
                }
            }
        } catch { /* best effort */ }

        return map;
    }

    /// <summary>
    /// Canonical (dashless) agent id from a subagent filename stem (a dashed UUID). Matches
    /// the dashless form the server routes/correlates on and canonicalizes defensively.
    /// </summary>
    public static string CanonicalAgentId(string dashedSubId) => dashedSubId.Replace("-", "");

    // ── Hook payloads (the vendor-agnostic /hooks/subagent-start|stop wire shape) ──

    /// <summary>
    /// <c>/hooks/subagent-start</c> body. cwd is "" — Gemini carries no cwd for subagents
    /// (same as the parent session); the server's HookBase requires the (non-null) field.
    /// </summary>
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
}
