using System.Text.Json.Nodes;
using Capacitor.Cli.Core.Harness.Codex;

namespace Capacitor.Cli.Core.Harness.Claude;

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
    /// True only when the plugin is CURRENTLY effective: an enabled plugin registration in
    /// <paramref name="settingsPath"/> AND a resolvable INSTALLED payload. Distinct from
    /// <see cref="IsInstalled"/>, which is the refresh gate ("previously installed → refresh
    /// it") and accepts the version marker alone: a stale marker (manual removal, failed
    /// refresh, npm re-layout) must never authorize a DESTRUCTIVE decision — doctor's
    /// duplicate cleanup and the launcher's merge-skip both assume Claude will actually load
    /// the plugin's servers in place of the entry being suppressed or removed.
    ///
    /// <para>The payload is resolved the way Claude loads it, not from a marketplace SOURCE
    /// dir (whose content proves nothing about what is installed/active): the enabled key's
    /// entry in <c>&lt;claude-home&gt;/plugins/installed_plugins.json</c> names the per-scope
    /// <c>installPath</c> (the plugin cache). Directory-sourced marketplaces are the one
    /// exception — Claude resolves those LIVE and their recorded cache path never
    /// materializes (verified against Claude Code 2.x), so when no cache payload exists the
    /// marketplace's <c>installLocation</c> in <c>known_marketplaces.json</c> is consulted.
    /// Anything unresolvable → not effective (fail closed: no destructive action).</para>
    /// </summary>
    public static bool IsEffectivelyInstalled(string settingsPath) {
        var enabledKey = EnabledKcapPluginKey(settingsPath);
        if (enabledKey is null) return false;

        var claudeHome = Path.GetDirectoryName(settingsPath);
        if (string.IsNullOrEmpty(claudeHome)) return false;
        var pluginsDir = Path.Combine(claudeHome, "plugins");

        try {
            // No install record for the enabled key → Claude has nothing to load.
            if (JsonNode.Parse(File.ReadAllText(Path.Combine(pluginsDir, "installed_plugins.json")))
                    is not JsonObject installedRoot ||
                installedRoot["plugins"] is not JsonObject plugins ||
                plugins[enabledKey] is not { } entryNode)
                return false;

            // v2 records an array of per-scope installs. Both callers gate on the USER-scope
            // settings.json enabled flag, so only a "user"-scoped install proves that flag's
            // payload — a local/project-scoped install belonging to some unrelated repo must
            // not make the plugin globally "effective". The bare-object shape (pre-v2
            // compatibility) predates scopes and is accepted as-is.
            List<JsonObject> entries = entryNode switch {
                JsonArray arr => [.. arr.OfType<JsonObject>().Where(e =>
                    e["scope"] is JsonValue sv && sv.TryGetValue<string>(out var scope) &&
                    string.Equals(scope, "user", StringComparison.Ordinal))],
                JsonObject single => [single],
                _                 => []
            };
            // No eligible user-scoped record (v2 with only local/project installs, or an
            // unrecognized shape) → not effective, and the directory-marketplace fallback
            // below must not run either: it only excuses a PHANTOM cache path on an
            // otherwise-eligible record, never the absence of an eligible record.
            if (entries.Count == 0) return false;
            foreach (var entry in entries) {
                if (entry["installPath"] is JsonValue v && v.TryGetValue<string>(out var installPath) &&
                    !string.IsNullOrWhiteSpace(installPath) &&
                    File.Exists(Path.Combine(installPath, ".mcp.json")))
                    return true;
            }

            // Directory-sourced marketplace: loaded live from installLocation, never cached.
            // The exception applies ONLY when the source type is exactly "directory" — a
            // git/github marketplace IS cached, so its lingering checkout under
            // installLocation proves nothing once the installed cache is gone; accepting it
            // would let doctor delete the only working registrations.
            var marketplaceName = enabledKey[(enabledKey.IndexOf('@') + 1)..];
            return JsonNode.Parse(File.ReadAllText(Path.Combine(pluginsDir, "known_marketplaces.json")))
                       is JsonObject markets &&
                   markets[marketplaceName] is JsonObject market &&
                   market["source"]?["source"] is JsonValue srcType &&
                   srcType.TryGetValue<string>(out var sourceType) &&
                   string.Equals(sourceType, "directory", StringComparison.Ordinal) &&
                   market["installLocation"] is JsonValue loc &&
                   loc.TryGetValue<string>(out var installLocation) &&
                   !string.IsNullOrWhiteSpace(installLocation) &&
                   File.Exists(Path.Combine(installLocation, ".mcp.json"));
        } catch {
            return false; // missing/malformed plugin records → fail closed
        }
    }

    /// <summary>The first recognized kcap plugin key enabled in settings, else null.</summary>
    static string? EnabledKcapPluginKey(string settingsPath) {
        try {
            if (!File.Exists(settingsPath)) return null;
            if (JsonNode.Parse(File.ReadAllText(settingsPath)) is not JsonObject root) return null;
            if (root["enabledPlugins"] is not JsonObject enabled) return null;

            foreach (var key in (string[]) ["kcap@kcap", "kcap@kurrent", "kapacitor@kapacitor", "kapacitor@kurrent"])
                if (HasEnabledFlag(enabled, key))
                    return key;
        } catch {
            // malformed settings → nothing provably enabled
        }
        return null;
    }

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
            if (root["extraKnownMarketplaces"] is not JsonObject marketplaces) return null;

            // Same key set IsInstalled recognises, current shape first — the two must agree or
            // the gate composing them falls back to the wrong directory for legacy installs.
            foreach (var key in (string[]) ["kcap", "kurrent", "kapacitor"]) {
                if (marketplaces[key]?["source"]?["path"] is JsonValue v
                    && v.TryGetValue<string>(out var p)
                    && !string.IsNullOrWhiteSpace(p)) {
                    return p;
                }
            }

            return null;
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
