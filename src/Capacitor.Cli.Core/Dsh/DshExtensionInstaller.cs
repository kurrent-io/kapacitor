namespace Capacitor.Cli.Core.Dsh;

/// <summary>
/// Installs / removes kcap's live-ingest plugin for DeepSeek Harness (dsh).
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
/// reflection) to stay NativeAOT-safe, mirroring <see cref="Harness.OpenCode.OpenCodeExtensionInstaller"/>.</para>
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
          const runHook = (id, event, extra = []) => {
            try {
              const c = spawn('kcap', ['hook','--dsh','--event',event,'--session',id,'--file',fileFor(id), ...extra], { stdio: 'ignore', detached: true })
              c.on('error', () => {}); c.unref()
            } catch {}
          }
          const hookArgs = (h = {}) => {
            // dsh's session header carries cwd (+ id/createdAt/agentPreset) but NOT model/provider;
            // its `version` is the schema version, not an app version — so forward only cwd.
            const cwd = h.cwd || process.cwd()
            return cwd ? ['--cwd', cwd] : []
          }
          ctx.on('session/created', s => { write(s.id, { $kcap: 'header', ...s.header }); runHook(s.id, 'session-start', hookArgs(s.header)) })
          ctx.on('session/event', (s, e) => write(s.id, e))
          ctx.on('session/disposed', s => { write(s.id, { $kcap: 'disposed', id: s.id }); runHook(s.id, 'session-end', ['--reason','disposed', ...hookArgs(s.header)]) })
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

    // ── Cordis profile registration ──────────────────────────────────────────
    // dsh loads plugins from the active profile's live-watched cordis.patch.yml (a top-level YAML
    // array of patch entries). We register the observer plugin via an idempotent, marker-delimited
    // managed block so `install` can update it and `remove` can strip it without touching the user's
    // own entries. Only the dependency-free `file://` observer plugin is registered here; the MCP
    // entries (which depend on the dsh-mcp-client bundle and could fail-loud) stay documented.

    const string CordisBeginMarker = "# --- kcap-dsh:begin (kcap plugin install --dsh) — do not edit inside ---";
    const string CordisEndMarker   = "# --- kcap-dsh:end ---";

    /// <summary>The managed cordis.patch.yml block that registers the observer plugin.</summary>
    public static string BuildCordisBlock(string pluginPath) {
        var uri = new Uri(pluginPath).AbsoluteUri;   // file:///C:/... on Windows, file:///home/... on Unix
        return CordisBeginMarker + "\n"
             + "- insert:\n"
             + "    - id: kcap\n"
             + $"      name: '{uri}'\n"
             + CordisEndMarker;
    }

    /// <summary>Idempotently writes the managed block into a profile's cordis.patch.yml. Preserves
    /// the user's own array entries; replaces any prior managed block. Returns true on success.</summary>
    public static bool RegisterInCordisPatch(string cordisPatchPath, string pluginPath) {
        try {
            var existing = File.Exists(cordisPatchPath) ? File.ReadAllText(cordisPatchPath) : "";
            var stripped = StripManagedBlock(existing);
            var block    = BuildCordisBlock(pluginPath);

            var lines = stripped.Replace("\r\n", "\n").Split('\n');
            string result;
            if (HasRealEntries(lines)) {
                // Append after the user's own block-style entries.
                result = stripped.TrimEnd() + "\n" + block + "\n";
            } else {
                // Base array is empty (comments / whitespace / a lone `[]` flow literal). A block-style
                // array can't follow a `[]` in one document, so drop that literal but keep any comments.
                var comments = string.Join("\n", lines.Where(l => l.Trim() != "[]")).TrimEnd();
                result = (comments.Length == 0 ? block : comments + "\n" + block) + "\n";
            }

            Directory.CreateDirectory(Path.GetDirectoryName(cordisPatchPath)!);
            File.WriteAllText(cordisPatchPath, result);
            return true;
        } catch {
            return false;
        }
    }

    /// <summary>Strips the managed block; restores an empty array (<c>[]</c>) if no real entries remain
    /// (keeping any comments). Returns true if the file existed.</summary>
    public static bool UnregisterFromCordisPatch(string cordisPatchPath) {
        try {
            if (!File.Exists(cordisPatchPath)) return false;
            var stripped = StripManagedBlock(File.ReadAllText(cordisPatchPath));
            var lines = stripped.Replace("\r\n", "\n").Split('\n');
            string result;
            if (HasRealEntries(lines)) {
                result = stripped.TrimEnd() + "\n";
            } else {
                var comments = string.Join("\n", lines.Where(l => l.Trim() != "[]")).TrimEnd();
                result = (comments.Length == 0 ? "[]" : comments + "\n[]") + "\n";
            }
            File.WriteAllText(cordisPatchPath, result);
            return true;
        } catch {
            return false;
        }
    }

    /// <summary>True if the lines contain a real YAML array entry (not just comments, whitespace,
    /// or a lone <c>[]</c> flow literal).</summary>
    static bool HasRealEntries(IEnumerable<string> lines) =>
        lines.Any(l => { var t = l.Trim(); return t.Length > 0 && t != "[]" && !t.StartsWith('#'); });

    public static bool IsRegisteredInCordisPatch(string cordisPatchPath) {
        try { return File.Exists(cordisPatchPath) && File.ReadAllText(cordisPatchPath).Contains("kcap-dsh:begin"); }
        catch { return false; }
    }

    static string StripManagedBlock(string content) {
        var sb = new System.Text.StringBuilder();
        var inBlock = false;
        foreach (var line in content.Replace("\r\n", "\n").Split('\n')) {
            if (!inBlock && line.Contains("kcap-dsh:begin")) { inBlock = true; continue; }
            if (inBlock) { if (line.Contains("kcap-dsh:end")) inBlock = false; continue; }
            sb.Append(line).Append('\n');
        }
        return sb.ToString();
    }
}
