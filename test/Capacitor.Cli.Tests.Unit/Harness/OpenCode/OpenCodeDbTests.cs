using Capacitor.Cli.Harness.OpenCode;
using Microsoft.Data.Sqlite;

namespace Capacitor.Cli.Tests.Unit.Harness.OpenCode;

public class OpenCodeDbTests {
    static string BuildDb(string dir) {
        var path = Path.Combine(dir, "opencode.db");
        using var conn = new SqliteConnection($"Data Source={path}");
        conn.Open();
        Exec(conn, """
            CREATE TABLE session (id TEXT PRIMARY KEY, parent_id TEXT, directory TEXT,
                title TEXT NOT NULL, version TEXT NOT NULL DEFAULT '', model TEXT,
                time_created INTEGER NOT NULL, time_updated INTEGER NOT NULL);
            CREATE TABLE message (id TEXT PRIMARY KEY, session_id TEXT NOT NULL,
                time_created INTEGER NOT NULL, time_updated INTEGER NOT NULL DEFAULT 0, data TEXT NOT NULL);
            CREATE TABLE part (id TEXT PRIMARY KEY, message_id TEXT NOT NULL, session_id TEXT NOT NULL,
                time_created INTEGER NOT NULL, time_updated INTEGER NOT NULL DEFAULT 0, data TEXT NOT NULL);
            """);
        return path;
    }

    static void Exec(SqliteConnection c, string sql) {
        using var cmd = c.CreateCommand(); cmd.CommandText = sql; cmd.ExecuteNonQuery();
    }

    static void InsertSession(string dbPath, string id, string? parent, string dir, string title, long t) {
        using var c = new SqliteConnection($"Data Source={dbPath}"); c.Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "INSERT INTO session(id,parent_id,directory,title,version,time_created,time_updated) VALUES($i,$p,$d,$t,'1.17',$tc,$tc)";
        cmd.Parameters.AddWithValue("$i", id);
        cmd.Parameters.AddWithValue("$p", (object?)parent ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$d", dir);
        cmd.Parameters.AddWithValue("$t", title);
        cmd.Parameters.AddWithValue("$tc", t);
        cmd.ExecuteNonQuery();
    }

    static void InsertMessage(string dbPath, string id, string sid, long t, string dataJson) {
        using var c = new SqliteConnection($"Data Source={dbPath}"); c.Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "INSERT INTO message(id,session_id,time_created,data) VALUES($i,$s,$t,$d)";
        cmd.Parameters.AddWithValue("$i", id);
        cmd.Parameters.AddWithValue("$s", sid);
        cmd.Parameters.AddWithValue("$t", t);
        cmd.Parameters.AddWithValue("$d", dataJson);
        cmd.ExecuteNonQuery();
    }

    static void InsertPart(string dbPath, string id, string mid, string sid, long t, string dataJson) {
        using var c = new SqliteConnection($"Data Source={dbPath}"); c.Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "INSERT INTO part(id,message_id,session_id,time_created,data) VALUES($i,$m,$s,$t,$d)";
        cmd.Parameters.AddWithValue("$i", id);
        cmd.Parameters.AddWithValue("$m", mid);
        cmd.Parameters.AddWithValue("$s", sid);
        cmd.Parameters.AddWithValue("$t", t);
        cmd.Parameters.AddWithValue("$d", dataJson);
        cmd.ExecuteNonQuery();
    }

    [Test]
    public async Task QueryRoots_returns_only_parentless_sessions() {
        using var tmp = new TempDir();
        var db = BuildDb(tmp.Path);
        InsertSession(db, "ses_root1", null, "/work/a", "Root one", 1782241513759);
        InsertSession(db, "ses_root2", "",   "/work/b", "Root two", 1782241513760);
        InsertSession(db, "ses_child", "ses_root1", "/work/a", "Child", 1782241513761);

        using var ocdb = new OpenCodeDb(db);
        var roots = ocdb.QueryRoots();

        await Assert.That(roots.Select(r => r.Id).OrderBy(x => x))
            .IsEquivalentTo(new[] { "ses_root1", "ses_root2" });
    }

    [Test]
    public async Task QueryChildren_returns_children_in_time_order() {
        using var tmp = new TempDir();
        var db = BuildDb(tmp.Path);
        InsertSession(db, "ses_root", null, "/work/a", "Root", 100);
        InsertSession(db, "ses_c2", "ses_root", "/work/a", "C2", 220);
        InsertSession(db, "ses_c1", "ses_root", "/work/a", "C1", 210);

        using var ocdb = new OpenCodeDb(db);
        var kids = ocdb.QueryChildren("ses_root");

        // Order-sensitive: c1 (t=210) must precede c2 (t=220).
        await Assert.That(string.Join(",", kids.Select(k => k.Id))).IsEqualTo("ses_c1,ses_c2");
    }

    // ── QueryDescendants (D3: recursive grandchild discovery) ──────────────────────

    [Test]
    public async Task QueryDescendants_walks_a_multi_level_parent_chain() {
        using var tmp = new TempDir();
        var db = BuildDb(tmp.Path);
        InsertSession(db, "ses_root", null, "/w", "Root", 100);
        InsertSession(db, "ses_child", "ses_root", "/w", "Child", 200);
        InsertSession(db, "ses_grandchild", "ses_child", "/w", "Grandchild", 300);

        using var ocdb = new OpenCodeDb(db);
        var (descendants, omitted, omittedIds, truncated) = ocdb.QueryDescendants("ses_root");

        await Assert.That(omitted).IsEqualTo(0);
        await Assert.That(omittedIds.Count).IsEqualTo(0);
        await Assert.That(truncated).IsFalse();
        await Assert.That(descendants.Select(d => (d.Row.Id, d.Depth)))
            .IsEquivalentTo(new[] { ("ses_child", 1), ("ses_grandchild", 2) });
    }

    [Test]
    public async Task QueryDescendants_depth_8_imports_depth_9_is_omitted_with_diagnostic() {
        using var tmp = new TempDir();
        var db = BuildDb(tmp.Path);
        InsertSession(db, "ses_root", null, "/w", "Root", 100);

        // A 9-level chain below the root: n1 (depth 1) ... n9 (depth 9).
        var prev = "ses_root";
        for (var depth = 1; depth <= 9; depth++) {
            var id = $"ses_n{depth}";
            InsertSession(db, id, prev, "/w", $"N{depth}", 100 + depth);
            prev = id;
        }

        using var ocdb = new OpenCodeDb(db);
        var (descendants, omitted, omittedIds, truncated) = ocdb.QueryDescendants("ses_root");

        // Depths 1..8 import; depth 9 is omitted, not imported, not promoted.
        await Assert.That(descendants.Select(d => d.Row.Id).OrderBy(x => x))
            .IsEquivalentTo(Enumerable.Range(1, 8).Select(i => $"ses_n{i}"));
        await Assert.That(descendants.Any(d => d.Row.Id == "ses_n9")).IsFalse();
        await Assert.That(descendants.Max(d => d.Depth)).IsEqualTo(8);
        await Assert.That(omitted).IsEqualTo(1);
        await Assert.That(omittedIds).IsEquivalentTo(new[] { "ses_n9" });
        await Assert.That(truncated).IsFalse();
    }

    // the walker used to stop AT the boundary child (depth 9) and
    // never look below it, so a chain continuing to depth 10 was still counted as ONE omitted
    // descendant — undercounting the true size of the omitted subtree. The walk must now
    // continue (never importing) below the cap to count the WHOLE subtree.
    [Test]
    public async Task QueryDescendants_depth_9_and_10_chain_reports_omitted_two_not_one() {
        using var tmp = new TempDir();
        var db = BuildDb(tmp.Path);
        InsertSession(db, "ses_root", null, "/w", "Root", 100);

        // A 10-level chain below the root: n1 (depth 1) ... n10 (depth 10).
        var prev = "ses_root";
        for (var depth = 1; depth <= 10; depth++) {
            var id = $"ses_n{depth}";
            InsertSession(db, id, prev, "/w", $"N{depth}", 100 + depth);
            prev = id;
        }

        using var ocdb = new OpenCodeDb(db);
        var (descendants, omitted, omittedIds, truncated) = ocdb.QueryDescendants("ses_root");

        // Depths 1..8 import; depths 9 AND 10 are omitted — TWO, not one.
        await Assert.That(descendants.Select(d => d.Row.Id).OrderBy(x => x))
            .IsEquivalentTo(Enumerable.Range(1, 8).Select(i => $"ses_n{i}"));
        await Assert.That(omitted).IsEqualTo(2);
        await Assert.That(omittedIds).IsEquivalentTo(new[] { "ses_n9", "ses_n10" });
        await Assert.That(truncated).IsFalse();
    }

    [Test]
    public async Task QueryDescendants_reachable_cycle_terminates_via_visited_set() {
        using var tmp = new TempDir();
        var db = BuildDb(tmp.Path);
        // A mutual-parent cycle: X's parent is Y, and Y's parent is X (corrupt data —
        // no acyclic parent_id tree would ever be written this way, but the walker must
        // not spin forever if it's found).
        InsertSession(db, "ses_x", "ses_y", "/w", "X", 100);
        InsertSession(db, "ses_y", "ses_x", "/w", "Y", 200);

        using var ocdb = new OpenCodeDb(db);
        var (descendants, omitted, omittedIds, truncated) = ocdb.QueryDescendants("ses_x");

        // Terminates (the awaited assertion below is reachable at all — a hang would time
        // out the test) and does not re-discover ses_x as its own descendant.
        await Assert.That(descendants.Select(d => d.Row.Id)).IsEquivalentTo(new[] { "ses_y" });
        await Assert.That(omitted).IsEqualTo(0);
        await Assert.That(omittedIds.Count).IsEqualTo(0);
        await Assert.That(truncated).IsFalse();
    }

    // ── MaxCountingNodes scope (D3 review fix #4) ───────────────────────────────────
    //
    // The counting ceiling used to gate the WHOLE unified traversal via the shared visited-id
    // set's total size, so a root with a wide IN-CAP fan-out (well within MaxDescendantDepth)
    // could itself get silently truncated once 10,000 total ids had been discovered — corrupting
    // the import set, not merely the below-cap counting walk. It must now apply ONLY to
    // descendants already beyond the import cap.

    [Test]
    public async Task QueryDescendants_wide_in_cap_fanout_beyond_the_counting_ceiling_imports_every_child() {
        using var tmp = new TempDir();
        var db = BuildDb(tmp.Path);
        InsertSession(db, "ses_root", null, "/w", "Root", 100);

        // MaxCountingNodes + 50 direct (depth-1, well within MaxDescendantDepth=8) children —
        // before the fix, the old `visited.Count >= MaxCountingNodes` ceiling (which counted the
        // root + every discovered id, in-cap or not) would silently stop importing after ~10,000
        // total visited ids, dropping the tail of this real, in-cap import set.
        const int childCount = OpenCodeDb.MaxCountingNodes + 50;
        InsertManyChildren(db, "ses_root", "ses_kid", childCount, baseTime: 200);

        using var ocdb = new OpenCodeDb(db);
        var (descendants, omitted, omittedIds, truncated) = ocdb.QueryDescendants("ses_root");

        await Assert.That(descendants.Count).IsEqualTo(childCount);
        await Assert.That(descendants.All(d => d.Depth == 1)).IsTrue();
        await Assert.That(omitted).IsEqualTo(0);
        await Assert.That(omittedIds.Count).IsEqualTo(0);
        await Assert.That(truncated).IsFalse();
    }

    [Test]
    public async Task QueryDescendants_below_cap_ceiling_hit_does_not_corrupt_in_cap_descendants() {
        using var tmp = new TempDir();
        var db = BuildDb(tmp.Path);
        InsertSession(db, "ses_root", null, "/w", "Root", 100);

        // A depth 1..8 chain (in-cap, imported), then MaxCountingNodes + 50 direct children of
        // the depth-8 node — ALL at depth 9, i.e. entirely below the import cap. This pushes the
        // below-cap counting walk past MaxCountingNodes, which must set CountTruncated and stop
        // growing the below-cap subtree WITHOUT touching the in-cap descendants gathered above.
        var prev = "ses_root";
        for (var depth = 1; depth <= 8; depth++) {
            var id = $"ses_n{depth}";
            InsertSession(db, id, prev, "/w", $"N{depth}", 100 + depth);
            prev = id;
        }
        const int belowCapCount = OpenCodeDb.MaxCountingNodes + 50;
        InsertManyChildren(db, "ses_n8", "ses_deep", belowCapCount, baseTime: 1000);

        using var ocdb = new OpenCodeDb(db);
        var (descendants, omitted, omittedIds, truncated) = ocdb.QueryDescendants("ses_root");

        // The in-cap import set (depths 1..8) is complete and uncorrupted.
        await Assert.That(descendants.Select(d => d.Row.Id).OrderBy(x => x))
            .IsEquivalentTo(Enumerable.Range(1, 8).Select(i => $"ses_n{i}"));
        await Assert.That(descendants.Max(d => d.Depth)).IsEqualTo(8);

        // The below-cap counting walk hit the ceiling: truncated, and the omitted count/ids are
        // a lower bound (exactly MaxCountingNodes, not the true belowCapCount), never silently
        // reported as an exact/complete count.
        await Assert.That(truncated).IsTrue();
        await Assert.That(omitted).IsEqualTo(OpenCodeDb.MaxCountingNodes);
        await Assert.That(omittedIds.Count).IsEqualTo(OpenCodeDb.MaxCountingNodes);
    }

    // the ceiling used to bound the RETURNED omitted count/ids, but
    // not the actual WORK — every below-cap node already enqueued before the ceiling was hit
    // (up to MaxCountingNodes of them) still got individually dequeued and queried afterward.
    // Once truncation is established, the below-cap frontier must be abandoned outright rather
    // than drained node-by-node, so QueryChildren is never called on any of those below-cap
    // nodes' own children.
    [Test]
    public async Task QueryDescendants_below_cap_truncation_stops_expanding_already_queued_below_cap_nodes() {
        using var tmp = new TempDir();
        var db = BuildDb(tmp.Path);
        InsertSession(db, "ses_root", null, "/w", "Root", 100);

        // A depth 1..8 chain (in-cap), then MaxCountingNodes + 50 direct children of the
        // depth-8 node — ALL at depth 9 (below cap). A SINGLE QueryChildren("ses_n8") call
        // returns this entire cap+50 list, so truncation is established while still inside the
        // IN-CAP walk's processing of ses_n8 — before the below-cap frontier is ever drained.
        var prev = "ses_root";
        for (var depth = 1; depth <= 8; depth++) {
            var id = $"ses_n{depth}";
            InsertSession(db, id, prev, "/w", $"N{depth}", 100 + depth);
            prev = id;
        }
        const int belowCapCount = OpenCodeDb.MaxCountingNodes + 50;
        InsertManyChildren(db, "ses_n8", "ses_deep", belowCapCount, baseTime: 1000);

        using var ocdb = new OpenCodeDb(db);
        var (descendants, omitted, omittedIds, truncated) = ocdb.QueryDescendants("ses_root");

        // Sanity: same correctness guarantees as the sibling test above.
        await Assert.That(descendants.Select(d => d.Row.Id).OrderBy(x => x))
            .IsEquivalentTo(Enumerable.Range(1, 8).Select(i => $"ses_n{i}"));
        await Assert.That(truncated).IsTrue();
        await Assert.That(omitted).IsEqualTo(OpenCodeDb.MaxCountingNodes);

        // The actual WORK is bounded too: only the in-cap chain (root + n1..n7, each with one
        // child, plus n8's own single big query) is ever queried — 9 calls total. None of the
        // ~10,000 below-cap "ses_deep*" nodes is ever individually dequeued and queried for its
        // OWN children (which would have driven QueryChildrenCallCount into the thousands).
        await Assert.That(ocdb.QueryChildrenCallCount).IsLessThanOrEqualTo(9);
    }

    // a below-cap parent's single QueryChildren call used to
    // materialize its ENTIRE child row set before CountTruncated could even be detected — a
    // depth-8 parent with (say) a million depth-9 children would read/allocate all million rows
    // in one call. The per-parent fetch must instead be capped to (remaining counting capacity +
    // 1 sentinel), regardless of how many children the parent truly has.
    [Test]
    public async Task QueryDescendants_below_cap_parent_never_materializes_more_than_the_counting_cap_in_a_single_fetch() {
        using var tmp = new TempDir();
        var db = BuildDb(tmp.Path);
        InsertSession(db, "ses_root", null, "/w", "Root", 100);

        // Depth 1..8 chain (in-cap), then a below-cap depth-9 fan-out FAR larger than
        // MaxCountingNodes under the depth-8 node — 5x the cap, to make an unbounded fetch
        // unmistakably distinguishable from a bounded one.
        var prev = "ses_root";
        for (var depth = 1; depth <= 8; depth++) {
            var id = $"ses_n{depth}";
            InsertSession(db, id, prev, "/w", $"N{depth}", 100 + depth);
            prev = id;
        }
        const int hugeChildCount = OpenCodeDb.MaxCountingNodes * 5;
        InsertManyChildren(db, "ses_n8", "ses_deep", hugeChildCount, baseTime: 1000);

        using var ocdb = new OpenCodeDb(db);
        var (_, omitted, _, truncated) = ocdb.QueryDescendants("ses_root");

        await Assert.That(truncated).IsTrue();
        await Assert.That(omitted).IsEqualTo(OpenCodeDb.MaxCountingNodes);

        // The crux of the fix: no single QueryChildren/QueryChildrenBounded call ever returned
        // more than (MaxCountingNodes + 1) rows — proving the below-cap fetch is capped by a SQL
        // LIMIT rather than materializing the true 50,000-row child set before truncating.
        await Assert.That(ocdb.QueryChildrenMaxRowsReturned).IsLessThanOrEqualTo(OpenCodeDb.MaxCountingNodes + 1);
    }

    // a below-cap parent's bounded row fetch caps RAW SQL rows via
    // `LIMIT`, but QuerySessions's per-row try/catch skip of a malformed row happens AFTER that —
    // so a malformed row landing inside the sentinel window used to silently consume the sole
    // sentinel slot, filling omittedIds to exactly MaxCountingNodes valid ids while CountTruncated
    // stayed false: a false "complete" report, even though the parent's true child count could be
    // larger. QueryChildrenBounded must now report HitLimit from the RAW row count (regardless of
    // how many rows survived mapping), so this case is always caught.
    [Test]
    public async Task QueryDescendants_below_cap_boundary_plus_malformed_row_is_never_falsely_reported_complete() {
        using var tmp = new TempDir();
        var db = BuildDb(tmp.Path);
        InsertSession(db, "ses_root", null, "/w", "Root", 100);

        var prev = "ses_root";
        for (var depth = 1; depth <= 8; depth++) {
            var id = $"ses_n{depth}";
            InsertSession(db, id, prev, "/w", $"N{depth}", 100 + depth);
            prev = id;
        }

        // MaxCountingNodes - 1 valid below-cap children of ses_n8, plus ONE malformed row (NULL
        // id — SQLite permits NULL in a non-INTEGER PRIMARY KEY column; QuerySessions's
        // r.GetString(0) throws on it, and the per-row try/catch skips it during mapping) —
        // MaxCountingNodes raw rows total, strictly BELOW the bounded fetch's limit
        // (MaxCountingNodes + 1). A definite raw EOF: the whole result set is seen, so this is
        // (correctly) NOT truncated.
        InsertManyChildren(db, "ses_n8", "ses_deep", OpenCodeDb.MaxCountingNodes - 1, baseTime: 1000);
        InsertMalformedChild(db, "ses_n8", time: 1000 + OpenCodeDb.MaxCountingNodes);

        using var ocdb = new OpenCodeDb(db);
        var before = ocdb.QueryDescendants("ses_root");

        await Assert.That(before.CountTruncated).IsFalse();
        await Assert.That(before.DescendantsOmitted).IsEqualTo(OpenCodeDb.MaxCountingNodes - 1);

        // Add ONE more genuinely-new valid child of ses_n8, pushing its row count to exactly
        // MaxCountingNodes + 1 (MaxCountingNodes valid + 1 malformed) — precisely the bounded
        // fetch's sentinel-inflated limit, so the malformed row now lands inside the sentinel
        // window.
        InsertSession(db, "ses_deep_extra", "ses_n8", "/w", "Extra", 1000 + OpenCodeDb.MaxCountingNodes + 1);

        var after = ocdb.QueryDescendants("ses_root");

        // The in-cap import set (depths 1..8) is unaffected either way.
        await Assert.That(after.Descendants.Select(d => d.Row.Id).OrderBy(x => x))
            .IsEquivalentTo(Enumerable.Range(1, 8).Select(i => $"ses_n{i}"));

        // Before the fix: the malformed row silently consumed the sole sentinel slot, so exactly
        // MaxCountingNodes valid ids filled omittedIds and CountTruncated stayed false — the
        // genuinely-new child added above must change the reported signature: DescendantsOmitted
        // grows from MaxCountingNodes - 1 to MaxCountingNodes AND CountTruncated flips to true (a
        // safe over-report — the raw fetch exhausted its limit, so we can no longer prove there
        // isn't yet another child beyond it).
        await Assert.That(after.CountTruncated).IsTrue();
        await Assert.That(after.DescendantsOmitted).IsEqualTo(OpenCodeDb.MaxCountingNodes);
        await Assert.That(after.OmittedDescendantIds.Count).IsEqualTo(OpenCodeDb.MaxCountingNodes);
    }

    // A NULL id is allowed by SQLite in a non-INTEGER PRIMARY KEY column (unlike the SQL
    // standard, SQLite doesn't imply NOT NULL for PRIMARY KEY unless it aliases rowid) — this
    // simulates a malformed/schema-drifted row that passes the SQL WHERE/LIMIT but fails
    // QuerySessions's row mapping (r.GetString(0) on a NULL column throws).
    static void InsertMalformedChild(string dbPath, string parentId, long time) {
        using var c = new SqliteConnection($"Data Source={dbPath}");
        c.Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText =
            "INSERT INTO session(id,parent_id,directory,title,version,time_created,time_updated) " +
            "VALUES (NULL,$p,'/w','Malformed','1.17',$tc,$tc)";
        cmd.Parameters.AddWithValue("$p", parentId);
        cmd.Parameters.AddWithValue("$tc", time);
        cmd.ExecuteNonQuery();
    }

    // Bulk-inserts `count` direct children of `parentId` in a single transaction with a reused
    // prepared command — the per-call-connection InsertSession helper above is far too slow for
    // the 10,000+ row scale these MaxCountingNodes tests need.
    static void InsertManyChildren(string dbPath, string parentId, string idPrefix, int count, long baseTime) {
        using var c = new SqliteConnection($"Data Source={dbPath}");
        c.Open();
        using var tx  = c.BeginTransaction();
        using var cmd = c.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText =
            "INSERT INTO session(id,parent_id,directory,title,version,time_created,time_updated) " +
            "VALUES($i,$p,'/w','T','1.17',$tc,$tc)";
        var pId = cmd.CreateParameter(); pId.ParameterName = "$i"; cmd.Parameters.Add(pId);
        var pParent = cmd.CreateParameter(); pParent.ParameterName = "$p"; pParent.Value = parentId; cmd.Parameters.Add(pParent);
        var pTc = cmd.CreateParameter(); pTc.ParameterName = "$tc"; cmd.Parameters.Add(pTc);

        for (var i = 0; i < count; i++) {
            pId.Value = $"{idPrefix}{i}";
            pTc.Value = baseTime + i;
            cmd.ExecuteNonQuery();
        }
        tx.Commit();
    }

    [Test]
    public async Task reads_while_a_wal_writer_holds_uncheckpointed_data() {
        using var tmp = new TempDir();
        var db = BuildDb(tmp.Path);

        using var writer = new SqliteConnection($"Data Source={db}");
        writer.Open();
        Exec(writer, "PRAGMA journal_mode=WAL;");
        InsertSession(db, "ses_live", null, "/w", "Live", 100);

        await Assert.That(File.Exists(db + "-wal")).IsTrue();
        await Assert.That(File.Exists(db + "-shm")).IsTrue();

        using var ocdb = new OpenCodeDb(db);
        var roots = ocdb.QueryRoots();

        await Assert.That(roots.Select(r => r.Id)).Contains("ses_live");
    }

    [Test]
    public async Task SynthesizeLines_merges_ids_onto_info_and_parts() {
        using var tmp = new TempDir();
        var db = BuildDb(tmp.Path);
        InsertSession(db, "ses_x", null, "/w", "T", 100);
        InsertMessage(db, "msg_1", "ses_x", 100, """{"role":"user","time":{"created":100}}""");
        InsertPart(db, "prt_1", "msg_1", "ses_x", 100, """{"type":"text","text":"hello"}""");

        using var ocdb = new OpenCodeDb(db);
        var lines = ocdb.SynthesizeLines("ses_x").ToList();

        await Assert.That(lines.Count).IsEqualTo(1);
        using var doc = System.Text.Json.JsonDocument.Parse(lines[0]);
        var info  = doc.RootElement.GetProperty("info");
        var part0 = doc.RootElement.GetProperty("parts")[0];
        await Assert.That(info.GetProperty("id").GetString()).IsEqualTo("msg_1");
        await Assert.That(info.GetProperty("sessionID").GetString()).IsEqualTo("ses_x");
        await Assert.That(info.GetProperty("role").GetString()).IsEqualTo("user");
        await Assert.That(part0.GetProperty("id").GetString()).IsEqualTo("prt_1");
        await Assert.That(part0.GetProperty("messageID").GetString()).IsEqualTo("msg_1");
        await Assert.That(part0.GetProperty("sessionID").GetString()).IsEqualTo("ses_x");
        await Assert.That(part0.GetProperty("type").GetString()).IsEqualTo("text");
    }

    [Test]
    public async Task SynthesizeLines_orders_by_message_chronology_not_lexical_id() {
        using var tmp = new TempDir();
        var db = BuildDb(tmp.Path);
        InsertSession(db, "ses_x", null, "/w", "T", 100);
        InsertMessage(db, "msg_b", "ses_x", 100, """{"role":"user"}""");
        InsertMessage(db, "msg_a", "ses_x", 200, """{"role":"assistant"}""");
        InsertPart(db, "prt_b", "msg_b", "ses_x", 100, """{"type":"text","text":"first"}""");
        InsertPart(db, "prt_a", "msg_a", "ses_x", 200, """{"type":"text","text":"second"}""");

        using var ocdb = new OpenCodeDb(db);
        var roles = ocdb.SynthesizeLines("ses_x")
            .Select(l => System.Text.Json.JsonDocument.Parse(l).RootElement.GetProperty("info").GetProperty("role").GetString())
            .ToList();

        await Assert.That(string.Join(",", roles)).IsEqualTo("user,assistant");
    }

    [Test]
    public async Task SynthesizeLines_includes_message_with_no_parts() {
        using var tmp = new TempDir();
        var db = BuildDb(tmp.Path);
        InsertSession(db, "ses_x", null, "/w", "T", 100);
        InsertMessage(db, "msg_1", "ses_x", 100, """{"role":"assistant"}""");

        using var ocdb = new OpenCodeDb(db);
        var lines = ocdb.SynthesizeLines("ses_x").ToList();

        await Assert.That(lines.Count).IsEqualTo(1);
        var parts = System.Text.Json.JsonDocument.Parse(lines[0]).RootElement.GetProperty("parts");
        await Assert.That(parts.GetArrayLength()).IsEqualTo(0);
    }

    [Test]
    public async Task IsImportRelevantLine_matches_server_normalizer_rules() {
        // Importable:
        await Assert.That(OpenCodeDb.IsImportRelevantLine(
            """{"info":{"role":"user"},"parts":[{"id":"p","type":"text","text":"hi"}]}""")).IsTrue();
        await Assert.That(OpenCodeDb.IsImportRelevantLine(
            """{"info":{"role":"assistant"},"parts":[{"id":"p","type":"reasoning","text":"thinking"}]}""")).IsTrue();
        await Assert.That(OpenCodeDb.IsImportRelevantLine(
            """{"info":{"role":"assistant"},"parts":[{"id":"p","type":"tool","callID":"c","tool":"bash","state":{"status":"completed"}}]}""")).IsTrue();

        // NOT importable:
        await Assert.That(OpenCodeDb.IsImportRelevantLine(
            """{"info":{"role":"assistant"},"parts":[{"type":"step-start"},{"type":"step-finish"}]}""")).IsFalse();
        await Assert.That(OpenCodeDb.IsImportRelevantLine(
            """{"info":{"role":"user"},"parts":[{"id":"p","type":"text","text":""}]}""")).IsFalse();
        await Assert.That(OpenCodeDb.IsImportRelevantLine(
            """{"info":{"role":"user"},"parts":[{"id":"p","type":"reasoning","text":"x"}]}""")).IsFalse();
        await Assert.That(OpenCodeDb.IsImportRelevantLine(
            """{"info":{"role":"user"},"parts":[{"id":"p","type":"tool","callID":"c","tool":"bash","state":{"status":"completed"}}]}""")).IsFalse();
        await Assert.That(OpenCodeDb.IsImportRelevantLine(
            """{"info":{"role":"assistant"},"parts":[{"id":"p","type":"text","text":"x","synthetic":true}]}""")).IsFalse();
        await Assert.That(OpenCodeDb.IsImportRelevantLine(
            """{"info":{"role":"assistant"},"parts":[{"id":"p","type":"text","text":"x","ignored":true}]}""")).IsFalse();
        await Assert.That(OpenCodeDb.IsImportRelevantLine(
            """{"info":{"role":"assistant"},"parts":[{"id":"p","type":"tool","state":{"status":"completed"}}]}""")).IsFalse();
        await Assert.That(OpenCodeDb.IsImportRelevantLine(
            """{"info":{"role":"assistant"},"parts":[{"id":"p","type":"tool","callID":"c","tool":"bash","state":{"status":"running"}}]}""")).IsFalse();
        await Assert.That(OpenCodeDb.IsImportRelevantLine(
            """{"info":{"role":"assistant"},"parts":[{"type":"text","text":"no id"}]}""")).IsFalse();
        await Assert.That(OpenCodeDb.IsImportRelevantLine(
            """{"info":{"role":"assistant"},"parts":[{"id":"p","type":"text","text":" "}]}""")).IsTrue();
    }
}
