namespace Capacitor.Cli.Core.Curation;

public static class CuratedBlock {
    public const string StartMarker = "<!-- kcap:curated:start -->";
    public const string EndMarker   = "<!-- kcap:curated:end -->";

    const string Heading = "## Curated guidelines";
    const string Note    = "_Managed by `kcap curate apply` — do not edit; changes are overwritten._";

    public static string? Render(IReadOnlyList<CuratedGuideline> guidelines) {
        if (guidelines.Count == 0) return null;

        var sorted = guidelines
            .OrderBy(g => g.Category, StringComparer.Ordinal)
            .ThenBy(g => g.Text, StringComparer.Ordinal)
            .ToList();

        var lines = new List<string> { StartMarker, Heading, "", Note, "" };
        foreach (var g in sorted) lines.Add($"- {g.Text}");
        lines.Add(EndMarker);
        return string.Join("\n", lines);
    }

    public static IReadOnlyList<string> ExtractBullets(string content) {
        var lines = Normalize(content);
        var loc   = Locate(lines);
        if (loc is null) return [];

        var (s, e) = loc.Value;
        var bullets = new List<string>();
        for (var i = s + 1; i < e; i++) {
            var t = lines[i];
            if (t.StartsWith("- ", StringComparison.Ordinal)) bullets.Add(t[2..]);
        }
        return bullets;
    }

    public static string Splice(string content, string? renderedBlock) {
        var newline = content.Contains("\r\n") ? "\r\n" : "\n";
        var lines   = new List<string>(Normalize(content));
        var loc     = Locate(lines.ToArray());   // throws on malformed

        if (renderedBlock is null) {
            if (loc is null) return content;     // nothing to remove
            RemoveBlock(lines, loc.Value);
            var removedJoined = string.Join(newline, lines);
            removedJoined = removedJoined.TrimEnd('\r', '\n');
            return removedJoined.Length == 0 ? removedJoined : removedJoined + newline;
        }

        var blockLines = renderedBlock.Replace("\r\n", "\n").Split('\n');

        if (loc is null) {
            // Append, separated by a blank line if the file has trailing content.
            if (lines.Count > 0 && !(lines.Count == 1 && lines[0].Length == 0) && lines[^1].Trim().Length != 0)
                lines.Add("");
            lines.AddRange(blockLines);
        } else {
            var (s, e) = loc.Value;
            lines.RemoveRange(s, e - s + 1);
            lines.InsertRange(s, blockLines);
        }

        var joined = string.Join(newline, lines);
        joined = joined.TrimEnd('\r', '\n');
        return joined.Length == 0 ? joined : joined + newline;
    }

    static string[] Normalize(string content) => content.Replace("\r\n", "\n").Split('\n');

    static void RemoveBlock(List<string> lines, (int start, int end) loc) {
        var (s, e) = loc;
        lines.RemoveRange(s, e - s + 1);
    }

    static (int start, int end)? Locate(string[] lines) {
        int start = -1, end = -1, starts = 0, ends = 0;
        for (var i = 0; i < lines.Length; i++) {
            if (lines[i] == StartMarker) { starts++; if (start < 0) start = i; }
            else if (lines[i] == EndMarker) { ends++; end = i; }
        }

        if (starts == 0 && ends == 0) return null;
        if (starts == 1 && ends == 1 && start < end) return (start, end);
        throw new CuratedBlockException(
            $"Malformed managed block: found {starts} start and {ends} end marker(s). Fix the file by hand and re-run.");
    }
}
