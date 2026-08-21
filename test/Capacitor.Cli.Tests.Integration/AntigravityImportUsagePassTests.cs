using Capacitor.Cli.Commands;
using Capacitor.Cli.Harness.Antigravity;
using Microsoft.Data.Sqlite;
using WireMock.RequestBuilders;
using WireMock.ResponseBuilders;
using WireMock.Server;

namespace Capacitor.Cli.Tests.Integration;

/// <summary>
/// Wire-contract test for the (item 7.1) explicit gen_metadata usage pass on
/// Antigravity historical import: the source must decode the sibling conversation
/// <c>.db</c>'s <c>gen_metadata</c> row(s) into synthetic USAGE transcript lines and POST
/// them via <c>/hooks/transcript</c> BEFORE <c>/hooks/session-end/antigravity</c> — for every
/// classification, including AlreadyLoaded (which sends zero real transcript lines).
/// </summary>
public class AntigravityImportUsagePassTests : IDisposable {
    readonly WireMockServer _server = WireMockServer.Start();
    readonly TempDir        _tmp    = new();
    readonly string         _home;

    public AntigravityImportUsagePassTests() => _home = _tmp.Path;

    const string Root = "11110000-0000-4000-8000-000000000001";

    public void Dispose() {
        _server.Stop();
        _tmp.Dispose();
    }

    string BrainDir(string convId) => Path.Combine(_home, ".gemini", "antigravity", "brain", convId);

    void WriteTranscript(string convId, string firstUserText) {
        var dir = Path.Combine(BrainDir(convId), ".system_generated", "logs");
        Directory.CreateDirectory(dir);
        File.WriteAllLines(Path.Combine(dir, "transcript_full.jsonl"), new[] {
            $$"""{"step_index":0,"source":"USER_EXPLICIT","type":"USER_INPUT","status":"DONE","created_at":"2026-07-02T19:00:00Z","content":"<USER_REQUEST>{{firstUserText}}</USER_REQUEST>"}""",
            """{"step_index":1,"source":"MODEL","type":"PLANNER_RESPONSE","status":"DONE","created_at":"2026-07-02T19:00:05Z","content":"done"}"""
        });
    }

    // The gen_metadata db is a sibling of the brain dir: <home>/.gemini/antigravity/conversations/<convId>.db
    // (AntigravityPaths.ConversationDbFromTranscript derives it from the transcript path).
    string ConversationDbPath(string convId) =>
        Path.Combine(_home, ".gemini", "antigravity", "conversations", $"{convId}.db");

    /// <summary>
    /// Seeds a throwaway SQLite db shaped like Antigravity's <c>conversations/&lt;id&gt;.db</c>:
    /// a <c>gen_metadata</c> table with one row whose <c>data</c> blob is a hand-built protobuf
    /// message decodable by <c>AntigravityGenMetadata.TryDecode</c> (top.1 → 4 → {2 input,
    /// 3 output}) — the same empirically-pinned shape exercised in
    /// <c>AntigravityGenMetadataTests</c> (Unit suite). No existing db-seeding helper covers
    /// gen_metadata, so this builds the table + blob directly, mirroring the pattern already used
    /// for OpenCode's sqlite fixtures (<c>OpenCodeDbFixtureIt</c>) in this same suite.
    /// </summary>
    void SeedGenMetadataDb(string convId, long idx, long inputTokens, long outputTokens) {
        var dbPath = ConversationDbPath(convId);
        Directory.CreateDirectory(Path.GetDirectoryName(dbPath)!);

        using var conn = new SqliteConnection($"Data Source={dbPath}");
        conn.Open();
        using (var create = conn.CreateCommand()) {
            create.CommandText = "CREATE TABLE gen_metadata (idx INTEGER PRIMARY KEY, data BLOB)";
            create.ExecuteNonQuery();
        }

        var blob = BuildUsageBlob(inputTokens, outputTokens);
        using var insert = conn.CreateCommand();
        insert.CommandText = "INSERT INTO gen_metadata (idx, data) VALUES ($idx, $data)";
        insert.Parameters.AddWithValue("$idx", idx);
        insert.Parameters.AddWithValue("$data", blob);
        insert.ExecuteNonQuery();
    }

    // ── tiny protobuf encoder (mirrors AntigravityGenMetadataTests in the Unit suite —
    // duplicated here rather than shared since the two projects don't expose internals to
    // each other and this shape is only ~15 lines) ──
    static byte[] BuildUsageBlob(long input, long output) {
        var counts = Concat(Varint(2, input), Varint(3, output));
        var usage  = Ld(4, counts);
        return Ld(1, usage);
    }
    static byte[] Varint(int field, long v) {
        var b = new List<byte>();
        WriteVarint(b, (ulong)((long)field << 3));
        WriteVarint(b, (ulong)v);
        return b.ToArray();
    }
    static byte[] Ld(int field, byte[] payload) {
        var b = new List<byte>();
        WriteVarint(b, (ulong)(((long)field << 3) | 2));
        WriteVarint(b, (ulong)payload.Length);
        b.AddRange(payload);
        return b.ToArray();
    }
    static void WriteVarint(List<byte> b, ulong v) {
        do { var x = (byte)(v & 0x7f); v >>= 7; if (v != 0) x |= 0x80; b.Add(x); } while (v != 0);
    }
    static byte[] Concat(params byte[][] parts) => parts.SelectMany(p => p).ToArray();

    [Test]
    public async Task Import_posts_usage_lines_before_session_end() {
        WriteTranscript(Root, "build it");
        SeedGenMetadataDb(Root, idx: 0, inputTokens: 19360, outputTokens: 246);

        _server.Given(Request.Create().WithPath("/api/sessions/*/last-line").UsingGet())
            .RespondWith(Response.Create().WithStatusCode(404));
        _server.Given(Request.Create().WithPath("/hooks/session-start/antigravity").UsingPost())
            .RespondWith(Response.Create().WithStatusCode(200));
        _server.Given(Request.Create().WithPath("/hooks/transcript").UsingPost())
            .RespondWith(Response.Create().WithStatusCode(200));
        _server.Given(Request.Create().WithPath("/hooks/session-end/antigravity").UsingPost())
            .RespondWith(Response.Create().WithStatusCode(200));

        using var client = new HttpClient();
        var source = new AntigravityImportSource(home: _home, geminiCliHome: "");

        var discovered = await source.DiscoverAsync(new DiscoveryFilters(null, null, null, 0), CancellationToken.None);
        await Assert.That(discovered.Count).IsEqualTo(1);

        var classified = await source.ClassifyAsync(
            discovered,
            new ClassifyContext(client, _server.Url!, MinLines: 0, ExcludedRepos: null, ExcludedPaths: null),
            CancellationToken.None);
        await Assert.That(classified[0].Status).IsEqualTo(ImportCommand.ClassificationStatus.New);

        var outcome = await source.ImportSessionAsync(
            classified[0], new ImportContext(client, _server.Url!, ForcePrivate: false), CancellationToken.None);
        await Assert.That(outcome).IsEqualTo(ImportOutcome.Loaded);

        var ordered = _server.LogEntries
            .Where(e => e.RequestMessage.Method == "POST")
            .Select(e => (Path: e.RequestMessage.Path!, Body: e.RequestMessage.Body ?? ""))
            .ToList();

        // The USAGE line is nested JSON-as-a-string inside the batch's "lines" array, so its
        // quotes come back double-escaped on the wire (CapacitorJsonContext's default encoder
        // escapes embedded '"' as ") — search on the bare token instead of a quoted literal.
        var lastUsageIdx = ordered.FindLastIndex(x => x.Path == "/hooks/transcript" && x.Body.Contains("USAGE"));
        var endIdx       = ordered.FindIndex(x => x.Path == "/hooks/session-end/antigravity");

        await Assert.That(lastUsageIdx).IsGreaterThanOrEqualTo(0);       // USAGE was posted
        await Assert.That(lastUsageIdx).IsLessThan(endIdx);              // ...before session-end

        // The USAGE line lands in the high line-number band (>= 1_000_000_000) and carries the
        // decoded token counts, so it can't be confused with a real content line.
        var usageBody = ordered[lastUsageIdx].Body;
        await Assert.That(usageBody).Contains("19360"); // decoded input_tokens from gen_metadata
        await Assert.That(usageBody).Contains("\"line_numbers\":[1000000000]"); // top-level field — not nested, quotes are literal
        await Assert.That(usageBody).Contains("\"vendor\":\"antigravity\"");    // top-level field — not nested, quotes are literal
    }

    [Test]
    public async Task AlreadyLoaded_still_posts_usage_lines_before_session_end_with_zero_content_lines() {
        WriteTranscript(Root, "build it");
        SeedGenMetadataDb(Root, idx: 0, inputTokens: 500, outputTokens: 20);

        // High watermark → AlreadyLoaded: the real transcript content is fully ingested and must
        // NOT be re-sent, but the usage pass must still run (this is the whole point of the task —
        // a session re-imported after this feature shipped may have content but no cost).
        _server.Given(Request.Create().WithPath("/api/sessions/*/last-line").UsingGet())
            .RespondWith(Response.Create().WithStatusCode(200).WithBody("""{"last_line_number":99}"""));
        _server.Given(Request.Create().WithPath("/hooks/session-start/antigravity").UsingPost())
            .RespondWith(Response.Create().WithStatusCode(200));
        _server.Given(Request.Create().WithPath("/hooks/transcript").UsingPost())
            .RespondWith(Response.Create().WithStatusCode(200));
        _server.Given(Request.Create().WithPath("/hooks/session-end/antigravity").UsingPost())
            .RespondWith(Response.Create().WithStatusCode(200));

        using var client = new HttpClient();
        var source = new AntigravityImportSource(home: _home, geminiCliHome: "");

        var discovered = await source.DiscoverAsync(new DiscoveryFilters(null, null, null, 0), CancellationToken.None);
        var classified = await source.ClassifyAsync(
            discovered,
            new ClassifyContext(client, _server.Url!, MinLines: 0, ExcludedRepos: null, ExcludedPaths: null),
            CancellationToken.None);
        await Assert.That(classified[0].Status).IsEqualTo(ImportCommand.ClassificationStatus.AlreadyLoaded);

        var outcome = await source.ImportSessionAsync(
            classified[0], new ImportContext(client, _server.Url!, ForcePrivate: false), CancellationToken.None);
        await Assert.That(outcome).IsEqualTo(ImportOutcome.Skipped);

        var ordered = _server.LogEntries
            .Where(e => e.RequestMessage.Method == "POST")
            .Select(e => (Path: e.RequestMessage.Path!, Body: e.RequestMessage.Body ?? ""))
            .ToList();

        var usagePosts = ordered.Where(x => x.Path == "/hooks/transcript" && x.Body.Contains("USAGE")).ToList();
        await Assert.That(usagePosts.Count).IsEqualTo(1); // real content is NOT re-sent — only the usage pass

        // The USAGE line is nested JSON-as-a-string inside the batch's "lines" array, so its
        // quotes come back double-escaped on the wire (CapacitorJsonContext's default encoder
        // escapes embedded '"' as ") — search on the bare token instead of a quoted literal.
        var lastUsageIdx = ordered.FindLastIndex(x => x.Path == "/hooks/transcript" && x.Body.Contains("USAGE"));
        var endIdx       = ordered.FindIndex(x => x.Path == "/hooks/session-end/antigravity");
        await Assert.That(lastUsageIdx).IsGreaterThanOrEqualTo(0);
        await Assert.That(lastUsageIdx).IsLessThan(endIdx);
    }
}
