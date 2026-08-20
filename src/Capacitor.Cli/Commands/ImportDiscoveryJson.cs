using System.Text.Json;
using System.Text.Json.Serialization;

namespace Capacitor.Cli.Commands;

/// <summary>Machine-readable payload for <c>kcap import --discover --json</c>.</summary>
/// <remarks>
/// A separate shape from <see cref="ImportDiscoverySummary"/> rather than serializing that directly:
/// the summary is free to change with the import pipeline, whereas this is a contract someone else's
/// code reads. Mirrors <c>kcap daemon service status --json</c> — snake_case, source-generated because
/// the CLI is NativeAOT. Indented rather than compact, because a person reads this too.
/// </remarks>
public sealed record ImportDiscoveryJson(
    IReadOnlyList<ImportDiscoveryRepoJson>   Repos,
    int                                      UnmatchedSessions,
    IReadOnlyList<ImportDiscoveryWindowJson> Windows);

public sealed record ImportDiscoveryRepoJson(
    string Owner, string Name, int Sessions, string? LastSessionAt);

/// <param name="Since">ISO date, or null for "everything".</param>
public sealed record ImportDiscoveryWindowJson(string? Since, int Sessions);

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.SnakeCaseLower, WriteIndented = true)]
[JsonSerializable(typeof(ImportDiscoveryJson))]
public partial class ImportDiscoveryJsonContext : JsonSerializerContext;

/// <summary>Pure renderer, kept apart from I/O so it is directly testable.</summary>
public static class ImportDiscoveryRender {
    public static string ToJson(ImportDiscoverySummary summary) =>
        JsonSerializer.Serialize(
            new ImportDiscoveryJson(
                [.. summary.Repos.Select(r => new ImportDiscoveryRepoJson(
                    r.Owner, r.Name, r.SessionCount, r.LastSessionAt?.UtcDateTime.ToString("O")))],
                summary.UnmatchedCount,
                [.. summary.ByWindow.Select(w => new ImportDiscoveryWindowJson(
                    w.Since?.ToString("yyyy-MM-dd"), w.SessionCount))]),
            ImportDiscoveryJsonContext.Default.ImportDiscoveryJson);

    public static string ToText(ImportDiscoverySummary summary) {
        var lines = new List<string> { "Discovered sessions:" };

        foreach (var r in summary.Repos) {
            var last = r.LastSessionAt is { } at ? at.UtcDateTime.ToString("yyyy-MM-dd") : "unknown";

            lines.Add($"  {r.Owner}/{r.Name}  {r.SessionCount} session{(r.SessionCount == 1 ? "" : "s")}, last {last}");
        }

        if (summary.Repos.Count == 0) lines.Add("  (no sessions could be attributed to a repository)");

        if (summary.UnmatchedCount > 0) {
            lines.Add($"  couldn't match to a repository: {summary.UnmatchedCount}");
        }

        lines.Add("");
        lines.Add("By window:");

        foreach (var w in summary.ByWindow) {
            lines.Add($"  {(w.Since is { } s ? $"since {s:yyyy-MM-dd}" : "everything"),-22}{w.SessionCount}");
        }

        return string.Join(Environment.NewLine, lines);
    }
}
