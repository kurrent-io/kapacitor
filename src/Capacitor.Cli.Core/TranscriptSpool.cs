using System.Text;
using System.Text.RegularExpressions;

namespace Capacitor.Cli.Core;

/// <summary>
/// Dedicated on-disk spool for the UNDELIVERED TRANSCRIPT TAIL captured at shutdown during an
/// outage. Unlike <see cref="HookSpool"/> (1 MB drop-oldest — acceptable for small lifecycle POSTs)
/// this is bounded with NO SILENT DROP: on cap exhaustion it stops appending and writes a
/// <c>needs-import</c> marker so the session is surfaced as requiring `kcap import` rather than
/// truncated. Per-session JSONL of transcript batch JSON, one per line, arrival order.
///
/// <para>Moved to <c>Capacitor.Cli.Core</c> alongside <see cref="HookSpool"/> and
/// <see cref="LifecycleSpoolDrain"/> so both the CLI and the daemon can share the ordered-drain
/// primitives.</para>
/// </summary>
public sealed partial class TranscriptSpool(string spoolDir, long capBytes = TranscriptSpool.DefaultCapBytes) {
    public const long DefaultCapBytes = 8_388_608; // 8 MB per session

    // Named here rather than on ConfigRoot, for the same reason as HookSpool's.
    const string DirName = "transcript-spool";

    /// <summary>The spool under a config root. The directory overload is for a spool that is not
    /// under one — a test's own throwaway directory.</summary>
    public TranscriptSpool(ConfigRoot config, long capBytes = DefaultCapBytes)
        : this(config.Path(DirName), capBytes) { }

    /// <summary>Outcome of an <see cref="Append"/> call.</summary>
    public enum AppendResult {
        Appended,          // batch persisted to the live spool file
        MarkedNeedsImport, // NOT persisted (cap hit or write failure) — needs-import marker set instead
        Ignored,           // discarded before any spool interaction (malformed session id) — never a real append
    }

    static readonly Regex SafeSessionId  = SafeSessionIdRegex();
    static readonly Regex LegacyGuidKey = LegacyGuidKeyRegex();
    static int seqCounter;

    /// <summary>The directory where spool files are stored.</summary>
    internal string Dir => spoolDir;

    string? LivePathFor(string sessionId) =>
        SafeSessionId.IsMatch(sessionId) ? Path.Combine(spoolDir, $"{EncodeKey(sessionId)}.transcript.jsonl") : null;

    string? MarkerPathFor(string sessionId) =>
        SafeSessionId.IsMatch(sessionId) ? Path.Combine(spoolDir, $"{EncodeKey(sessionId)}.needs-import") : null;

    public AppendResult Append(string sessionId, string batchJson) {
        var path = LivePathFor(sessionId);
        if (path is null) return AppendResult.Ignored; // malformed id — nothing we can key on
        try {
            Directory.CreateDirectory(spoolDir);
            var line = batchJson.Replace("\n", "").Replace("\r", "");
            var incoming = Encoding.UTF8.GetByteCount(line) + 1;
            var existing = File.Exists(path) ? new FileInfo(path).Length : 0;
            if (existing + incoming > capBytes) {
                // NO drop-oldest: preserve what we have and surface the gap honestly.
                MarkNeedsImport(sessionId, $"transcript tail exceeded {capBytes}-byte spool cap");
                return AppendResult.MarkedNeedsImport;
            }
            File.AppendAllText(path, $"{line}\n");
            return AppendResult.Appended;
        } catch (Exception ex) {
            // The whole point of this class is NO silent drop: a failed write is a real gap, so
            // surface it as needs-import rather than a phantom "Appended". Never throw on the
            // shutdown path — MarkNeedsImport logs if it too can't persist the marker.
            MarkNeedsImport(sessionId, $"append failed: {ex.Message}");
            return AppendResult.MarkedNeedsImport;
        }
    }

    /// <summary>
    /// Records a needs-import marker for the session. Returns <c>true</c> if the marker was
    /// persisted; <c>false</c> (and logs to stderr) if it could not be written — the caller must
    /// not assume a marker exists just because it asked for one.
    /// </summary>
    public bool MarkNeedsImport(string sessionId, string reason) {
        var p = MarkerPathFor(sessionId);
        if (p is null) return false;
        try {
            Directory.CreateDirectory(spoolDir);
            File.WriteAllText(p, $"{DateTimeOffset.UtcNow:O} {reason}\n");
            return true;
        } catch (Exception ex) {
            Console.Error.WriteLine($"[kcap] transcript spool: failed to write needs-import marker for {sessionId}: {ex.Message}");
            return false;
        }
    }

    public bool NeedsImport(string sessionId) {
        var p = MarkerPathFor(sessionId);
        return p is not null && File.Exists(p);
    }

    /// <summary>True if this session still has undelivered spool entries (live .jsonl or .draining temp).</summary>

    // ---- filename key encoding -------------------------------------------------------------
    //
    // Session ids are case-SENSITIVE and are preserved byte-for-byte (OpenCode's are base62 --
    // "ses_619a78374ffe7o0x1iTK74jFRg"), but macOS and Windows filesystems are case-INSENSITIVE, so
    // using the raw id as the filename would let two distinct sessions address one file: their
    // lifecycle entries would interleave and one session's ended marker would discard the other's
    // remainder.
    //
    // So the id is escaped into a single-case filename key and decoded back on the way out. '~' is
    // the escape and cannot occur in an admitted id, which keeps the mapping unambiguous and
    // reversible -- the drain posts the DECODED id as session_id, so a lossy or one-way transform
    // (a hash, or lowercasing) would put a fabricated id on the wire.
    static string EncodeKey(string sessionId) {
        // A dashless GUID is left EXACTLY as-is. Two hex spellings differing only by case are the
        // SAME id, so they neither need disambiguating nor may be renamed: this is the entire
        // population the pre-upgrade grammar admitted, so every spool file already on disk keeps its
        // historical name and stays readable. Escaping applies only to the ids that are new here.
        if (LegacyGuidKey.IsMatch(sessionId)) return sessionId;

        var sb = new StringBuilder(sessionId.Length + 8);

        foreach (var c in sessionId) {
            if (char.IsAsciiLetterUpper(c)) sb.Append('~').Append(char.ToLowerInvariant(c));
            else sb.Append(c);
        }

        return sb.ToString();
    }

    static string? DecodeKey(string key) {
        var sb = new StringBuilder(key.Length);

        for (var i = 0; i < key.Length; i++) {
            if (key[i] != '~') { sb.Append(key[i]); continue; }
            if (++i >= key.Length || !char.IsAsciiLetterLower(key[i])) return null; // malformed
            sb.Append(char.ToUpperInvariant(key[i]));
        }

        return sb.ToString();
    }

    public bool HasBacklog(string sessionId) =>
        SafeSessionId.IsMatch(sessionId) && Directory.Exists(spoolDir)
        && (File.Exists(Path.Combine(spoolDir, $"{EncodeKey(sessionId)}.transcript.jsonl"))
            || Directory.EnumerateFiles(spoolDir, $"{EncodeKey(sessionId)}.*.transcript.draining").Any());

    /// <summary>Every distinct session id with a live .jsonl, a recovered .draining temp, or a
    /// needs-import marker (a marker can outlive its transcript file if a prior pass ran out of
    /// budget before delivering it, so it must still surface the session to the caller).</summary>
    public IEnumerable<string> SessionIdsWithBacklog() {
        if (!Directory.Exists(spoolDir)) return [];
        var ids = new List<string>();
        foreach (var f in Directory.EnumerateFiles(spoolDir)) {
            var sid = SessionIdOf(f);
            if (sid is not null && !ids.Contains(sid)) ids.Add(sid);
        }
        return ids;
    }

    static string? SessionIdOf(string filePath) {
        var name = Path.GetFileName(filePath);
        var dot  = name.IndexOf('.');
        if (dot <= 0) return null;
        var decoded = DecodeKey(name[..dot]);
        return decoded is not null && SafeSessionId.IsMatch(decoded) ? decoded : null;
    }

    public async Task DrainAsync(string sessionId, Func<string, Task<DrainOutcome>> poster, Func<bool> expired, CancellationToken ct) {
        var live = LivePathFor(sessionId);
        if (live is null || !Directory.Exists(spoolDir)) return;

        foreach (var temp in Directory.EnumerateFiles(spoolDir, $"{EncodeKey(sessionId)}.*.transcript.draining").OrderBy(File.GetCreationTimeUtc)) {
            if (expired() || ct.IsCancellationRequested) return;
            if (await DrainFileAsync(temp, poster, expired, ct)) return; // transient → stop, keep remainder
        }

        if (!File.Exists(live) || expired() || ct.IsCancellationRequested) return;

        var rotated = Path.Combine(spoolDir, $"{EncodeKey(sessionId)}.{Environment.ProcessId}-{Interlocked.Increment(ref seqCounter)}.transcript.draining");
        try { File.Move(live, rotated); }
        catch { return; } // lost the atomic-rename race (or vanished) — the winner handles it
        await DrainFileAsync(rotated, poster, expired, ct);
    }

    // Drain a private temp. Delivered advances; TransientStop or budget stops and keeps the remainder.
    static async Task<bool> DrainFileAsync(
            string path, Func<string, Task<DrainOutcome>> poster, Func<bool> expired, CancellationToken ct) {
        string[] lines;
        try { lines = await File.ReadAllLinesAsync(path, ct); }
        catch { return false; }

        var i = 0;
        for (; i < lines.Length; i++) {
            if (expired() || ct.IsCancellationRequested) break;
            if (string.IsNullOrWhiteSpace(lines[i])) continue;

            DrainOutcome outcome;
            try { outcome = await poster(lines[i]); }
            catch { outcome = DrainOutcome.TransientStop; }

            if (outcome == DrainOutcome.TransientStop) break;
        }

        if (i >= lines.Length) {
            try { File.Delete(path); } catch { }
            return false;
        }
        try { await File.WriteAllLinesAsync(path, lines.Skip(i), ct); } catch { }
        return true;
    }

    public void ReapOlderThan(TimeSpan age) {
        try {
            if (!Directory.Exists(spoolDir)) return;
            var cutoff = DateTime.UtcNow - age;
            foreach (var file in Directory.EnumerateFiles(spoolDir)) {
                try { if (File.GetLastWriteTimeUtc(file) < cutoff) File.Delete(file); } catch { }
            }
        } catch { }
    }

    // Filename-safe superset of the old dashless-GUID form. The filename IS the session id --
    // LifecycleSpoolDrain posts it verbatim as session_id for session-needs-import -- so the key may
    // be widened but never transformed (hashing would fabricate an id on the wire). Excludes '.', '/'
    // and '\\', preserving both the path-traversal property and the parse-before-first-dot split.
    // Vendors such as OpenCode use ids like "ses_7f3a9c21b8", which the old form silently dropped.
    /// <summary>The pre-upgrade key space: a dashless GUID, case-insensitively one id.</summary>
    [GeneratedRegex("^[0-9a-fA-F]{32}$", RegexOptions.Compiled)]
    private static partial Regex LegacyGuidKeyRegex();

    [GeneratedRegex("^[A-Za-z0-9_-]{1,64}$", RegexOptions.Compiled)]
    private static partial Regex SafeSessionIdRegex();
}
