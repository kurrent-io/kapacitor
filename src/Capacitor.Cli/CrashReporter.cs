using System.Text;
using Capacitor.Cli.Core;

namespace Capacitor.Cli;

/// <summary>
/// last-resort guard for the CLI's top-level command dispatch. A `kcap`
/// invocation — especially a hook or a detached generator the coding agent
/// spawns — that lets an exception escape its command handler would otherwise
/// reach the NativeAOT runtime, which <b>aborts the process (SIGABRT) and writes
/// a macOS crash report</b>. That was happening dozens of times a day in the
/// wild (the same class as a prior fix that patched two specific <c>Process.Start</c>
/// sites but not the structural gap). This records the exception to a rolling
/// crash log and maps the command to a fail-open exit, so the abort class
/// disappears <i>and</i> the exact exception is captured for follow-up.
/// </summary>
internal static class CrashReporter {
    // Commands the coding agent spawns as hooks or detached side-processes, where a
    // crash must never surface (no error banner, no crash report) — mirrors the
    // hook handler's own fail-open behaviour. Internal (not private): also consumed
    // directly by UpdateNotice.IsHumanFacing — the same population never gets the
    // exit-time update notice either, since nobody reads their stderr.
    internal static readonly HashSet<string> FailOpenCommands = new(StringComparer.Ordinal) {
        "hook", "generate-whats-done", "set-title", "copilot-finalize", "report-version",
    };

    /// <summary>True for agent-spawned commands that must fail open (exit 0) on a crash.</summary>
    public static bool IsFailOpenCommand(string? command) =>
        command is not null && FailOpenCommands.Contains(command);

    /// <summary>0 for fail-open commands (crash stays invisible), 1 otherwise.</summary>
    public static int ExitCode(string? command) => IsFailOpenCommand(command) ? 0 : 1;

    /// <summary>
    /// Deterministic crash-log entry. Pure — the timestamp is injected so it can be
    /// unit-tested. <paramref name="ex"/>.ToString() carries the type, message, and
    /// stack (when the exception was thrown).
    /// </summary>
    public static string FormatEntry(string? command, Exception ex, DateTimeOffset now) {
        var sb = new StringBuilder();
        sb.Append(now.ToUniversalTime().ToString("o"));
        sb.Append("  command=");
        sb.Append(string.IsNullOrEmpty(command) ? "?" : command);
        sb.Append('\n');
        sb.Append(ex);
        sb.Append("\n---\n");

        return sb.ToString();
    }

    /// <summary>
    /// Best-effort: append the crash to <c>~/.config/kcap/crash.log</c> (size-capped)
    /// and write a single stderr line. Never throws — it runs while the process is
    /// already failing, possibly with a closed stderr pipe (detached process).
    /// </summary>
    public static void Record(ConfigRoot config, string? command, Exception ex) {
        string? writtenPath = null;
        try {
            var path = config.Path("crash.log");
            // Ensure the config dir exists — on a fresh install it may not yet, and
            // ConfigRoot.Path only combines paths (doesn't create them).
            var dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            TrimIfLarge(path);
            File.AppendAllText(path, FormatEntry(command, ex, DateTimeOffset.UtcNow));
            writtenPath = path;
        } catch {
            // Disk full, permissions — nothing useful to do while crashing.
        }

        try {
            // Report the actual resolved path (honours KCAP_CONFIG_DIR) and only
            // claim it was logged when the write actually succeeded.
            var where = writtenPath is null ? "crash log unavailable" : $"logged to {writtenPath}";
            Console.Error.WriteLine(
                $"[kcap] {(string.IsNullOrEmpty(command) ? "command" : command)} failed: "
              + $"{ex.GetType().Name}: {ex.Message} ({where})");
        } catch {
            // stderr may be a broken pipe on a detached process.
        }
    }

    // Keep the crash log bounded: once it passes ~256 KB, drop it (the fresh entry
    // is appended right after). Best-effort.
    static void TrimIfLarge(string path) {
        try {
            var fi = new FileInfo(path);
            if (fi.Exists && fi.Length > 256 * 1024) File.Delete(path);
        } catch {
            // best-effort
        }
    }
}
