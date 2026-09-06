using System.Text.Json;

namespace Capacitor.Cli.Core;

/// The command a shell tool call was given, dug out of the tool input each vendor shapes
/// differently: Codex writes `cmd`, Claude writes `command` as a string, and the argv form is a
/// `bash -lc &lt;script&gt;` triple.
public static class ToolCommandText {
    /// Null when the input carries no command. An argv triple hands its script through, since
    /// <see cref="Harness.Codex.CodexToolKinds"/>'s classifier only peels the wrapper when the
    /// script is one quoted token.
    public static string? From(JsonElement root) {
        if (!root.IsObject) return null;
        if (root.Str("cmd") is { } cmd) return cmd;
        if (root.Str("command") is { } command) return command;
        if (root.Arr("command") is not { } argv) return null;
        var parts = argv.EnumerateArray().Where(p => p.IsString).Select(p => p.GetString()!).ToList();
        if (parts.Count == 3 && parts[1] is "-lc" or "-c" && parts[0].EndsWith("sh", StringComparison.Ordinal)) return parts[2];
        return string.Join(' ', parts);
    }

    /// The same read from an unparsed input string; null when it is absent, not JSON, or not an
    /// object.
    public static string? From(string? inputJson) {
        if (string.IsNullOrEmpty(inputJson)) return null;
        try {
            using var doc = JsonDocument.Parse(inputJson);
            return From(doc.RootElement);
        } catch (JsonException) {
            return null;
        }
    }
}
