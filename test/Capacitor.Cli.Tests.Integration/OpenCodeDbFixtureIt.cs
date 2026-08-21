using System.Text.Json.Nodes;
using Microsoft.Data.Sqlite;

namespace Capacitor.Cli.Tests.Integration;

/// <summary>OpenCode-shaped throwaway SQLite db for routed-import wire-contract tests.</summary>
internal sealed class OpenCodeDbFixtureIt : IDisposable {
    readonly TempDir _tmp = new();
    public string Dir        => _tmp.Path;
    public string DbPath     => Path.Combine(Dir, "opencode.db");
    public string LedgerPath => Path.Combine(Dir, "ledger.json");

    public OpenCodeDbFixtureIt() {
        using var c = Open();
        Exec(c, """
            CREATE TABLE session (id TEXT PRIMARY KEY, parent_id TEXT, directory TEXT,
                title TEXT NOT NULL, version TEXT NOT NULL DEFAULT '', model TEXT,
                time_created INTEGER, time_updated INTEGER);
            CREATE TABLE message (id TEXT PRIMARY KEY, session_id TEXT NOT NULL,
                time_created INTEGER NOT NULL, time_updated INTEGER NOT NULL DEFAULT 0, data TEXT NOT NULL);
            CREATE TABLE part (id TEXT PRIMARY KEY, message_id TEXT NOT NULL, session_id TEXT NOT NULL,
                time_created INTEGER NOT NULL, time_updated INTEGER NOT NULL DEFAULT 0, data TEXT NOT NULL);
            """);
    }

    SqliteConnection Open() { var c = new SqliteConnection($"Data Source={DbPath}"); c.Open(); return c; }
    static void Exec(SqliteConnection c, string sql) { using var cmd = c.CreateCommand(); cmd.CommandText = sql; cmd.ExecuteNonQuery(); }

    public void AddSession(string id, string? parent, string? dir, string title, long t) {
        using var c = Open(); using var cmd = c.CreateCommand();
        cmd.CommandText = "INSERT INTO session(id,parent_id,directory,title,version,time_created,time_updated) VALUES($i,$p,$d,$t,'1.17',$tc,$tc)";
        cmd.Parameters.AddWithValue("$i", id);
        cmd.Parameters.AddWithValue("$p", (object?)parent ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$d", (object?)dir ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$t", title);
        cmd.Parameters.AddWithValue("$tc", t);
        cmd.ExecuteNonQuery();
    }

    // Nullable-timestamp variant of AddSession — real OpenCode rows can carry a NULL
    // time_created/time_updated (session never touched after creation, or a schema
    // that predates the column), which must map to "unknown", not epoch.
    public void AddSessionRaw(string id, string? parent, string? dir, string title, long? timeCreated, long? timeUpdated) {
        using var c = Open(); using var cmd = c.CreateCommand();
        cmd.CommandText = "INSERT INTO session(id,parent_id,directory,title,version,time_created,time_updated) VALUES($i,$p,$d,$t,'1.17',$tc,$tu)";
        cmd.Parameters.AddWithValue("$i", id);
        cmd.Parameters.AddWithValue("$p", (object?)parent ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$d", (object?)dir ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$t", title);
        cmd.Parameters.AddWithValue("$tc", (object?)timeCreated ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$tu", (object?)timeUpdated ?? DBNull.Value);
        cmd.ExecuteNonQuery();
    }

    public void AddMessageWithText(string sid, string msgId, string text, long t, string? agent = null) {
        var info = new JsonObject { ["role"] = "user", ["time"] = new JsonObject { ["created"] = t } };
        if (agent is not null) info["agent"] = agent;
        var part = new JsonObject { ["id"] = "prt_" + msgId, ["type"] = "text", ["text"] = text };
        InsertRaw(sid, msgId, t, info.ToJsonString(), "prt_" + msgId, part.ToJsonString());
    }

    public void AddMessageWithTextAndAgent(string sid, string msgId, string text, long t, string agent) =>
        AddMessageWithText(sid, msgId, text, t, agent);

    void InsertRaw(string sid, string msgId, long t, string msgData, string partId, string partData) {
        using var c = Open();
        using (var m = c.CreateCommand()) {
            m.CommandText = "INSERT INTO message(id,session_id,time_created,data) VALUES($i,$s,$t,$d)";
            m.Parameters.AddWithValue("$i", msgId); m.Parameters.AddWithValue("$s", sid);
            m.Parameters.AddWithValue("$t", t); m.Parameters.AddWithValue("$d", msgData);
            m.ExecuteNonQuery();
        }
        using (var p = c.CreateCommand()) {
            p.CommandText = "INSERT INTO part(id,message_id,session_id,time_created,data) VALUES($i,$m,$s,$t,$d)";
            p.Parameters.AddWithValue("$i", partId); p.Parameters.AddWithValue("$m", msgId);
            p.Parameters.AddWithValue("$s", sid); p.Parameters.AddWithValue("$t", t);
            p.Parameters.AddWithValue("$d", partData);
            p.ExecuteNonQuery();
        }
    }

    public void Dispose() => _tmp.Dispose();
}
