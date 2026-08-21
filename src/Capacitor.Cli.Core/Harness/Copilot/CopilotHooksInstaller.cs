using System.Text.Json.Nodes;
using Capacitor.Cli.Core.Harness.Cursor;

namespace Capacitor.Cli.Core.Harness.Copilot;

/// <summary>
/// Marker + detection helpers for <c>~/.copilot/hooks/kcap.json</c>. Mirror of
/// <see cref="CursorHooksInstaller"/>: the npm postinstall hook calls
/// <see cref="IsInstalled"/> to gate the upgrade-time refresh, and
/// <see cref="WriteMarker"/> stamps the version after a successful write.
/// </summary>
public static class CopilotHooksInstaller {
    public const string MarkerFileName = ".kcap-hooks-version";

    /// <summary>
    /// True when the user has previously installed Copilot hooks via setup or
    /// <c>kcap plugin install --copilot</c>. Marker file presence OR an
    /// existing <c>kcap hook --copilot</c> entry in kcap.json — the
    /// hooks-json fallback covers pre-marker installs.
    /// </summary>
    public static bool IsInstalled(string hooksPath) {
        var dir = Path.GetDirectoryName(hooksPath);
        if (string.IsNullOrEmpty(dir) || !Directory.Exists(dir)) return false;

        if (File.Exists(Path.Combine(dir, MarkerFileName))) return true;
        if (!File.Exists(hooksPath)) return false;

        try {
            if (JsonNode.Parse(File.ReadAllText(hooksPath)) is not JsonObject root) return false;
            if (root["hooks"] is not JsonObject hooks) return false;

            foreach (var (_, entries) in hooks) {
                if (entries is not JsonArray arr) continue;

                if (arr.Any(CopilotHooksParser.EntryReferencesCapacitorCopilotHook)) {
                    return true;
                }
            }
        } catch { /* Malformed → treat as not installed. */ }
        return false;
    }

    public static string? ReadMarker(string hooksPath) {
        var dir = Path.GetDirectoryName(hooksPath);
        if (string.IsNullOrEmpty(dir)) return null;
        var marker = Path.Combine(dir, MarkerFileName);
        try { return File.Exists(marker) ? File.ReadAllText(marker).Trim() : null; }
        catch { return null; }
    }

    public static void WriteMarker(string hooksPath) {
        var dir = Path.GetDirectoryName(hooksPath);
        if (string.IsNullOrEmpty(dir)) return;
        try {
            Directory.CreateDirectory(dir);
            File.WriteAllText(Path.Combine(dir, MarkerFileName), CapacitorVersion.Current());
        } catch { /* best effort */ }
    }

    public static void DeleteMarker(string hooksPath) {
        var dir = Path.GetDirectoryName(hooksPath);
        if (string.IsNullOrEmpty(dir)) return;
        var marker = Path.Combine(dir, MarkerFileName);
        try { if (File.Exists(marker)) File.Delete(marker); } catch { }
    }
}
