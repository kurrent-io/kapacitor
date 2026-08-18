using Capacitor.Cli.Commands;
using Capacitor.Cli.Core.Harness.Antigravity;
using Capacitor.Cli.Harness.OpenCode;
using Microsoft.Data.Sqlite;

namespace Capacitor.Cli.Harness.Antigravity;

/// <summary>
/// Read-only reader over an Antigravity conversation's SQLite db
/// (<c>~/.gemini/antigravity/conversations/&lt;id&gt;.db</c>), yielding the synthetic
/// <c>USAGE</c> transcript lines the server maps to <c>AntigravityUsageBackfilledEvent</c>.
/// Antigravity keeps per-generation tokens/model in the <c>gen_metadata</c>
/// table's protobuf <c>data</c> blob, not the JSONL transcript, so the watcher polls this
/// alongside tailing <c>transcript_full.jsonl</c> and streams the decoded rows.
///
/// Lives in the CLI project (not Core) so the Microsoft.Data.Sqlite native bundle never
/// reaches the AOT-published daemon — same rationale as <see cref="OpenCodeDb"/>. Opened
/// read-only, WAL-tolerant. Every step is best-effort: a missing db / table / undecodable
/// row is skipped, never thrown (cost is always fail-open).
/// </summary>
internal sealed class AntigravityGenMetadataDb : IDisposable {
    readonly SqliteConnection _conn;

    // Install the on-demand native-SQLite resolver before SqliteConnection's static ctor
    // runs its first e_sqlite3 P/Invoke (mirrors OpenCodeDb).
    static AntigravityGenMetadataDb() => SqliteNativeResolver.Register();

    AntigravityGenMetadataDb(string dbPath) {
        _conn = new SqliteConnection(new SqliteConnectionStringBuilder {
            DataSource = dbPath,
            Mode       = SqliteOpenMode.ReadOnly,
            Cache      = SqliteCacheMode.Private,
        }.ToString());
        _conn.Open();
    }

    /// <summary>
    /// Reads every <c>gen_metadata</c> row with <c>idx &gt; afterIdx</c> in idx order,
    /// returning (idx, usageLine) for each decodable row. Returns empty (never throws)
    /// when the db is absent/locked/malformed. <paramref name="afterIdx"/> lets the
    /// watcher stream only newly-appended rows across polls.
    /// </summary>
    public static IReadOnlyList<(long Idx, string Line)> ReadUsageLines(string dbPath, long afterIdx = -1, string? createdAt = null) {
        if (!File.Exists(dbPath)) return [];

        try {
            using var db = new AntigravityGenMetadataDb(dbPath);
            return db.Query(afterIdx, createdAt);
        } catch {
            return []; // locked / not-a-db / schema drift — cost is best-effort
        }
    }

    List<(long, string)> Query(long afterIdx, string? createdAt) {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText =
            "SELECT idx, data FROM gen_metadata WHERE idx > $after AND data IS NOT NULL AND length(data) > 0 ORDER BY idx";
        cmd.Parameters.AddWithValue("$after", afterIdx);

        var lines = new List<(long, string)>();
        using var r = cmd.ExecuteReader();
        while (r.Read()) {
            long idx;
            byte[] blob;
            try {
                idx  = r.GetInt64(0);
                blob = ReadBlob(r, 1);
            } catch {
                continue; // skip a malformed / schema-drifted row rather than abort
            }

            if (AntigravityGenMetadata.TryDecode(blob) is { } row)
                lines.Add((idx, AntigravityGenMetadata.ToUsageLine(row, idx, createdAt)));
        }
        return lines;
    }

    static byte[] ReadBlob(SqliteDataReader r, int ordinal) {
        using var src = r.GetStream(ordinal);
        using var ms  = new MemoryStream();
        src.CopyTo(ms);
        return ms.ToArray();
    }

    public void Dispose() => _conn.Dispose();
}
