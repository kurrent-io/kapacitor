using System.Diagnostics;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Capacitor.Cli.Core;

/// <summary>Result of a single spooled-entry replay attempt.</summary>
public enum DrainOutcome {
    Delivered,    // POST succeeded — advance past the entry
    Drop,         // permanent failure (4xx except 408/429) — discard, do not retry
    TransientStop // server down/timeout/budget — stop draining, keep the remainder
}

/// <summary>
/// Vendor-neutral on-disk spool for lifecycle hook POSTs whose delivery failed.
/// Per-session JSONL (<c>{spoolDir}/&lt;dashless-sid&gt;.jsonl</c>), one
/// <c>{"route","body"}</c> object per line in arrival order. Drains are
/// rotate-on-drain: the live file is atomically renamed to a private
/// <c>.draining</c> temp before reading, so concurrent appends never collide
/// with an in-flight drain.
///
/// <para>Moved to <c>Capacitor.Cli.Core</c> so both the CLI
/// (<c>Capacitor.Cli</c>, an AOT exe) and the daemon (<c>Capacitor.Cli.Daemon</c>,
/// a separate AOT exe) can share the ordered-drain primitives without either
/// project referencing the other's exe as a library.</para>
/// </summary>
public sealed partial class HookSpool(string spoolDir, int capBytes = HookSpool.DefaultCapBytes) {
    public const int DefaultCapBytes = 1_048_576; // 1 MB per session file

    static readonly Regex SafeSessionId = SafeSessionIdRegex();
    static          int   seqCounter;

    /// <summary>The directory where spool files are stored.</summary>
    internal string Dir => spoolDir;

    string? LivePathFor(string sessionId) =>
        SafeSessionId.IsMatch(sessionId) ? Path.Combine(spoolDir, $"{sessionId}.jsonl") : null;

    /// <summary>
    /// Appends one payload for a later drain pass. Returns whether it was actually persisted, so a
    /// caller cannot report "spooled" after a rejected key or a disk/permission fault.
    /// </summary>
    public bool Append(string sessionId, string route, string rawPayloadJson) {
        var path = LivePathFor(sessionId);
        if (path is null) return false;
        try {
            Directory.CreateDirectory(spoolDir);
            var line = new JsonObject { ["route"] = route, ["body"] = rawPayloadJson }.ToJsonString();
            EnsureUnderCap(path, Encoding.UTF8.GetByteCount(line) + 1);
            File.AppendAllText(path, $"{line}\n");
            return true;
        } catch {
            return false;
        }
    }

    void EnsureUnderCap(string path, int incomingBytes) {
        try {
            if (!File.Exists(path)) return;
            if (new FileInfo(path).Length + incomingBytes <= capBytes) return;
            // Count UTF-8 BYTES (not chars) so the cap holds for non-ASCII payloads — the file is
            // byte-measured (FileInfo.Length), so a char-based count would under-count and let the
            // file grow past capBytes.
            var lines = File.ReadAllLines(path).ToList();
            while (lines.Count > 0 && lines.Sum(l => Encoding.UTF8.GetByteCount(l) + 1) + incomingBytes > capBytes)
                lines.RemoveAt(0);
            File.WriteAllText(path, lines.Count > 0 ? string.Join('\n', lines) + "\n" : "");
        } catch { }
    }

    public async Task DrainAllAsync(
            string?                                   currentSessionId,
            Func<string, string, Task<DrainOutcome>>  poster,
            TimeSpan                                  budget,
            CancellationToken                         ct) {
        if (!Directory.Exists(spoolDir)) return;
        var sw = Stopwatch.StartNew();
        bool Expired() => sw.Elapsed >= budget;

        foreach (var sid in OrderedSessionIds(currentSessionId)) {
            if (Expired() || ct.IsCancellationRequested) return;
            if (await DrainSessionAsync(sid, poster, Expired, ct)) return; // transient → stop the pass
        }
    }

    // Current session first (if it has anything), then every other session once.
    IEnumerable<string> OrderedSessionIds(string? currentSessionId) {
        var ids = new List<string>();
        if (currentSessionId is not null && SafeSessionId.IsMatch(currentSessionId) && HasAny(currentSessionId))
            ids.Add(currentSessionId);
        foreach (var f in Directory.EnumerateFiles(spoolDir)) {
            var sid = SessionIdOf(f);
            if (sid is not null && !ids.Contains(sid)) ids.Add(sid);
        }
        return ids;
    }

    bool HasAny(string sid) =>
        File.Exists(Path.Combine(spoolDir, $"{sid}.jsonl"))
     || Directory.EnumerateFiles(spoolDir, $"{sid}.*.draining").Any();

    static string? SessionIdOf(string filePath) {
        var name = Path.GetFileName(filePath);
        var dot  = name.IndexOf('.');
        if (dot <= 0) return null;
        var sid = name[..dot];
        return SafeSessionId.IsMatch(sid) ? sid : null;
    }

    // Recovered temps (oldest first) then the rotated live file. Returns true => stop the whole pass.
    // NOTE: only ever recovers ITS OWN ".draining" temps — never the
    // ordered drain's ".ordered-*" temps (see DrainRoutesAsync below). The ordered drain deliberately
    // WITHHOLDS a phase's remainder mid-pass (e.g. a session-end held back until the transcript tail
    // and the leading non-terminal run are done); if this route-agnostic FIFO recovered that withheld
    // remainder it would happily re-deliver it immediately, out of order, via a different poster —
    // reintroducing the exact race the ordered drain exists to prevent. Distinct temp namespaces keep
    // the two drains from ever cross-consuming each other's in-flight files.
    async Task<bool> DrainSessionAsync(
            string sid, Func<string, string, Task<DrainOutcome>> poster, Func<bool> expired, CancellationToken ct) {
        foreach (var temp in Directory.EnumerateFiles(spoolDir, $"{sid}.*.draining").OrderBy(File.GetCreationTimeUtc)) {
            if (expired() || ct.IsCancellationRequested) return false;
            if (await DrainFileAsync(temp, poster, expired, ct)) return true;
        }

        var live = Path.Combine(spoolDir, $"{sid}.jsonl");
        if (!File.Exists(live) || expired() || ct.IsCancellationRequested) return false;

        var rotated = Path.Combine(spoolDir, $"{sid}.{Environment.ProcessId}-{Interlocked.Increment(ref seqCounter)}.draining");
        try { File.Move(live, rotated); }
        catch { return false; } // lost the atomic-rename race (or vanished) — the winner handles it
        return await DrainFileAsync(rotated, poster, expired, ct);
    }

    // Drain a private temp. Delivered/Drop advance; TransientStop or budget stops and keeps the remainder.
    static async Task<bool> DrainFileAsync(
            string path, Func<string, string, Task<DrainOutcome>> poster, Func<bool> expired, CancellationToken ct) {
        string[] lines;
        try { lines = await File.ReadAllLinesAsync(path, ct); }
        catch { return false; }

        var i = 0;
        for (; i < lines.Length; i++) {
            if (expired() || ct.IsCancellationRequested) break;
            if (string.IsNullOrWhiteSpace(lines[i])) continue;

            string? route, body;
            try {
                var node = JsonNode.Parse(lines[i]);
                route = node?["route"]?.GetValue<string>();
                body  = node?["body"]?.GetValue<string>();
            } catch { route = body = null; }
            if (route is null || body is null) continue; // skip old-format / malformed

            DrainOutcome outcome;
            try { outcome = await poster(route, body); }
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

    /// <summary>True if this session still has undelivered spool entries: a live .jsonl, a
    /// route-agnostic-drain (Claude/Cursor) ".draining" temp, or an ordered-drain ".ordered-*" temp.
    /// Callers use this as an ordering guard (e.g. "don't post my fresh session-end directly — a
    /// withheld ordered-drain phase is still in flight for this session") so both temp namespaces
    /// must be visible here even though only the ordered drain ever creates/consumes the latter.</summary>
    public bool HasBacklog(string sessionId) =>
        SafeSessionId.IsMatch(sessionId) && Directory.Exists(spoolDir)
        && (File.Exists(Path.Combine(spoolDir, $"{sessionId}.jsonl"))
            || Directory.EnumerateFiles(spoolDir, $"{sessionId}.*.draining").Any()
            || Directory.EnumerateFiles(spoolDir, $"{sessionId}.ordered-*").Any());

    /// <summary>Every distinct session id with a live .jsonl or recovered .draining/.ordered-* temp,
    /// in no particular order (callers that need current-session-first ordering add it themselves).</summary>
    public IEnumerable<string> SessionIdsWithBacklog() {
        if (!Directory.Exists(spoolDir)) return [];
        var ids = new List<string>();
        foreach (var f in Directory.EnumerateFiles(spoolDir)) {
            var sid = SessionIdOf(f);
            if (sid is not null && !ids.Contains(sid)) ids.Add(sid);
        }
        return ids;
    }

    /// <summary>
    /// Route-filtered drain for a single session: only entries whose route is a "session-end"
    /// (terminal, when <paramref name="isTerminal"/> is <c>true</c>) or anything else (non-terminal,
    /// when <c>false</c>) are posted. Arrival order within the session file guarantees start-before-end,
    /// so as soon as an entry of the OTHER phase is reached, draining stops and the remainder — including
    /// that entry — is preserved for the caller's next phase. Used by <see cref="LifecycleSpoolDrain"/>
    /// to enforce cross-spool ordering (lifecycle start &#8594; transcript tail &#8594; lifecycle end).
    ///
    /// <para><b>Distinct temp namespace.</b> Rotates the live file to a
    /// <c>{sid}.ordered-{pid}-{seq}</c> temp — NOT the <c>{sid}.{pid}-{seq}.draining</c> shape
    /// <see cref="DrainAllAsync"/> (route-agnostic FIFO, still used by Claude/Cursor) uses — so a
    /// deliberately-withheld phase can never be recovered and redelivered out of order by the
    /// unrelated FIFO drain, and vice versa.</para>
    ///
    /// <para>Returns the number of entries actually resolved (delivered or permanently dropped) in
    /// THIS call — never those left for a retry by a <c>TransientStop</c> or the budget. The caller
    /// uses a non-zero terminal-phase count to durably mark the session ended (see
    /// <see cref="MarkEnded"/>), which is how <see cref="LifecycleSpoolDrain"/> prevents a later
    /// straggler non-terminal entry from ever being delivered after session-end (BLOCKER-3).</para>
    /// </summary>
    public async Task<int> DrainRoutesAsync(
            string sid, bool isTerminal, Func<string, string, Task<DrainOutcome>> poster,
            Func<bool> expired, CancellationToken ct) {
        if (!SafeSessionId.IsMatch(sid) || !Directory.Exists(spoolDir)) return 0;

        var consumed = 0;

        foreach (var temp in Directory.EnumerateFiles(spoolDir, $"{sid}.ordered-*").OrderBy(File.GetCreationTimeUtc)) {
            if (expired() || ct.IsCancellationRequested) return consumed;
            var (stopped, n) = await DrainFileRoutesAsync(temp, isTerminal, poster, expired, ct);
            consumed += n;
            if (stopped) return consumed; // stopped — remainder kept
        }

        var live = Path.Combine(spoolDir, $"{sid}.jsonl");
        if (!File.Exists(live) || expired() || ct.IsCancellationRequested) return consumed;

        var rotated = Path.Combine(spoolDir, $"{sid}.ordered-{Environment.ProcessId}-{Interlocked.Increment(ref seqCounter)}");
        try { File.Move(live, rotated); }
        catch { return consumed; } // lost the atomic-rename race (or vanished) — the winner handles it
        var (_, more) = await DrainFileRoutesAsync(rotated, isTerminal, poster, expired, ct);
        return consumed + more;
    }

    internal static bool IsTerminalRoute(string route) {
        var slash = route.IndexOf('/');
        var head  = slash >= 0 ? route[..slash] : route;
        return head == "session-end";
    }

    // Drain a private temp, posting only entries matching isTerminal. Delivered/Drop advance and
    // count toward the returned "consumed" total; TransientStop, budget, or a phase mismatch stops
    // (Stopped=true) and keeps the remainder for a later pass.
    static async Task<(bool Stopped, int Consumed)> DrainFileRoutesAsync(
            string path, bool isTerminal, Func<string, string, Task<DrainOutcome>> poster,
            Func<bool> expired, CancellationToken ct) {
        string[] lines;
        try { lines = await File.ReadAllLinesAsync(path, ct); }
        catch { return (false, 0); }

        var i = 0;
        var consumed = 0;
        for (; i < lines.Length; i++) {
            if (expired() || ct.IsCancellationRequested) break;
            if (string.IsNullOrWhiteSpace(lines[i])) continue;

            string? route, body;
            try {
                var node = JsonNode.Parse(lines[i]);
                route = node?["route"]?.GetValue<string>();
                body  = node?["body"]?.GetValue<string>();
            } catch { route = body = null; }
            if (route is null || body is null) continue; // skip old-format / malformed

            if (IsTerminalRoute(route) != isTerminal) break; // other phase — stop, preserve order

            DrainOutcome outcome;
            try { outcome = await poster(route, body); }
            catch { outcome = DrainOutcome.TransientStop; }

            if (outcome == DrainOutcome.TransientStop) break;
            consumed++;
        }

        if (i >= lines.Length) {
            try { File.Delete(path); } catch { }
            return (false, consumed);
        }
        try { await File.WriteAllLinesAsync(path, lines.Skip(i), ct); } catch { }
        return (true, consumed);
    }

    /// <summary>
    /// True if a terminal (session-end) entry for this session has already been durably delivered
    /// (or permanently dropped) by a previous ordered-drain pass — see <see cref="MarkEnded"/>.
    /// Task 12 / BLOCKER-3: once true, <see cref="LifecycleSpoolDrain"/> must never attempt
    /// to deliver a straggler non-terminal entry for this session, even one that arrives (or is
    /// discovered) in a later pass — that would be a real cross-pass ordering violation, not just a
    /// same-pass one (the same-pass case is already prevented by <see cref="DrainRoutesAsync"/>'s
    /// phase-mismatch break).
    /// </summary>
    public bool IsMarkedEnded(string sessionId) =>
        SafeSessionId.IsMatch(sessionId) && File.Exists(EndedMarkerPath(sessionId));

    /// <summary>Durably marks a session as ended (see <see cref="IsMarkedEnded"/>). The marker's
    /// filename is dot-prefixed so it is invisible to <see cref="SessionIdOf"/> / the session-id-keyed
    /// enumerations (matching the existing <c>.last-drain</c> throttle-stamp convention) — it is swept
    /// only by <see cref="ReapOlderThan"/>'s unconditional file-age sweep.</summary>
    public void MarkEnded(string sessionId) {
        if (!SafeSessionId.IsMatch(sessionId)) return;
        try {
            Directory.CreateDirectory(spoolDir);
            File.WriteAllText(EndedMarkerPath(sessionId), "");
        } catch { }
    }

    /// <summary>
    /// Deletes any lifecycle entries remaining for a session already <see cref="MarkEnded"/> —
    /// a post-end straggler (e.g. a subagent-stop that arrived after session-end was already
    /// delivered) must be dropped, never delivered out of order.
    /// </summary>
    public void DiscardRemainder(string sessionId) {
        if (!SafeSessionId.IsMatch(sessionId)) return;
        try {
            if (!Directory.Exists(spoolDir)) return;

            var live = Path.Combine(spoolDir, $"{sessionId}.jsonl");
            if (File.Exists(live)) File.Delete(live);

            foreach (var temp in Directory.EnumerateFiles(spoolDir, $"{sessionId}.*.draining"))
                try { File.Delete(temp); } catch { }
            foreach (var temp in Directory.EnumerateFiles(spoolDir, $"{sessionId}.ordered-*"))
                try { File.Delete(temp); } catch { }
        } catch { }
    }

    string EndedMarkerPath(string sessionId) => Path.Combine(spoolDir, $".ended-{sessionId}");

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
    [GeneratedRegex("^[A-Za-z0-9_-]{1,64}$", RegexOptions.Compiled)]
    private static partial Regex SafeSessionIdRegex();
}
