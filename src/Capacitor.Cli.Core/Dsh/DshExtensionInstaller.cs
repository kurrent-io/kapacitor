namespace Capacitor.Cli.Core.Dsh;

/// <summary>
/// Installs / removes kcap's live-ingest plugin for DeepSeek Harness (dsh), AI-2020.
/// dsh has no shell hooks, so — like OpenCode/Pi — kcap ships a plugin that dsh's
/// Cordis plugin system auto-discovers and loads in-process. Because dsh is
/// event-sourced and writes its own <c>session.jsonl</c>, the dsh plugin is far
/// simpler than OpenCode's <c>kcap.ts</c>: it only shells the kcap CLI on lifecycle
/// boundaries — on session create <c>kcap hook --dsh --event session-start --session
/// &lt;id&gt; --file &lt;session.jsonl&gt; …</c> (which POSTs lifecycle + spawns the
/// transcript watcher that tails the file directly), and on session idle/teardown
/// <c>kcap hook --dsh --event session-end …</c>. No SDK fetch, no JSONL synthesis.
///
/// <para><b>TODO (AI-2020):</b> <see cref="ExtensionContent"/> is a documented
/// PLACEHOLDER — the real Cordis plugin manifest/language and dsh's plugin-discovery
/// directory are not yet confirmed, so this installer is intentionally NOT wired into
/// <c>kcap plugin install</c> / the setup wizard yet (auto-installing a non-functional
/// plugin would be worse than none). The install/remove/marker MECHANICS below are
/// final and unit-tested; only the embedded plugin body + <see cref="DshPaths.KcapPlugin"/>
/// need filling once dsh's plugin API is known. A hand-written dsh plugin that invokes
/// the <c>kcap hook --dsh</c> contract above works end-to-end with the rest of the
/// pipeline today.</para>
///
/// <para><see cref="ExtensionContent"/> is embedded as a const (no manifest-resource
/// reflection) to stay NativeAOT-safe, mirroring <see cref="OpenCode.OpenCodeExtensionInstaller"/>.</para>
/// </summary>
public static class DshExtensionInstaller {
    public const string MarkerFileName = ".kcap-extension-version";

    /// <summary>
    /// PLACEHOLDER dsh plugin (TODO AI-2020: replace with the real Cordis plugin once
    /// dsh's plugin API is confirmed). Documents the exact CLI contract the real plugin
    /// must implement so live capture lights up. Dependency-free + fail-safe by design.
    /// </summary>
    public const string ExtensionContent =
        """
        // kcap dsh live-ingest plugin — PLACEHOLDER (AI-2020).
        //
        // dsh is event-sourced: its on-disk session.jsonl IS the SessionEvent stream the
        // kcap watcher tails, so this plugin only needs to notify kcap of lifecycle
        // boundaries. On the real dsh/Cordis plugin API, subscribe to session lifecycle
        // and shell the kcap CLI (fail-open — never disrupt the dsh session):
        //
        //   on session create/start:
        //     kcap hook --dsh --event session-start --session <id> --file <path/to/session.jsonl> \
        //       [--cwd <cwd>] [--model <m>] [--provider <p>] [--version <v>]
        //
        //   on session idle/teardown:
        //     kcap hook --dsh --event session-end --session <id> --file <path> [--reason <r>] [--cwd <cwd>]
        //
        // The watcher tails <file> directly (vendor=dsh); the server normalizes it via the
        // keyed DeepSeekHarnessTranscriptNormalizer. No SDK fetch or JSONL synthesis needed.
        """;

    /// <summary>
    /// True when the plugin (or its marker) is present. Marker covers the case where a
    /// user deleted the plugin but kept the dir.
    /// </summary>
    public static bool IsInstalled(string pluginPath) {
        if (File.Exists(pluginPath)) return true;
        var dir = Path.GetDirectoryName(pluginPath);
        return dir is not null && File.Exists(Path.Combine(dir, MarkerFileName));
    }

    public static string? ReadMarker(string pluginPath) {
        var dir = Path.GetDirectoryName(pluginPath);
        if (string.IsNullOrEmpty(dir)) return null;
        var marker = Path.Combine(dir, MarkerFileName);
        try { return File.Exists(marker) ? File.ReadAllText(marker).Trim() : null; }
        catch { return null; }
    }

    public static void WriteMarker(string pluginPath) {
        var dir = Path.GetDirectoryName(pluginPath);
        if (string.IsNullOrEmpty(dir)) return;
        try {
            Directory.CreateDirectory(dir);
            File.WriteAllText(Path.Combine(dir, MarkerFileName), CapacitorVersion.Current());
        } catch { /* best effort */ }
    }

    public static void DeleteMarker(string pluginPath) {
        var dir = Path.GetDirectoryName(pluginPath);
        if (string.IsNullOrEmpty(dir)) return;
        var marker = Path.Combine(dir, MarkerFileName);
        try { if (File.Exists(marker)) File.Delete(marker); } catch { }
    }

    public static bool Install(string pluginPath) {
        try {
            Directory.CreateDirectory(Path.GetDirectoryName(pluginPath)!);
            File.WriteAllText(pluginPath, ExtensionContent);
            WriteMarker(pluginPath);
            return true;
        } catch {
            return false;
        }
    }

    /// <summary>Removes the plugin + marker. Returns true if the plugin existed.</summary>
    public static bool Remove(string pluginPath) {
        var existed = File.Exists(pluginPath);
        try {
            if (existed) File.Delete(pluginPath);
            DeleteMarker(pluginPath);
        } catch {
            return false;
        }
        return existed;
    }
}
