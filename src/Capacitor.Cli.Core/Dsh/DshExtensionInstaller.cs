namespace Capacitor.Cli.Core.Dsh;

/// <summary>
/// Installs / removes kcap's live-ingest plugin for DeepSeek Harness (dsh), AI-2020.
/// dsh is a Cordis-based agent whose session module declares "persistence is a plugin
/// concern" — so kcap ships a dependency-free Cordis persistence plugin that forwards
/// every appended <c>SessionEvent</c> to <c>~/.cache/kcap/dsh/{id}.jsonl</c>, writes the
/// durable header on <c>session/created</c> and a terminal marker on <c>session/disposed</c>,
/// and spawns <c>kcap hook --dsh --event session-start</c> so the watcher tails that file
/// (vendor=dsh). This mirrors the OpenCode plugin; the watcher owns session-end.
///
/// <para><b>Install</b> = copy <see cref="ExtensionContent"/> to <see cref="DshPaths.KcapPlugin"/>
/// (<c>$DSH_HOME/kcap-dsh.plugin.mjs</c>) and add an entry to dsh's Cordis config
/// (<c>cordis.yml</c> / the active profile): <c>- name: './kcap-dsh.plugin.mjs'</c>. The
/// copy + version-marker mechanics below are the automatable part; registering the entry in
/// dsh's profile/patch config is left to <c>dsh plugin</c> / a manual one-line edit (see the
/// plugin comment) because that format is dsh-profile-specific.</para>
///
/// <para><see cref="ExtensionContent"/> is embedded as a const (no manifest-resource
/// reflection) to stay NativeAOT-safe, mirroring <see cref="OpenCode.OpenCodeExtensionInstaller"/>.</para>
/// </summary>
public static class DshExtensionInstaller {
    public const string MarkerFileName = ".kcap-extension-version";

    /// <summary>
    /// The kcap dsh Cordis persistence plugin (plain-JS build, for dsh's <c>--patch install</c>).
    /// Dependency-free (only <c>node:</c> builtins) and fail-open — a kcap/server problem must
    /// never disrupt the dsh session. Kept byte-for-byte in sync with the source at
    /// <c>deepseek-harness/kcap-dsh.mts</c>.
    /// </summary>
    public const string ExtensionContent =
        """
        // kcap observer plugin for dsh (plain-JS build for --patch install). Fail-open.
        import { appendFileSync, mkdirSync } from 'node:fs'
        import { join } from 'node:path'
        import { homedir } from 'node:os'
        import { spawn } from 'node:child_process'
        export const name = 'kcap'
        export function apply(ctx) {
          const dir = join(homedir(), '.cache', 'kcap', 'dsh')
          try { mkdirSync(dir, { recursive: true }) } catch {}
          const fileFor = id => join(dir, `${id}.jsonl`)
          const write = (id, rec) => { try { appendFileSync(fileFor(id), JSON.stringify(rec) + '\n') } catch {} }
          const ensureWatcher = id => {
            try {
              const c = spawn('kcap', ['hook','--dsh','--event','session-start','--session',id,'--file',fileFor(id)], { stdio: 'ignore', detached: true })
              c.on('error', () => {}); c.unref()
            } catch {}
          }
          ctx.on('session/created', s => { ensureWatcher(s.id); write(s.id, { $kcap: 'header', ...s.header }) })
          ctx.on('session/event', (s, e) => write(s.id, e))
          ctx.on('session/disposed', s => write(s.id, { $kcap: 'disposed', id: s.id }))
        }
        export default apply
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
