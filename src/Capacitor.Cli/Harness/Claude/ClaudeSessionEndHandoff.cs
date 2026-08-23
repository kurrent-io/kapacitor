using System.Diagnostics;
using System.Text.Json.Nodes;
using Capacitor.Cli.Core;

namespace Capacitor.Cli.Harness.Claude;

/// <summary>
/// Hands the Claude <c>SessionEnd</c> event to a detached continuation of this same hook so the
/// hook itself returns at once.
/// </summary>
/// <remarks>
/// Claude Code runs SessionEnd hooks on shutdown, <c>/clear</c> and resume under a grace it
/// computes from <c>settings.json</c> hook timeouts only — a plugin's <c>hooks.json</c> timeout is
/// matched but never read — so a plugin-sourced hook gets the 1.5 s floor, then is killed. The
/// session-end path (server-URL git probe, spool drain, auth, watcher kill, transcript drain,
/// POST) cannot fit, and killing the watcher before the POST left nothing to end the session.
///
/// <para>The hook therefore re-invokes itself with <see cref="DetachedFlag"/>, pipes the payload
/// to that child's stdin and exits; the child runs the unchanged session-end path under today's
/// 15 s budget, with its output in the session log and its own session so neither Claude's abort
/// of the hook nor a closing terminal reaches it. The spool fallback and the <c>ended_at</c>
/// idempotency stamp ride along untouched because the path itself is untouched.</para>
/// </remarks>
static class ClaudeSessionEndHandoff {
    public const string DetachedFlag = "--detached";

    public static bool IsDetached(string[] args) => args.Contains(DetachedFlag);

    public static bool ShouldHandOff(string[] args, string body) => !IsDetached(args) && IsSessionEnd(body);

    static bool IsSessionEnd(string body) {
        try {
            var name = JsonNode.Parse(body)?["hook_event_name"]?.GetValue<string>();

            return name is not null
                && name.Replace("-", "").Replace("_", "").Equals("sessionend", StringComparison.OrdinalIgnoreCase);
        } catch {
            return false;
        }
    }

    /// <summary>
    /// Starts the detached continuation and feeds it <paramref name="body"/>. False when the
    /// child could not be started — the caller then runs the event inline, as before.
    /// </summary>
    public static bool TrySpawn(string[] args, string body) {
        try {
            var psi = new ProcessStartInfo(Environment.ProcessPath ?? "kcap") {
                RedirectStandardInput  = true,
                RedirectStandardOutput = true,
                RedirectStandardError  = true,
                UseShellExecute        = false,
                CreateNoWindow         = true,
            };

            foreach (var arg in args) psi.ArgumentList.Add(arg);
            psi.ArgumentList.Add(DetachedFlag);

            // Same pipe-leak hazard as the watcher spawn: the child must not hold Claude's hook
            // pipes open, or Claude waits on them past the hook's own exit.
            ProcessHelpers.PreventInheritedHandles();

            var process = WatcherManager.StartProcess(psi);

            if (process is null) {
                Console.Error.WriteLine("[kcap] session-end hand-off: failed to start the detached continuation; running inline");

                return false;
            }

            process.StandardInput.Write(body);
            process.StandardInput.Close();
            process.StandardOutput.Close();
            process.StandardError.Close();

            return true;
        } catch (Exception ex) {
            Console.Error.WriteLine($"[kcap] session-end hand-off failed: {ex.Message}; running inline");

            return false;
        }
    }

    /// <summary>
    /// The continuation's own setup: its std pipes were closed the instant it started, so output
    /// goes to the session log (shared with the watcher, whose lines it interleaves with), and it
    /// leaves the terminal's session so a closing window cannot SIGHUP it mid-drain.
    /// </summary>
    public static void EnterDetached(string body) {
        try {
            string? sessionId = null;
            try { sessionId = JsonNode.Parse(body)?["session_id"]?.GetValue<string>()?.Replace("-", ""); } catch { }

            var logDir = PathHelpers.ConfigPath("logs");
            Directory.CreateDirectory(logDir);
            var logWriter = new StreamWriter(Path.Combine(logDir, $"{sessionId ?? "claude-session-end"}.log"), append: true) { AutoFlush = true };
            Console.SetOut(logWriter);
            Console.SetError(logWriter);
        } catch {
            // Logging is best-effort; the drain and POST matter more.
        }

        ProcessHelpers.DetachFromControllingTerminal();
    }
}
