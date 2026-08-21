using System.Text.Json.Nodes;

namespace Capacitor.Cli.Core.Harness.Kiro;

/// <summary>
/// Marker + detection helpers for <c>~/.kiro/agents/kcap.json</c>. Mirror of
/// <see cref="Copilot.CopilotHooksInstaller"/>: the npm postinstall hook calls
/// <see cref="IsInstalled"/> to gate the upgrade-time refresh, and
/// <see cref="WriteMarker"/> stamps the version after a successful write.
/// </summary>
public static class KiroHooksInstaller {
    public const string MarkerFileName = ".kcap-hooks-version";

    /// <summary>
    /// True when the user has previously installed Kiro hooks via setup or
    /// <c>kcap plugin install --kiro</c>. Marker file presence OR an existing
    /// <c>kcap hook --kiro</c> entry in the agent JSON — the hooks-json fallback
    /// covers pre-marker installs.
    /// </summary>
    public static bool IsInstalled(string agentJsonPath) {
        var dir = Path.GetDirectoryName(agentJsonPath);
        if (string.IsNullOrEmpty(dir) || !Directory.Exists(dir)) return false;

        if (File.Exists(Path.Combine(dir, MarkerFileName))) return true;
        if (!File.Exists(agentJsonPath)) return false;

        try {
            if (JsonNode.Parse(File.ReadAllText(agentJsonPath)) is not JsonObject root) return false;
            if (root["hooks"] is not JsonObject hooks) return false;

            foreach (var (_, entries) in hooks) {
                if (entries is not JsonArray arr) continue;

                if (arr.Any(KiroHooksParser.EntryReferencesCapacitorKiroHook)) {
                    return true;
                }
            }
        } catch { /* Malformed → treat as not installed. */ }
        return false;
    }

    public static string? ReadMarker(string agentJsonPath) {
        var dir = Path.GetDirectoryName(agentJsonPath);
        if (string.IsNullOrEmpty(dir)) return null;
        var marker = Path.Combine(dir, MarkerFileName);
        try { return File.Exists(marker) ? File.ReadAllText(marker).Trim() : null; }
        catch { return null; }
    }

    /// <summary>
    /// Stamps the marker with the current version and, optionally, the default
    /// agent that kcap replaced (line 2) so <c>plugin remove --kiro</c> can
    /// restore it. <paramref name="previousDefault"/> is null when kcap was
    /// already the default (nothing to restore). Returns false on I/O failure —
    /// line 2 is the ONLY record of the previous default, so the caller must treat
    /// a failed write as a failed install (not best-effort).
    /// </summary>
    public static bool WriteMarker(string agentJsonPath, string? previousDefault = null) {
        var dir = Path.GetDirectoryName(agentJsonPath);
        if (string.IsNullOrEmpty(dir)) return false;
        try {
            Directory.CreateDirectory(dir);
            var body = previousDefault is { Length: > 0 } p
                ? $"{CapacitorVersion.Current()}\n{p}"
                : CapacitorVersion.Current();
            File.WriteAllText(Path.Combine(dir, MarkerFileName), body);
            return true;
        } catch {
            return false;
        }
    }

    /// <summary>The default agent kcap replaced at install (marker line 2), or null.</summary>
    public static string? ReadPreviousDefault(string agentJsonPath) {
        var dir = Path.GetDirectoryName(agentJsonPath);
        if (string.IsNullOrEmpty(dir)) return null;
        var marker = Path.Combine(dir, MarkerFileName);
        try {
            if (!File.Exists(marker)) return null;
            var lines = File.ReadAllLines(marker);
            return lines.Length >= 2 && lines[1].Trim() is { Length: > 0 } p ? p : null;
        } catch {
            return null;
        }
    }

    public static void DeleteMarker(string agentJsonPath) {
        var dir = Path.GetDirectoryName(agentJsonPath);
        if (string.IsNullOrEmpty(dir)) return;
        var marker = Path.Combine(dir, MarkerFileName);
        try { if (File.Exists(marker)) File.Delete(marker); } catch { }
    }
}
