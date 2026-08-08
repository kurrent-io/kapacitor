using System.Text.Json;
using System.Text.Json.Serialization;

namespace Capacitor.Cli.Core.LocalIpc;

/// The consent decision log's single write/read shape (spec §4.4): the daemon appends one of
/// these per decision to consent-decisions.jsonl; the CLI `log` verb prints raw lines; the app
/// parses them for the Activity feed. Field names are the pre-existing on-disk names verbatim —
/// existing log files remain readable. Outcome: "allowed"|"denied". Source: "owner"|"rule[i]"|
/// "default"|"prompt_no_ui"|"prompt_user"|"prompt_timeout".
public sealed record ConsentDecisionRecord(
    string DecidedAt, string AgentId, string? Requester, bool RequesterIsOwner,
    string Kind, string RepoPath, string Vendor, string Outcome, string Source,
    string? RequesterDisplay);

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.SnakeCaseLower)]
[JsonSerializable(typeof(ConsentDecisionRecord))]
public partial class ConsentDecisionJsonContext : JsonSerializerContext;

/// Complete=false means at least one file existed but could not be read (I/O failure) — the
/// records list may be partial and the consumer must not mistake it for a genuinely shorter
/// log. Clean absence (file not found) is Complete=true (spec §4.4).
public sealed record ConsentLogReadResult(IReadOnlyList<ConsentDecisionRecord> Records, bool Complete);

public static class ConsentDecisionLogReader {
    public static string PathFor(string daemonName) =>
        Path.Combine(DaemonLockPaths.Directory, DaemonLockPaths.Sanitize(daemonName), "consent-decisions.jsonl");

    public static ConsentLogReadResult ReadTail(string daemonName, int max) {
        var path = PathFor(daemonName);
        var complete = true;
        var lines = new List<string>();
        foreach (var file in new[] { path + ".1", path }) {         // .1 first: its lines are older
            if (!TryReadLines(file, lines)) complete = false;
        }

        var seen = new HashSet<ConsentDecisionRecord>();            // value equality — rotation-race dedup
        var records = new List<ConsentDecisionRecord>();
        for (var i = lines.Count - 1; i >= 0 && records.Count < max; i--) {
            var rec = ParseValid(lines[i]);
            if (rec is not null && seen.Add(rec)) records.Add(rec); // newest first
        }
        return new(records, complete);
    }

    // The reader's open — ReadWrite so the daemon's live appends are never blocked, Delete so
    // its File.Move rotation is never blocked (Windows mandatory sharing is otherwise exclusive).
    internal static FileStream OpenShared(string path) =>
        new(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);

    /// True = read cleanly OR cleanly absent; false = existed-but-unreadable (I/O failure).
    static bool TryReadLines(string path, List<string> into) {
        try {
            using var fs = OpenShared(path);
            using var reader = new StreamReader(fs);
            while (reader.ReadLine() is { } line) into.Add(line);
            return true;
        } catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException) {
            return true;  // clean absence
        } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) {
            return false; // exists (or vanished mid-read) but unreadable — partial/incomplete
        }
    }

    static ConsentDecisionRecord? ParseValid(string line) {
        ConsentDecisionRecord? rec;
        try { rec = JsonSerializer.Deserialize(line, ConsentDecisionJsonContext.Default.ConsentDecisionRecord); }
        catch (JsonException) { return null; }
        if (rec is null || string.IsNullOrEmpty(rec.DecidedAt) || string.IsNullOrEmpty(rec.AgentId)
            || string.IsNullOrEmpty(rec.Kind) || string.IsNullOrEmpty(rec.RepoPath)
            || string.IsNullOrEmpty(rec.Vendor) || string.IsNullOrEmpty(rec.Outcome)
            || string.IsNullOrEmpty(rec.Source)) return null;
        return rec;
    }
}
