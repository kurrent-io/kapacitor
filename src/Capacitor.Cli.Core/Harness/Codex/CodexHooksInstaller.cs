using System.Text.Json.Nodes;

namespace Capacitor.Cli.Core.Harness.Codex;

/// <summary>
/// Marker + detection helpers for the Codex hooks file
/// (<c>~/.codex/hooks.json</c>). Mirror of <see cref="AgentsSkillsInstaller"/>:
/// the npm postinstall hook uses <see cref="IsInstalled"/> to gate the
/// upgrade-time refresh, and <see cref="WriteMarker"/> stamps the version
/// after a successful write so subsequent upgrades can short-circuit when
/// the marker already matches.
/// </summary>
/// <remarks>
/// The hooks.json itself is written by <c>PluginCommand.InstallCodexHooks</c>;
/// this type owns only the marker side-channel and pre-marker detection.
/// </remarks>
public static class CodexHooksInstaller {
    /// <summary>
    /// File name written next to <c>hooks.json</c> after a successful install.
    /// Holds the CLI version that produced the entries.
    /// </summary>
    public const string MarkerFileName = ".kcap-hooks-version";

    /// <summary>
    /// True when the user has previously installed Codex hooks via setup or
    /// <c>kcap plugin install --codex</c>. The npm postinstall hook uses
    /// this to decide whether to refresh on upgrade vs. leave the system alone.
    /// </summary>
    /// <remarks>
    /// Detection is marker OR existing <c>kcap codex-hook</c> entry in
    /// <paramref name="hooksPath"/>. The hooks-json fallback covers users
    /// whose install predates the marker — without it, the first upgrade onto
    /// a marker-aware build would no-op and leave stale command strings in
    /// place forever.
    /// </remarks>
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

                if (arr.Any(CodexHooksParser.EntryReferencesCapacitorCodexHook)) {
                    return true;
                }
            }
        } catch {
            // Malformed JSON → treat as not installed; the next setup run
            // will rewrite the file cleanly.
        }
        return false;
    }

    /// <summary>
    /// True iff <paramref name="hooksPath"/> has any hook entry referencing the
    /// <c>kcap codex-hook</c> command — the "is kcap wired into Codex?" check the <c>kcap status</c>
    /// Hooks line has always used (reference only, NOT the marker), now shared with the new-harness
    /// nudge. Reads with shared access so it never blocks Codex writing its own hooks.json.
    /// </summary>
    public static bool ReferencesKcapHook(string hooksPath) {
        try {
            if (!File.Exists(hooksPath)) return false;
            if (JsonNode.Parse(SharedFileText.ReadAllText(hooksPath)) is not JsonObject root) return false;
            if (root["hooks"] is not JsonObject hooks) return false;

            foreach (var (_, value) in hooks)
                if (value is JsonArray entries && entries.Any(CodexHooksParser.EntryReferencesCapacitorCodexHook))
                    return true;

            return false;
        } catch {
            return false;
        }
    }

    /// <summary>
    /// Returns the version string from the marker, or null when absent or
    /// unreadable.
    /// </summary>
    public static string? ReadMarker(string hooksPath) {
        var dir = Path.GetDirectoryName(hooksPath);
        if (string.IsNullOrEmpty(dir)) return null;
        var marker = Path.Combine(dir, MarkerFileName);
        try {
            return File.Exists(marker) ? File.ReadAllText(marker).Trim() : null;
        } catch {
            return null;
        }
    }

    public static void WriteMarker(string hooksPath) {
        var dir = Path.GetDirectoryName(hooksPath);
        if (string.IsNullOrEmpty(dir)) return;
        try {
            Directory.CreateDirectory(dir);
            File.WriteAllText(Path.Combine(dir, MarkerFileName), CapacitorVersion.Current());
        } catch {
            // Best effort. Worst case the next upgrade re-runs the install
            // unconditionally, which is idempotent.
        }
    }

    public static void DeleteMarker(string hooksPath) {
        var dir = Path.GetDirectoryName(hooksPath);
        if (string.IsNullOrEmpty(dir)) return;
        var marker = Path.Combine(dir, MarkerFileName);
        try { if (File.Exists(marker)) File.Delete(marker); } catch { /* non-fatal */ }
    }
}
