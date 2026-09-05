using System.Text.Json;

namespace Capacitor.Cli.Core.Harness.Claude;

/// <summary>
/// Extracts Claude Code's own session title from a project transcript: the last non-blank
/// <c>ai-title</c> line wins (the CLI revises it over the session). The older
/// <c>{"type":"summary","summary":...}</c> shape still appears in transcripts written by
/// earlier Claude Code versions and must keep being accepted. Returns null when the file is
/// unreadable or carries no title; never throws. Capped at 120 chars — the
/// <c>/hooks/set-title</c> limit.
/// </summary>
public static class ClaudeNativeTitle {
    public static string? TryExtract(string transcriptPath) {
        string? title = null;

        try {
            // The agent owns this file and appends to it live — the open must not deny writers.
            using var stream = new FileStream(transcriptPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            using var reader = new StreamReader(stream);

            while (reader.ReadLine() is { } line) {
                if (!line.Contains("\"ai-title\"") && !line.Contains("\"summary\"")) continue;

                try {
                    using var doc  = JsonDocument.Parse(line);
                    var       root = doc.RootElement;

                    var candidate = root.Str("type") switch {
                        "ai-title" => root.Str("aiTitle"),
                        "summary"  => root.Str("summary"),
                        _          => null,
                    };

                    if (!string.IsNullOrWhiteSpace(candidate)) title = candidate.Trim();
                } catch (JsonException) { }
            }
        } catch {
            return null;
        }

        return title is { Length: > 120 } ? title[..120] : title;
    }
}
