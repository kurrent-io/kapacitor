using System.Text.Json;

namespace Capacitor.Cli.Commands;

/// <summary>
/// How old a discovered session is, by the same rule <c>--since</c> applies to it.
/// </summary>
/// <remarks>
/// There is no single rule: <c>--since</c> prunes Codex by the <c>YYYY/MM/DD</c> directory a rollout
/// sits in, filters Claude on the transcript's first message timestamp (falling back to the file's
/// last write), and compares everyone else against the <c>FirstTimestamp</c> their discovery already
/// populates. A window count that used one rule for all three would promise an import it would not
/// deliver — which is the whole point of showing the count next to the choice.
///
/// Claude is the only vendor this reads a file for, and only its first line: discovery is otherwise a
/// directory scan, and its <c>FirstTimestamp</c> stays null until classification.
/// </remarks>
internal static class ImportDiscoveryAge {
    public static DateTimeOffset? Of(DiscoveredSession session) {
        var path = session.SourceMeta.TryGetValue("FilePath", out var raw) ? raw as string : null;

        return session.Vendor switch {
            // The prune is on the directory, so the directory is the date it compares.
            "codex"  => path is null ? session.FirstTimestamp : DayFromCodexPath(path) ?? LastWrite(path),
            "claude" => path is null ? session.FirstTimestamp : FirstTranscriptTimestamp(path) ?? LastWrite(path),
            _        => session.FirstTimestamp,
        };
    }

    /// <summary>Rollouts live under <c>sessions/YYYY/MM/DD/</c>; null when the path is not that shape.</summary>
    internal static DateTimeOffset? DayFromCodexPath(string filePath) {
        var day   = Path.GetDirectoryName(filePath);
        var month = Path.GetDirectoryName(day);
        var year  = Path.GetDirectoryName(month);

        if (day is null || month is null || year is null) return null;

        return int.TryParse(Path.GetFileName(year),  out var y)
            && int.TryParse(Path.GetFileName(month), out var m)
            && int.TryParse(Path.GetFileName(day),   out var d)
            && y is >= 1 and <= 9999 && m is >= 1 and <= 12 && d >= 1 && d <= DateTime.DaysInMonth(y, m)
            ? new DateTimeOffset(new DateTime(y, m, d, 0, 0, 0, DateTimeKind.Utc))
            : null;
    }

    /// <summary>
    /// The <c>timestamp</c> on the transcript's first line carrying one.
    /// </summary>
    /// <remarks>
    /// Opened <c>FileShare.ReadWrite</c>: the agent owns this file and may be appending to it, and a
    /// read that denies writers stalls it on Windows, where that sharing is mandatory.
    /// </remarks>
    internal static DateTimeOffset? FirstTranscriptTimestamp(string filePath) {
        try {
            using var stream = new FileStream(
                filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            using var reader = new StreamReader(stream);

            // A handful of lines, not the file: a transcript can be very large, and the timestamp is
            // on the first real record.
            for (var i = 0; i < 8; i++) {
                if (reader.ReadLine() is not { } line) return null;
                if (string.IsNullOrWhiteSpace(line)) continue;

                using var doc = JsonDocument.Parse(line);

                if (doc.RootElement.TryGetProperty("timestamp", out var ts)
                 && ts.ValueKind == JsonValueKind.String
                 && DateTimeOffset.TryParse(ts.GetString(), out var parsed)) {
                    return parsed;
                }
            }
        } catch {
            // Unreadable or not JSON — the caller falls back to the file's last write, which is what
            // the --since filter does for the same failure.
        }

        return null;
    }

    static DateTimeOffset? LastWrite(string filePath) {
        try { return File.GetLastWriteTimeUtc(filePath); } catch { return null; }
    }
}
