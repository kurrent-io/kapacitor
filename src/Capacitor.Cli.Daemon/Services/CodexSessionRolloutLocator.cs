using System.Globalization;
using System.Text.Json.Nodes;
using Capacitor.Cli.Core;

namespace Capacitor.Cli.Daemon.Services;

/// <summary>
/// Codex analog of <see cref="SessionTranscriptLocator"/>: best-effort discovery of a freshly
/// spawned hosted Codex reviewer's session id, for correlation/display only — the server
/// converges on daemon liveness, so a miss or mislink here never blocks the launch.
///
/// Codex layout: each session is
/// <c>~/.codex/sessions/YYYY/MM/DD/rollout-&lt;ISO-ts&gt;-&lt;uuid&gt;.jsonl</c>; the session id is
/// the trailing filename UUID, parsed by <see cref="CodexPaths.ExtractSessionIdFromFileName"/>.
/// The cwd lives in the opening <c>session_meta</c> envelope's <c>payload.cwd</c> (not the JSONL
/// root, unlike Claude).
///
/// The tree is shared across ALL of the user's Codex sessions, so candidates are filtered by
/// creation time at/after spawn (a rollout still being written by an older, unrelated session
/// must not pass just because its last-write time is recent) and verified by <c>payload.cwd</c>
/// equal to the agent's cwd; among matches, the one created closest after spawn wins — not the
/// numerically earliest creation. That creation time is read from the immutable timestamp
/// EMBEDDED IN THE FILENAME, not <see cref="File.GetCreationTimeUtc"/>: Linux cannot set file
/// birth time (<c>SetCreationTime</c> is a no-op) and does not reliably expose it, so the
/// filesystem creation time is unusable there for distinguishing rollouts — the filename stamp is
/// Codex's own session-start time, stable and cross-platform. The decision logic is pure and
/// unit-tested without a filesystem; only <see cref="TryLocate"/> touches disk.
/// </summary>
internal static class CodexSessionRolloutLocator {
    /// <summary>How many non-blank rollout lines to inspect for a <c>payload.cwd</c> before
    /// giving up on a candidate. The session_meta envelope is the very first line, so this is
    /// generous while still bounding the read.</summary>
    public const int MaxLinesToInspect = 50;

    /// <summary>Slack applied to the spawn-time filter to absorb filesystem timestamp
    /// granularity and small clock quirks. Safe to be generous: the cwd check is the real
    /// disambiguator; the time filter only avoids re-reading old sessions.</summary>
    static readonly TimeSpan FileTimeSkewTolerance = TimeSpan.FromSeconds(5);

    /// <summary>Outcome of inspecting a rollout's early lines for a <c>payload.cwd</c>.</summary>
    internal enum CwdMatch {
        /// <summary>A <c>payload.cwd</c> equal to the agent cwd was found — this is the reviewer's rollout.</summary>
        Yes,
        /// <summary>A <c>payload.cwd</c> was found but belongs to a different session — a permanent non-match.</summary>
        No,
        /// <summary>No parseable <c>payload.cwd</c> in the inspected lines — may still be being written; re-check later.</summary>
        Unknown,
    }

    /// <summary>
    /// Scans <paramref name="sessionsRoot"/> (the <c>~/.codex/sessions</c> tree) for a rollout
    /// written at/after <paramref name="spawnedAtUtc"/> whose <c>session_meta</c> reports
    /// <c>payload.cwd</c> equal to <paramref name="cwd"/>. Returns the normalized (dashless,
    /// lowercase) session id, or null when no candidate matches yet. Best-effort: unreadable or
    /// malformed candidates are skipped, never thrown.
    /// </summary>
    /// <param name="ruledOut">
    /// Optional set of file paths already confirmed to belong to another session. The caller
    /// polls every few seconds over a shared tree, so caching definitive non-matches avoids
    /// re-opening the user's own concurrently-written sessions on every tick. Only
    /// <em>definitive</em> non-matches are added (a foreign cwd, or a name that isn't a rollout);
    /// a rollout with no cwd yet is left out so the reviewer's own file is always re-checked.
    /// </param>
    public static string? TryLocate(string sessionsRoot, string cwd, DateTime spawnedAtUtc, ISet<string>? ruledOut = null) {
        if (!Directory.Exists(sessionsRoot)) return null;

        string?   bestId       = null;
        DateTime? bestCreation = null;

        foreach (var file in EnumerateRecentRolloutFiles(sessionsRoot, spawnedAtUtc)) {
            if (ruledOut?.Contains(file) == true) continue;

            try {
                if (CodexPaths.ExtractSessionIdFromFileName(file) is not { } sessionId) {
                    ruledOut?.Add(file); // not a rollout — its name won't change
                    continue;
                }

                // Read creation time from the filename stamp (immutable, cross-platform), not
                // File.GetCreationTimeUtc — Linux can neither set nor reliably read file birth
                // time. Fall back to the filesystem only for a name without a parseable stamp
                // (never a real Codex rollout); a mislink there is display-only either way.
                var creation = TryParseCreationFromFileName(file) ?? File.GetCreationTimeUtc(file);

                if (!IsNewEnough(creation, spawnedAtUtc)) continue;

                switch (MatchRollout(ReadFirstLines(file), cwd, DefaultPathComparison)) {
                    case CwdMatch.Yes:
                        // Prefer the rollout created closest AFTER spawn — not the numerically
                        // earliest creation, which could be an older, still-eligible (clock-skew
                        // tolerance) match. A mislink here is display-only (the server converges
                        // on daemon liveness), but correlating to the wrong session is still
                        // worth getting right.
                        if (bestCreation is null || IsCloserToSpawn(creation, bestCreation.Value, spawnedAtUtc)) {
                            bestCreation = creation;
                            bestId       = sessionId;
                        }

                        break;
                    case CwdMatch.No:
                        ruledOut?.Add(file); // another session's rollout — cwd is fixed
                        break;
                    // CwdMatch.Unknown: no cwd yet — leave uncached, re-check next tick.
                }
            } catch {
                // Candidate vanished mid-scan, is locked, or is otherwise unreadable — skip it;
                // the caller polls, so a transiently unreadable match is retried next tick.
            }
        }

        return bestId;
    }

    /// <summary>True when <paramref name="candidate"/> is a better spawn-time match than
    /// <paramref name="current"/>: a creation time at/after <paramref name="spawnedAtUtc"/>
    /// always beats one before it (barring the small clock-skew slack already applied by
    /// <see cref="IsNewEnough"/>, a rollout cannot causally predate its own spawn); within the
    /// same side, the creation closest to <paramref name="spawnedAtUtc"/> wins.</summary>
    static bool IsCloserToSpawn(DateTime candidate, DateTime current, DateTime spawnedAtUtc) {
        var candidateAfter = candidate >= spawnedAtUtc;
        var currentAfter   = current   >= spawnedAtUtc;

        if (candidateAfter != currentAfter) return candidateAfter;

        var candidateDistance = candidateAfter ? candidate - spawnedAtUtc : spawnedAtUtc - candidate;
        var currentDistance   = currentAfter   ? current   - spawnedAtUtc : spawnedAtUtc - current;

        return candidateDistance < currentDistance;
    }

    /// <summary>
    /// Classifies a candidate rollout by scanning up to <see cref="MaxLinesToInspect"/> non-blank
    /// lines for a JSON <c>payload.cwd</c> (the <c>session_meta</c> envelope). Returns
    /// <see cref="CwdMatch.Yes"/> on the first line whose <c>payload.cwd</c> equals
    /// <paramref name="cwd"/>, <see cref="CwdMatch.No"/> if a <c>payload.cwd</c> was seen but none
    /// matched, or <see cref="CwdMatch.Unknown"/> if none was seen. Partial/invalid JSON lines are
    /// tolerated (skipped) and both paths are normalized (separators, trailing separators) before
    /// comparing.
    /// </summary>
    internal static CwdMatch MatchRollout(IEnumerable<string> lines, string cwd, StringComparison comparison) {
        var target = NormalizePath(cwd);

        var inspected = 0;
        var sawCwd    = false;

        foreach (var line in lines) {
            if (inspected >= MaxLinesToInspect) break;
            if (string.IsNullOrWhiteSpace(line)) continue;

            inspected++;

            string? cwdValue;

            try {
                cwdValue = JsonNode.Parse(line)?["payload"]?["cwd"]?.GetValue<string>();
            } catch {
                continue; // partial/invalid JSONL line, non-object payload, or non-string cwd
            }

            if (cwdValue is null) continue;

            sawCwd = true;

            if (string.Equals(NormalizePath(cwdValue), target, comparison)) return CwdMatch.Yes;
        }

        return sawCwd ? CwdMatch.No : CwdMatch.Unknown;
    }

    /// <summary>
    /// True when the file's CREATION time is at/after the agent's spawn time, minus
    /// <see cref="FileTimeSkewTolerance"/>. Deliberately ignores last-write time: an older,
    /// unrelated session in the same cwd that is still being actively appended to must not pass
    /// this filter just because it was written to recently — a rollout created before the
    /// reviewer spawned is not this reviewer's session, regardless of ongoing writes. Filters
    /// out the user's own pre-existing sessions in the shared tree.
    /// </summary>
    public static bool IsNewEnough(DateTime creationTimeUtc, DateTime spawnedAtUtc)
        => creationTimeUtc >= spawnedAtUtc - FileTimeSkewTolerance;

    /// <summary>
    /// Parses Codex's session-start timestamp out of the rollout FILENAME
    /// (<c>rollout-&lt;yyyy-MM-ddTHH-mm-ss&gt;-&lt;uuid&gt;.jsonl</c>), returned as UTC. Codex writes
    /// this stamp — and the enclosing <c>YYYY/MM/DD</c> day folder — in LOCAL time, so it is parsed
    /// as local and converted; the daemon and Codex share a machine and clock, so this round-trips
    /// against the daemon's UTC spawn time. The stamp is immutable and, unlike
    /// <see cref="File.GetCreationTimeUtc"/>, cross-platform (Linux cannot set birth time and does
    /// not reliably expose it). Returns null when the name carries no parseable stamp.
    /// </summary>
    internal static DateTime? TryParseCreationFromFileName(string filePath) {
        var name = Path.GetFileNameWithoutExtension(filePath);

        // rollout-2026-05-07T17-50-21-019e0322-05fc-7570-be65-75719c3ea861
        // Drop the "rollout" prefix and the trailing 5-chunk UUID; the middle is the local stamp.
        var parts = name.Split('-');
        if (parts.Length < 6 || parts[0] != "rollout") return null;

        var stamp = string.Join('-', parts[1..^5]); // 2026-05-07T17-50-21

        return DateTime.TryParseExact(stamp, "yyyy-MM-ddTHH-mm-ss", CultureInfo.InvariantCulture,
                   DateTimeStyles.AssumeLocal | DateTimeStyles.AdjustToUniversal, out var utc)
            ? utc
            : null;
    }

    /// <summary>
    /// Enumerates <c>rollout-*.jsonl</c> files under <c>YYYY/MM/DD</c> day folders, pruning day
    /// folders older than the spawn's local date minus one day (Codex names folders by local
    /// date; the one-day slack covers a midnight boundary and timezone skew). Bounds the scan so
    /// a large history isn't walked on every poll tick.
    /// </summary>
    static IEnumerable<string> EnumerateRecentRolloutFiles(string sessionsRoot, DateTime spawnedAtUtc) {
        var cutoff = spawnedAtUtc.ToLocalTime().Date.AddDays(-1);

        foreach (var year in NumericDirs(sessionsRoot, cutoff.Year)) {
            foreach (var month in NumericDirs(year.Path, year.Value == cutoff.Year ? cutoff.Month : (int?)null)) {
                var dayMin = year.Value == cutoff.Year && month.Value == cutoff.Month ? cutoff.Day : (int?)null;

                foreach (var day in NumericDirs(month.Path, dayMin)) {
                    foreach (var file in Directory.EnumerateFiles(day.Path, "rollout-*.jsonl")) {
                        yield return file;
                    }
                }
            }
        }
    }

    static IEnumerable<(string Path, int Value)> NumericDirs(string parent, int? minInclusive) {
        if (!Directory.Exists(parent)) yield break;

        foreach (var dir in Directory.EnumerateDirectories(parent)) {
            if (!int.TryParse(Path.GetFileName(dir), out var value)) continue;
            if (minInclusive is { } min && value < min) continue;

            yield return (dir, value);
        }
    }

    static StringComparison DefaultPathComparison
        => OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

    /// <summary>Unifies separator style and strips trailing separators so
    /// <c>C:\r\p\</c>, <c>C:/r/p</c> and <c>C:\r\p</c> all compare equal.</summary>
    static string NormalizePath(string path) => path.Replace('\\', '/').TrimEnd('/');

    /// <summary>
    /// Reads up to <see cref="MaxLinesToInspect"/> lines with <see cref="FileShare.ReadWrite"/>
    /// (Codex is appending while we read) into memory, so the pure matcher never holds a file
    /// handle across JSON parsing.
    /// </summary>
    static List<string> ReadFirstLines(string path) {
        var lines = new List<string>(capacity: 16);

        using var fs     = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var reader = new StreamReader(fs);

        while (lines.Count < MaxLinesToInspect && reader.ReadLine() is { } line) {
            lines.Add(line);
        }

        return lines;
    }
}
