using System.Text.Json.Nodes;

namespace Capacitor.Cli.Core;

/// <summary>
/// Marker + detection helpers for the Claude Code settings file
/// (<c>~/.claude/settings.json</c> for user scope, or
/// <c>&lt;repo&gt;/.claude/settings.local.json</c> for project scope).
/// Mirrors <see cref="AgentsSkillsInstaller"/> and
/// <see cref="CodexHooksInstaller"/>: the npm postinstall hook calls
/// <see cref="IsInstalled"/> to gate the upgrade-time refresh, and
/// <see cref="WriteMarker"/> stamps the version after a successful
/// install.
/// </summary>
/// <remarks>
/// The settings file itself is written by
/// <c>SetupCommand.InstallPlugin</c>; this type owns only the marker
/// side-channel and pre-marker detection. The marketplace source path
/// is absolute and changes between npm installs, so a refresh on
/// upgrade is meaningful — not just for command-string drift.
/// </remarks>
public static class ClaudePluginInstaller {
    public const string MarkerFileName = ".kcap-plugin-version";

    /// <summary>
    /// True when the user has previously installed the kcap Claude
    /// plugin via setup or <c>kcap plugin install</c>. Detection is
    /// marker OR any historical kcap entry in <paramref name="settingsPath"/>:
    /// <c>enabledPlugins["kcap@kcap" | "kcap@kurrent" | "kapacitor@kapacitor" | "kapacitor@kurrent"]</c>,
    /// or <c>extraKnownMarketplaces["kcap" | "kurrent" | "kapacitor"]</c>.
    /// Recognising the legacy keys lets the postinstall refresh pick up
    /// installs left by the pre-rename <c>kapacitor</c> CLI as well as
    /// the interim <c>kurrent</c> marketplace shape.
    /// </summary>
    public static bool IsInstalled(string settingsPath) {
        var dir = Path.GetDirectoryName(settingsPath);
        if (string.IsNullOrEmpty(dir) || !Directory.Exists(dir)) return false;

        if (File.Exists(Path.Combine(dir, MarkerFileName))) return true;
        if (!File.Exists(settingsPath)) return false;

        try {
            if (JsonNode.Parse(File.ReadAllText(settingsPath)) is not JsonObject root) return false;

            if (root["enabledPlugins"] is JsonObject enabled &&
                (HasEnabledFlag(enabled, "kcap@kcap")           ||
                 HasEnabledFlag(enabled, "kcap@kurrent")        ||
                 HasEnabledFlag(enabled, "kapacitor@kapacitor") ||
                 HasEnabledFlag(enabled, "kapacitor@kurrent"))) {
                return true;
            }

            if (root["extraKnownMarketplaces"] is JsonObject marketplaces &&
                (marketplaces["kcap"]      is not null ||
                 marketplaces["kurrent"]   is not null ||
                 marketplaces["kapacitor"] is not null)) {
                return true;
            }
        } catch {
            // Malformed JSON → treat as not installed.
        }
        return false;
    }

    static bool HasEnabledFlag(JsonObject enabled, string key) =>
        enabled[key] is JsonValue v && v.TryGetValue<bool>(out var on) && on;

    /// <summary>
    /// The directory Claude actually loads the kcap plugin from —
    /// <c>extraKnownMarketplaces.kcap.source.path</c> in <paramref name="settingsPath"/> —
    /// or null when nothing is registered or the file is unreadable. Distinct from where the
    /// current build would install from: after an upgrade the two can differ.
    /// </summary>
    public static string? RegisteredMarketplacePath(string settingsPath) {
        try {
            if (!File.Exists(settingsPath)) return null;
            if (JsonNode.Parse(File.ReadAllText(settingsPath)) is not JsonObject root) return null;

            var path = root["extraKnownMarketplaces"]?["kcap"]?["source"]?["path"];

            return path is JsonValue v && v.TryGetValue<string>(out var p) && !string.IsNullOrWhiteSpace(p)
                ? p
                : null;
        } catch {
            return null;
        }
    }

    public static string? ReadMarker(string settingsPath) {
        var dir = Path.GetDirectoryName(settingsPath);
        if (string.IsNullOrEmpty(dir)) return null;
        var marker = Path.Combine(dir, MarkerFileName);
        try {
            return File.Exists(marker) ? File.ReadAllText(marker).Trim() : null;
        } catch {
            return null;
        }
    }

    public static void WriteMarker(string settingsPath) {
        var dir = Path.GetDirectoryName(settingsPath);
        if (string.IsNullOrEmpty(dir)) return;
        try {
            Directory.CreateDirectory(dir);
            File.WriteAllText(Path.Combine(dir, MarkerFileName), CapacitorVersion.Current());
        } catch {
            // Best effort. Worst case the next upgrade re-runs the install
            // unconditionally, which is idempotent.
        }
    }

    public static void DeleteMarker(string settingsPath) {
        var dir = Path.GetDirectoryName(settingsPath);
        if (string.IsNullOrEmpty(dir)) return;
        var marker = Path.Combine(dir, MarkerFileName);
        try { if (File.Exists(marker)) File.Delete(marker); } catch { /* non-fatal */ }
    }
}
