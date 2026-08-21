using System.Text.Json;
using System.Text.Json.Nodes;
using Capacitor.Cli.Commands;
using Microsoft.Data.Sqlite;

namespace Capacitor.Cli.Harness.OpenCode;

/// <summary>
/// One OpenCode session row (subset used by import). <c>TimeCreated</c>/<c>TimeUpdated</c>
/// are <c>null</c> when the column is NULL or 0 (both observed in the wild) — "unknown",
/// never epoch.
/// </summary>
internal sealed record OpenCodeSessionRow(
    string  Id,
    string? ParentId,
    string? Directory,
    string  Title,
    long?   TimeCreated,
    long?   TimeUpdated);

/// <summary>
/// Read-only reader over OpenCode's SQLite db (<c>~/.local/share/opencode/opencode.db</c>).
/// Lives in the CLI project (not Core) so the Microsoft.Data.Sqlite native bundle
/// never reaches the AOT-published daemon. Opened read-only, WAL-tolerant.
///
/// <para>Reconstructs the live plugin's <c>{info,parts}</c> JSONL per message by merging
/// each row's key columns back onto its <c>data</c> JSON — info gets {id, sessionID};
/// each part gets {id, messageID, sessionID} — so the server's <c>opencode</c> normalizer
/// consumes import output identically to the live stream.</para>
/// </summary>
internal sealed class OpenCodeDb : IDisposable {
    readonly SqliteConnection _conn;

    // Install the on-demand native-SQLite resolver before SqliteConnection's static ctor
    // runs its first e_sqlite3 P/Invoke. A static ctor on this type is guaranteed to run
    // before the instance ctor body references SqliteConnection below.
    static OpenCodeDb() => SqliteNativeResolver.Register();

    public OpenCodeDb(string dbPath) {
        _conn = new SqliteConnection(new SqliteConnectionStringBuilder {
            DataSource = dbPath,
            Mode       = SqliteOpenMode.ReadOnly,
            Cache      = SqliteCacheMode.Private,
        }.ToString());
        _conn.Open();
    }

    public IReadOnlyList<OpenCodeSessionRow> QueryRoots() =>
        QuerySessions("(parent_id IS NULL OR parent_id = '')", parent: null).Rows;

    public IReadOnlyList<OpenCodeSessionRow> QueryChildren(string parentId) {
        QueryChildrenCallCount++;
        var (rows, _) = QuerySessions("parent_id = $parent", parentId);
        TrackRowsReturned(rows.Count);
        return rows;
    }

    /// <summary>
    /// Cap-aware variant of <see cref="QueryChildren"/> for a parent whose children are
    /// NECESSARILY beyond <see cref="MaxDescendantDepth"/> — avoids materializing the whole
    /// child set before truncation could be detected. Adds a SQL <c>LIMIT $limit</c> so at most
    /// <paramref name="limit"/> rows are ever read/allocated for a single call, instead of the
    /// full (potentially unbounded) result <see cref="QueryChildren"/> would return. Callers
    /// pass (remaining counting capacity + 1 sentinel) so <see cref="DescendantDiscoveryResult.CountTruncated"/>
    /// can still be set correctly the moment the fetched batch itself proves there are more than
    /// the remaining capacity allows. NEVER used for an in-cap parent — those keep the unbounded
    /// <see cref="QueryChildren"/> (see <see cref="QueryDescendants"/>), preserving the
    /// always-complete in-cap discovery contract.
    ///
    /// <para>The returned <c>HitLimit</c> reflects the number of RAW rows the SQL <c>LIMIT</c>
    /// actually returned — BEFORE <see cref="QuerySessions"/>'s per-row try/catch skips a
    /// malformed row — not the count of rows that survived mapping. A malformed or
    /// already-visited row can otherwise silently consume the sole sentinel slot, masking a
    /// genuinely-omitted valid descendant just beyond it: the caller must treat <c>HitLimit</c>,
    /// not the returned row count, as the signal that more rows may exist beyond this fetch (see
    /// <see cref="QueryDescendants"/>).</para>
    /// </summary>
    internal (IReadOnlyList<OpenCodeSessionRow> Rows, bool HitLimit) QueryChildrenBounded(string parentId, int limit) {
        QueryChildrenCallCount++;
        var (rows, rawCount) = QuerySessions("parent_id = $parent", parentId, limit);
        TrackRowsReturned(rows.Count);
        return (rows, rawCount >= limit);
    }

    /// <summary>
    /// Total <see cref="QueryChildren"/>/<see cref="QueryChildrenBounded"/> invocations made
    /// through this instance — internal-only instrumentation letting a regression test prove
    /// the below-cap counting walk in <see cref="QueryDescendants"/> stops issuing further DB
    /// queries the instant <see cref="DescendantDiscoveryResult.CountTruncated"/> is established,
    /// instead of draining every already-enqueued below-cap node to completion.
    /// </summary>
    internal int QueryChildrenCallCount { get; private set; }

    /// <summary>
    /// The largest number of rows any single <see cref="QueryChildren"/>/<see cref="QueryChildrenBounded"/>
    /// call has returned so far — internal-only instrumentation letting a regression test prove
    /// a single below-cap parent's child fetch never materializes more than
    /// ~<see cref="MaxCountingNodes"/> rows, even when that parent's true child count is far
    /// larger.
    /// </summary>
    internal int QueryChildrenMaxRowsReturned { get; private set; }

    void TrackRowsReturned(int count) {
        if (count > QueryChildrenMaxRowsReturned) QueryChildrenMaxRowsReturned = count;
    }

    /// <summary>Import-side recursion depth cap — a descendant beyond this depth
    /// is neither imported nor promoted; the caller surfaces the count.</summary>
    public const int MaxDescendantDepth = 8;

    /// <summary>
    /// Hard ceiling on the total number of BELOW-CAP (omitted) nodes visited by the counting
    /// walk. This bounds ONLY the walk into descendants already beyond
    /// <see cref="MaxDescendantDepth"/> — it never applies to in-cap import discovery, which is
    /// always complete (see <see cref="QueryDescendants"/>). The visited-id set already prevents
    /// an infinite loop on a reachable cycle; this ceiling instead bounds the total work a
    /// pathologically huge or wide UNIMPORTED subtree could otherwise force onto a single
    /// classify/import pass. A real OpenCode session graph tops out at a few dozen nodes, so
    /// this is a generous safety valve, not a realistic limit — if it's ever hit,
    /// <see cref="DescendantDiscoveryResult.CountTruncated"/> is set so the omitted count/ids are
    /// treated as a LOWER BOUND rather than silently corrupting anything.
    /// </summary>
    public const int MaxCountingNodes = 10_000;

    /// <summary>One recursively-discovered descendant row at its depth below the root (a direct
    /// child is depth 1, a grandchild depth 2, etc.).</summary>
    public readonly record struct DescendantRow(OpenCodeSessionRow Row, int Depth);

    /// <summary>
    /// Recursive descendant discovery result. <see cref="Descendants"/> holds EVERY descendant
    /// WITHIN <see cref="MaxDescendantDepth"/> — the import set (BFS, deterministic, each level
    /// ordered like <see cref="QueryChildren"/>) — and is NEVER truncated by
    /// <see cref="MaxCountingNodes"/>: in-cap discovery is unbounded.
    /// <see cref="DescendantsOmitted"/> is the count of every descendant BELOW the cap, not
    /// merely the boundary children immediately past it — a chain through depths 9 and 10
    /// reports 2, not 1 — UNLESS <see cref="CountTruncated"/> is true, in which case it (and
    /// <see cref="OmittedDescendantIds"/>) is a LOWER BOUND: the below-cap counting walk hit
    /// <see cref="MaxCountingNodes"/> before it could finish, so the true omitted count/identity
    /// set may be larger. <see cref="OmittedDescendantIds"/> carries the (possibly partial)
    /// omitted ids (sorted, order-independent) so a caller can fold them into a completeness
    /// fingerprint: a newly-reachable capped descendant then changes the fingerprint even though
    /// its content is never imported. Never silent — the caller surfaces the count (and any
    /// truncation) in the import summary, and MUST treat <see cref="CountTruncated"/> as blocking
    /// a fingerprint-based completeness/AlreadyLoaded decision.
    /// </summary>
    public readonly record struct DescendantDiscoveryResult(
        IReadOnlyList<DescendantRow> Descendants,
        int                          DescendantsOmitted,
        IReadOnlyList<string>        OmittedDescendantIds,
        bool                         CountTruncated);

    /// <summary>
    /// Recursively walks <c>parent_id</c> edges from <paramref name="rootId"/> — per-level
    /// <see cref="QueryChildren"/>, a visited-id set to guard a reachable cycle (traversal
    /// simply stops re-descending, terminating the walk), IMPORT depth capped at
    /// <see cref="MaxDescendantDepth"/>. In-cap discovery (depth &lt;=
    /// <see cref="MaxDescendantDepth"/>) is ALWAYS complete — never bounded by
    /// <see cref="MaxCountingNodes"/>. A descendant beyond the cap is never imported, but the
    /// walk still recurses into it (same visited-set guard) purely to COUNT the whole omitted
    /// subtree accurately and surface its identities — this below-cap-only counting/signature
    /// walk is what <see cref="MaxCountingNodes"/> bounds; if it's hit,
    /// <see cref="DescendantDiscoveryResult.CountTruncated"/> is set and the omitted count/ids
    /// become a lower bound instead of silently under-reporting as complete.
    ///
    /// <para>The in-cap and below-cap walks run over two INDEPENDENT frontiers. A below-cap
    /// descendant's own descendants are always below-cap too (depth only grows), so the split is
    /// safe and lets the below-cap walk be abandoned the instant it truncates, without disturbing
    /// the in-cap walk's completeness guarantee: once truncation is established, the below-cap
    /// frontier is dropped rather than drained node-by-node.</para>
    ///
    /// <para>Because <see cref="MaxDescendantDepth"/> gates the in-cap frontier's own enqueue
    /// (a child is only ever added to <c>inCapFrontier</c> when its depth is &lt;=
    /// <see cref="MaxDescendantDepth"/>), every node dequeued from it has depth in
    /// <c>0..MaxDescendantDepth</c> — so a node at exactly <see cref="MaxDescendantDepth"/> has
    /// children that are ALL necessarily below the cap. That single call — where an unbounded
    /// <see cref="QueryChildren"/> could still force reading and allocating an entire
    /// (potentially enormous) child row set before truncation was even detectable — goes through
    /// <see cref="QueryChildrenBounded"/> instead, capped to (remaining counting capacity + 1
    /// sentinel row). An in-cap parent (depth &lt; <see cref="MaxDescendantDepth"/>) keeps the
    /// unbounded <see cref="QueryChildren"/> — its children are always in-cap.</para>
    ///
    /// <para>A bounded fetch's <c>limit</c> caps RAW rows, but <see cref="QuerySessions"/>
    /// filters malformed rows and this walk dedups already-visited ids AFTER the fetch — so a
    /// malformed or duplicate row landing inside the bounded window could consume the sole
    /// sentinel row without itself being counted as an omitted descendant, letting a
    /// genuinely-omitted valid descendant just past it go undetected and the walk falsely
    /// conclude <see cref="DescendantDiscoveryResult.CountTruncated"/> is <c>false</c>.
    /// <see cref="QueryChildrenBounded"/> reports whether the RAW <c>LIMIT</c> was actually
    /// exhausted (<c>HitLimit</c>); if so, truncation is asserted regardless of how many rows in
    /// the batch turned out valid — the only case that can't prove otherwise is a raw count
    /// strictly below the limit (definite EOF). This can over-report truncation in the rare case
    /// where the directory holds exactly <c>limit</c> raw rows and no more (safe: the omitted
    /// count/ids were already documented as a lower bound once truncated).</para>
    /// </summary>
    public DescendantDiscoveryResult QueryDescendants(string rootId) {
        var result         = new List<DescendantRow>();
        var visited        = new HashSet<string>(StringComparer.Ordinal) { rootId };
        var omittedIds     = new List<string>();
        var countTruncated = false;

        var inCapFrontier    = new Queue<(string Id, int Depth)>();
        var belowCapFrontier = new Queue<string>();
        inCapFrontier.Enqueue((rootId, 0));

        // In-cap walk: always run to completion, regardless of below-cap truncation state —
        // this is the unbounded, always-complete import set.
        while (inCapFrontier.Count > 0) {
            var (id, depth) = inCapFrontier.Dequeue();

            if (depth < MaxDescendantDepth) {
                // Every child here is necessarily in-cap (childDepth <= MaxDescendantDepth) —
                // always fully discovered, never bounded by MaxCountingNodes. Unbounded
                // QueryChildren is safe/required here.
                foreach (var child in QueryChildren(id)) {
                    var childDepth = depth + 1;
                    if (!visited.Add(child.Id)) continue; // cycle guard — already reached
                    result.Add(new DescendantRow(child, childDepth));
                    inCapFrontier.Enqueue((child.Id, childDepth));
                }
                continue;
            }

            // depth == MaxDescendantDepth: every child is necessarily BELOW the cap
            // (childDepth = MaxDescendantDepth + 1). Cap-aware fetch instead of materializing
            // the whole child row set for this parent.
            if (countTruncated) continue; // already known to be a lower bound — skip the query entirely

            var remaining = MaxCountingNodes - omittedIds.Count;
            var (belowChildren, hitLimit) = QueryChildrenBounded(id, remaining + 1);
            foreach (var child in belowChildren) {
                if (!visited.Add(child.Id)) continue; // cycle guard — already reached

                if (omittedIds.Count >= MaxCountingNodes) {
                    countTruncated = true;
                    break; // stop scanning this parent's remaining fetched rows too
                }
                omittedIds.Add(child.Id);
                belowCapFrontier.Enqueue(child.Id);
            }
            // A malformed/already-visited row can consume the sole sentinel slot before it's
            // counted as an omitted descendant, so the valid-count threshold above can
            // under-report. HitLimit — the raw SQL LIMIT was actually exhausted — is the only
            // proof we lack; when it's true we cannot rule out further valid descendants beyond
            // this fetch, so treat it as truncation too (a safe over-report in the rare
            // exact-boundary+junk case; a raw count strictly below the limit is definite EOF and
            // needs no such fallback).
            if (hitLimit) countTruncated = true;
        }

        // Below-cap counting/signature walk — every node here is already below-cap, so every
        // one of its children is too; bounded by MaxCountingNodes via QueryChildrenBounded and
        // stopped IMMEDIATELY once truncated: any already-queued below-cap tail (up to
        // MaxCountingNodes entries) is abandoned rather than drained node-by-node, so a
        // pathologically large omitted subtree can't force thousands of further (or unbounded)
        // DB queries once the count is already known to be a lower bound.
        while (belowCapFrontier.Count > 0 && !countTruncated) {
            var id = belowCapFrontier.Dequeue();

            var remaining = MaxCountingNodes - omittedIds.Count;
            var (children, hitLimit) = QueryChildrenBounded(id, remaining + 1);
            foreach (var child in children) {
                if (!visited.Add(child.Id)) continue; // cycle guard — already reached

                if (omittedIds.Count >= MaxCountingNodes) {
                    countTruncated = true;
                    break; // stop scanning this node's remaining fetched rows too
                }
                omittedIds.Add(child.Id);
                belowCapFrontier.Enqueue(child.Id);
            }
            if (hitLimit) countTruncated = true; // see above
        }

        omittedIds.Sort(StringComparer.Ordinal);
        return new DescendantDiscoveryResult(result, omittedIds.Count, omittedIds, countTruncated);
    }

    /// <summary>
    /// <c>RawCount</c> is every row the reader actually yielded —
    /// INCLUDING one skipped below for being malformed — never just <c>Rows.Count</c>. Only
    /// <c>RawCount</c> can prove whether a <c>LIMIT</c> was truly exhausted: a malformed row
    /// still consumes one of the SQL engine's <c>LIMIT</c> slots even though it never reaches
    /// <c>Rows</c>, so a caller checking <c>Rows.Count == limit</c> instead could be fooled into
    /// thinking fewer raw rows existed than actually did.
    /// </summary>
    (IReadOnlyList<OpenCodeSessionRow> Rows, int RawCount) QuerySessions(string whereClause, string? parent, int? limit = null) {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText =
            $"SELECT id, parent_id, directory, title, time_created, time_updated " +
            $"FROM session WHERE {whereClause} ORDER BY time_created, id" +
            (limit is not null ? " LIMIT $limit" : "");
        if (parent is not null) cmd.Parameters.AddWithValue("$parent", parent);
        if (limit is not null) cmd.Parameters.AddWithValue("$limit", limit.Value);

        var rows     = new List<OpenCodeSessionRow>();
        var rawCount = 0;
        using var r = cmd.ExecuteReader();
        while (r.Read()) {
            rawCount++;
            OpenCodeSessionRow row;
            try {
                row = new OpenCodeSessionRow(
                    Id:          r.GetString(0),
                    ParentId:    r.IsDBNull(1) ? null : r.GetString(1),
                    Directory:   r.IsDBNull(2) ? null : r.GetString(2),
                    Title:       r.IsDBNull(3) ? "" : r.GetString(3),
                    TimeCreated: r.IsDBNull(4) ? null : NonZero(r.GetInt64(4)),
                    TimeUpdated: r.IsDBNull(5) ? null : NonZero(r.GetInt64(5)));
            } catch {
                continue; // skip a malformed / schema-drifted row rather than abort the scan
            }
            rows.Add(row);
        }
        return (rows, rawCount);
    }

    // A 0 epoch is observed alongside NULL for an unset time_created/time_updated
    // column — both mean "unknown", not the 1970 epoch.
    static long? NonZero(long v) => v == 0 ? null : v;

    /// <summary>
    /// Streams one reconstructed <c>{info,parts}</c> JSONL line per message, in
    /// message chronological order, parts in (time_created,id) order. A LEFT JOIN keeps
    /// messages that have no parts. Bounded memory: holds only the current message's
    /// parts at a time.
    /// </summary>
    public IEnumerable<string> SynthesizeLines(string sessionId) {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText =
            "SELECT m.id, m.data, p.id, p.message_id, p.session_id, p.data " +
            "FROM message m LEFT JOIN part p ON p.message_id = m.id " +
            "WHERE m.session_id = $s " +
            "ORDER BY m.time_created, m.id, p.time_created, p.id";
        cmd.Parameters.AddWithValue("$s", sessionId);

        using var r = cmd.ExecuteReader();

        string?    curMsgId   = null;
        string     curMsgData = "{}";
        JsonArray? parts      = null;

        while (r.Read()) {
            var msgId = r.GetString(0);
            if (msgId != curMsgId) {
                if (curMsgId is not null) yield return BuildLine(curMsgId, sessionId, curMsgData, parts!);
                curMsgId   = msgId;
                curMsgData = r.GetString(1);
                parts      = new JsonArray();
            }
            // p.* columns are null for a message with no parts (LEFT JOIN).
            if (!r.IsDBNull(2)) {
                var partNode = JsonNode.Parse(r.GetString(5))!.AsObject();
                partNode["id"]        = r.GetString(2);
                partNode["messageID"] = r.GetString(3);
                partNode["sessionID"] = r.GetString(4);
                // Cast to JsonNode so the non-generic Add(JsonNode?) overload is chosen —
                // the generic Add<T>(T) trips IL2026/IL3050 under AOT (see CLAUDE.md).
                parts!.Add((JsonNode)partNode);
            }
        }
        if (curMsgId is not null) yield return BuildLine(curMsgId, sessionId, curMsgData, parts!);
    }

    // Merge the row's key columns back onto the message's data JSON to reproduce the
    // live SDK's `info` object: {id (= message.id), sessionID (= the session row id)}.
    static string BuildLine(string msgId, string sessionId, string msgData, JsonArray parts) {
        var info = JsonNode.Parse(msgData)!.AsObject();
        info["id"]        = msgId;
        info["sessionID"] = sessionId;
        return new JsonObject { ["info"] = info, ["parts"] = parts }.ToJsonString();
    }

    /// <summary>
    /// True when a reconstructed line maps to at least one canonical server event
    /// under the <c>opencode</c> normalizer. ROLE-AWARE and HIDDEN-AWARE, mirroring
    /// <c>Capacitor.Server/Sessions/Canonical/OpenCodeTranscriptNormalizer.cs</c>:
    /// <list type="bullet">
    ///   <item>user → a non-hidden <c>text</c> part with non-empty text;</item>
    ///   <item>assistant → a non-hidden <c>reasoning</c>/<c>text</c> part with <c>id</c> and non-empty text;</item>
    ///   <item>assistant → a <c>tool</c> part with <c>id</c>, <c>callID</c>, <c>tool</c>, and terminal <c>state.status</c>.</item>
    /// </list>
    /// A part is hidden when <c>synthetic:true</c> or <c>ignored:true</c>. Gates
    /// <c>TooShort</c>, so it must not over-count; keep in sync with the normalizer.
    /// </summary>
    public static bool IsImportRelevantLine(string line) {
        try {
            using var doc = JsonDocument.Parse(line);
            var root = doc.RootElement;
            var role = root.TryGetProperty("info", out var info) && info.TryGetProperty("role", out var rl)
                ? rl.GetString() : null;
            if (root.TryGetProperty("parts", out var parts) && parts.ValueKind == JsonValueKind.Array) {
                foreach (var p in parts.EnumerateArray()) {
                    if (IsHidden(p)) continue;
                    var type = p.TryGetProperty("type", out var t) ? t.GetString() : null;
                    switch (role, type) {
                        // user text: non-empty (server uses Length > 0, so whitespace counts).
                        case ("user", "text"):
                            if (HasText(p)) return true;
                            break;
                        // assistant text/reasoning: server skips when id is null, then requires Length > 0.
                        case ("assistant", "text"):
                        case ("assistant", "reasoning"):
                            if (HasField(p, "id") && HasText(p)) return true;
                            break;
                        // assistant tool: id + callID + tool present (null-checked) + terminal state.
                        case ("assistant", "tool"):
                            var status = p.TryGetProperty("state", out var st) && st.TryGetProperty("status", out var ss)
                                ? ss.GetString() : null;
                            if (status is "completed" or "error"
                             && HasField(p, "id") && HasField(p, "callID") && HasField(p, "tool"))
                                return true;
                            break;
                    }
                }
            }
            return false;
        } catch {
            return false;
        }
    }

    static bool IsHidden(JsonElement p) =>
        (p.TryGetProperty("synthetic", out var s) && s.ValueKind == JsonValueKind.True) ||
        (p.TryGetProperty("ignored",   out var i) && i.ValueKind == JsonValueKind.True);

    // Present as a JSON string — mirrors the server's `part.Str(name)`, which treats only
    // string values as present. Empty string counts, for parity with the server's null-check.
    static bool HasField(JsonElement p, string name) =>
        p.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String;

    // text present, string, length > 0 (matches server's Length > 0 — NOT IsNullOrWhiteSpace).
    static bool HasText(JsonElement p) =>
        p.TryGetProperty("text", out var tx) && tx.ValueKind == JsonValueKind.String && tx.GetString()!.Length > 0;

    public void Dispose() => _conn.Dispose();
}
