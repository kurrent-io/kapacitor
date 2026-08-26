using System.Text.Json;

namespace Capacitor.App.ViewModels;

/// The one-line detail a tool row shows beside its name, read from the call's input object.
public static class ToolDetail {
    const int MaxLength = 80;

    static readonly string[] Keys = [
        "description", "command", "cmd", "file_path", "path", "pattern", "query", "url", "skill", "prompt", "input",
    ];

    public static string From(string? inputJson) {
        if (string.IsNullOrEmpty(inputJson)) return "";
        try {
            using var doc = JsonDocument.Parse(inputJson);
            if (doc.RootElement.ValueKind != JsonValueKind.Object) return "";
            foreach (var key in Keys) {
                if (doc.RootElement.TryGetProperty(key, out var value)
                    && value.ValueKind == JsonValueKind.String
                    && value.GetString() is { } s && s.Trim().Length > 0)
                    return FirstLine(s);
            }
        } catch (JsonException) { }
        return "";
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
