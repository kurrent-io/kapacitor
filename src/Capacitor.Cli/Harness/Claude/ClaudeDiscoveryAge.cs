using System.Text.Json;
using Capacitor.Cli.Core;

namespace Capacitor.Cli.Harness.Claude;

/// <summary>
/// Dating a Claude transcript the way its <c>--since</c> does — by the first <c>timestamp</c> the
/// metadata scan would find.
/// </summary>
/// <remarks>
/// Deliberately mirrors <c>ExtractSessionMetadata</c>'s scan rather than approximating it: the same
/// 50-line bound, and a malformed record is skipped rather than ending the search. Reading fewer
/// lines, or stopping at the first bad one, makes discovery fall back to the file's last write for a
/// timestamp the import then finds — reporting a months-old session inside a 30-day window.
/// </remarks>
internal static class ClaudeDiscoveryAge {
    const int MaxLinesScanned = 50;

    public static DateTimeOffset? FirstTimestamp(string filePath) {
        try {
            // FileShare.ReadWrite: the agent owns this file and may be appending to it, and a read
            // that denies writers stalls it on Windows, where the sharing is mandatory.
            using var stream = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            using var reader = new StreamReader(stream);

            for (var scanned = 0; scanned < MaxLinesScanned; scanned++) {
                if (reader.ReadLine() is not { } line) return null;
                if (string.IsNullOrWhiteSpace(line)) continue;

                try {
                    using var doc = JsonDocument.Parse(line);

                    if (doc.RootElement.Str("timestamp") is { } ts
                     && DateTimeOffset.TryParse(ts, out var parsed)) {
                        return parsed;
                    }
                } catch (JsonException) {
                    // One unparseable line is not the end of the search — the real extractor skips it.
                }
            }
        } catch {
            // Unreadable: the caller falls back to the last write, as the --since filter does.
        }

        return null;
    }
}
