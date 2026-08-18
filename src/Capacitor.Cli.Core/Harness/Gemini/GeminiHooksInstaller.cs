using System.Text.Json.Nodes;

namespace Capacitor.Cli.Core.Harness.Gemini;

/// <summary>
/// Marker + detection helpers for kcap's hooks inside Gemini CLI's shared
/// <c>~/.gemini/settings.json</c>. Mirror of <c>CopilotHooksInstaller</c>, but
/// the detection fallback inspects the merged <c>hooks</c> block rather than a
/// kcap-owned file. The actual merge/unmerge lives in <c>PluginCommand</c>
/// (it must preserve user-authored hook entries — see <see cref="GeminiHooksParser"/>).
/// </summary>
public static class GeminiHooksInstaller {
    public const string MarkerFileName = ".kcap-hooks-version";

    /// <summary>
    /// True when kcap's Gemini hooks were previously installed. Marker file
    /// presence OR an existing <c>kcap hook --gemini</c> entry in settings.json
    /// (the latter covers pre-marker installs).
    /// </summary>
    public static bool IsInstalled(string settingsPath) {
        var dir = Path.GetDirectoryName(settingsPath);
        if (string.IsNullOrEmpty(dir) || !Directory.Exists(dir)) return false;

        if (File.Exists(Path.Combine(dir, MarkerFileName))) return true;
        if (!File.Exists(settingsPath)) return false;

        try {
            if (JsonNode.Parse(File.ReadAllText(settingsPath)) is not JsonObject root) return false;
            if (root["hooks"] is not JsonObject hooks) return false;

            foreach (var (_, entries) in hooks) {
                if (entries is JsonArray arr && arr.Any(GeminiHooksParser.EntryReferencesCapacitorGeminiHook)) {
                    return true;
                }
            }
        } catch { /* Malformed → treat as not installed. */ }
        return false;
    }

    public static string? ReadMarker(string settingsPath) {
        var dir = Path.GetDirectoryName(settingsPath);
        if (string.IsNullOrEmpty(dir)) return null;
        var marker = Path.Combine(dir, MarkerFileName);
        try { return File.Exists(marker) ? File.ReadAllText(marker).Trim() : null; }
        catch { return null; }
    }

    public static void WriteMarker(string settingsPath) {
        var dir = Path.GetDirectoryName(settingsPath);
        if (string.IsNullOrEmpty(dir)) return;
        try {
            Directory.CreateDirectory(dir);
            File.WriteAllText(Path.Combine(dir, MarkerFileName), CapacitorVersion.Current());
        } catch { /* best effort */ }
    }

    public static void DeleteMarker(string settingsPath) {
        var dir = Path.GetDirectoryName(settingsPath);
        if (string.IsNullOrEmpty(dir)) return;
        var marker = Path.Combine(dir, MarkerFileName);
        try { if (File.Exists(marker)) File.Delete(marker); } catch { }
    }
}
