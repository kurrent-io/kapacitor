using System.Text.Json;

namespace Capacitor.App.ViewModels;

/// The one-line detail a tool row shows beside its name, read from the call's input object. A
/// path under the session's root reads relative to it, the way the web UI shows it: the root is
/// the repository, or the daemon's per-agent worktree beneath it when the path is in one.
public static class ToolDetail {
    const int MaxLength = 80;
    const string WorktreesSegment = "/.capacitor/worktrees/";

    static readonly string[] Keys = [
        "description", "command", "cmd", "file_path", "path", "notebook_path", "pattern", "query", "url", "skill", "prompt", "input",
    ];
    static readonly string[] PathKeys = ["file_path", "path", "notebook_path"];

    public static string From(string? inputJson, string? root = null) {
        if (string.IsNullOrEmpty(inputJson)) return "";
        try {
            using var doc = JsonDocument.Parse(inputJson);
            if (doc.RootElement.ValueKind != JsonValueKind.Object) return "";
            foreach (var key in Keys) {
                if (doc.RootElement.TryGetProperty(key, out var value)
                    && value.ValueKind == JsonValueKind.String
                    && value.GetString() is { } s && s.Trim().Length > 0)
                    return FirstLine(PathKeys.Contains(key) ? Relative(s.Trim(), root) : s);
            }
        } catch (JsonException) { }
        return "";
    }

    static string Relative(string path, string? root) {
        if (string.IsNullOrEmpty(root)) return path;
        var prefix = root.TrimEnd('/') + "/";
        if (!path.StartsWith(prefix, StringComparison.Ordinal)) return path;
        var rest = path[prefix.Length..];
        if (rest.StartsWith(WorktreesSegment[1..], StringComparison.Ordinal)) {
            var afterWorktree = rest.IndexOf('/', WorktreesSegment.Length - 1);
            if (afterWorktree >= 0) rest = rest[(afterWorktree + 1)..];
        }
        return rest.Length == 0 ? path : rest;
    }

    static string FirstLine(string text) {
        var line = text.Trim();
        var newline = line.IndexOfAny(['\r', '\n']);
        if (newline >= 0) line = line[..newline].TrimEnd();
        if (line.Length <= MaxLength) return line;
        var cut = MaxLength - 1;
        if (char.IsHighSurrogate(line[cut - 1])) cut--;
        return string.Concat(line.AsSpan(0, cut), "…");
    }
}
