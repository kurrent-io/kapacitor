namespace Capacitor.Cli.Core.Harness.Pi;

/// <summary>
/// Installs / removes kcap's live-ingest extension for Pi. Pi has no shell
/// hooks, so instead of writing a hooks.json (Copilot/Cursor) kcap ships a
/// TypeScript extension file (<c>~/.pi/agent/extensions/kcap.ts</c>) that Pi
/// auto-discovers and loads in-process. The extension shells out to
/// <c>kcap hook --pi</c> on <c>session_start</c>/<c>session_shutdown</c>.
///
/// <para><see cref="ExtensionContent"/> is the single source of truth for the
/// installed file (embedding it as a const keeps NativeAOT happy — no manifest
/// resource reflection). A version marker beside it gates the upgrade-time
/// refresh, mirroring <c>CopilotHooksInstaller</c>.</para>
/// </summary>
public static class PiExtensionInstaller {
    public const string MarkerFileName = ".kcap-extension-version";

    /// <summary>
    /// The kcap Pi extension. Untyped (<c>pi: any</c>) so it carries no runtime
    /// dependency on the <c>@earendil-works/pi-coding-agent</c> types, and
    /// fail-safe so a kcap/server hiccup never disrupts the pi session.
    /// </summary>
    public const string ExtensionContent =
        """
        // kcap.ts — Kurrent Capacitor live-ingest extension for Pi (badlogic/pi-mono).
        //
        // Installed by `kcap plugin install --pi` into ~/.pi/agent/extensions/kcap.ts.
        // Pi has no shell hooks, so this extension bridges Pi's in-process lifecycle
        // events to the kcap CLI: on session start/shutdown it invokes
        // `kcap hook --pi`, which POSTs /hooks/session-{start,end}/pi and runs the
        // transcript watcher (vendor=pi). On session-start, kcap's stdout is a DATA
        // channel carrying the team-memory index fragment, which this extension
        // appends to each turn's system prompt (before_agent_start). Safe-by-default —
        // every handler swallows errors so a kcap or server hiccup never disrupts the
        // pi session.

        export default function (pi: any) {
          // Hosted launches set KCAP_PI_PURE=1: the daemon owns capture there, and this
          // extension standing down is what prevents the session being recorded twice.
          // Mirrors OPENCODE_PURE.
          if (typeof process !== "undefined" && process?.env?.KCAP_PI_PURE === "1") return;

          // Team-memory fragment for the CURRENT session file, or null. Keyed by file
          // because that is Pi's stable session identity: resume reuses the file,
          // fork/switch mint a different one (which must never inherit this fragment).
          let memFile: string | null = null;
          let memFragment: string | null = null;

          // Only stdout that opens with the marker is a memory fragment. Anything else
          // (a future diagnostic, an error string) must not reach the system prompt.
          const MEMORY_MARKER = "<!-- kcap-memory-index:v1 -->";

          async function notify(event: string, ctx: any, reason?: string): Promise<any> {
            try {
              const file = ctx?.sessionManager?.getSessionFile?.();
              if (!file) return null; // ephemeral (--no-session): nothing to record
              const args = ["hook", "--pi", "--event", event, "--file", String(file)];
              if (ctx?.cwd) args.push("--cwd", String(ctx.cwd));
              if (reason) args.push("--reason", String(reason));
              // --memory-contract declares that THIS extension captures and delivers
              // the command's stdout; an older kcap ignores unknown args (fail-open).
              if (event === "session-start") args.push("--memory-contract", "1");
              // kcap spawns a detached watcher and returns fast — except on session-start, where it
              // also blocks briefly (bounded to a ~3.5s hook budget) awaiting the memory-index fetch
              // before returning. Either way this exec timeout sits well outside that ceiling, so a
              // hung kcap can never stall pi's startup or shutdown.
              const res = await pi.exec("kcap", args, { timeout: 10000 });
              return { file: String(file), stdout: res?.stdout ?? "" };
            } catch {
              return null; // never disrupt the pi session
            }
          }

          pi.on("session_start", async (event: any, ctx: any) => {
            const res = await notify("session-start", ctx, event?.reason);
            if (!res) return;
            if (res.file !== memFile) {
              // A different session file never inherits the previous fragment.
              memFile = res.file;
              memFragment = null;
            }
            const out = String(res.stdout ?? "").trim();
            // A later empty result never erases a cached ready fragment (repeat
            // session_start with a spent lease legitimately returns nothing).
            if (out.startsWith(MEMORY_MARKER)) memFragment = out;
          });

          pi.on("before_agent_start", async (_event: any, ctx: any) => {
            try {
              if (!memFragment) return;
              const current = ctx?.sessionManager?.getSessionFile?.();
              if (!current || String(current) !== memFile) return;
              // Chained system prompt is rebuilt fresh each turn; append exactly once
              // per turn (the includes() guard is belt-and-braces against re-entry).
              if (_event?.systemPrompt && !String(_event.systemPrompt).includes(memFragment)) {
                return { systemPrompt: _event.systemPrompt + "\n\n" + memFragment };
              }
            } catch {
              // never disrupt the pi session
            }
          });

          pi.on("session_shutdown", async (event: any, ctx: any) => {
            memFile = null;
            memFragment = null;
            await notify("session-end", ctx, event?.reason);
          });
        }
        """;

    /// <summary>
    /// True when kcap.ts (or its marker) is present. Marker covers the case
    /// where a user deleted kcap.ts but kept the dir.
    /// </summary>
    public static bool IsInstalled(string extensionPath) {
        if (File.Exists(extensionPath)) return true;
        var dir = Path.GetDirectoryName(extensionPath);
        return dir is not null && File.Exists(Path.Combine(dir, MarkerFileName));
    }

    public static string? ReadMarker(string extensionPath) {
        var dir = Path.GetDirectoryName(extensionPath);
        if (string.IsNullOrEmpty(dir)) return null;
        var marker = Path.Combine(dir, MarkerFileName);
        try { return File.Exists(marker) ? File.ReadAllText(marker).Trim() : null; }
        catch { return null; }
    }

    public static void WriteMarker(string extensionPath) {
        var dir = Path.GetDirectoryName(extensionPath);
        if (string.IsNullOrEmpty(dir)) return;
        try {
            Directory.CreateDirectory(dir);
            File.WriteAllText(Path.Combine(dir, MarkerFileName), CapacitorVersion.Current());
        } catch { /* best effort */ }
    }

    public static void DeleteMarker(string extensionPath) {
        var dir = Path.GetDirectoryName(extensionPath);
        if (string.IsNullOrEmpty(dir)) return;
        var marker = Path.Combine(dir, MarkerFileName);
        try { if (File.Exists(marker)) File.Delete(marker); } catch { }
    }

    public static bool Install(string extensionPath) {
        try {
            Directory.CreateDirectory(Path.GetDirectoryName(extensionPath)!);
            File.WriteAllText(extensionPath, ExtensionContent);
            WriteMarker(extensionPath);
            return true;
        } catch {
            return false;
        }
    }

    /// <summary>Removes kcap.ts + marker. Returns true if kcap.ts existed.</summary>
    public static bool Remove(string extensionPath) {
        var existed = File.Exists(extensionPath);
        try {
            if (existed) File.Delete(extensionPath);
            DeleteMarker(extensionPath);
        } catch {
            return false;
        }
        return existed;
    }
}
