using System.Runtime.InteropServices;

namespace Capacitor.Cli;

/// <summary>
/// Interrupt / abandonment safety net for interactive commands.
///
/// Commands such as <c>setup</c>, <c>login</c>, <c>profile</c>, <c>use</c>,
/// <c>import</c>, and <c>uninstall</c> block on synchronous Spectre.Console
/// prompts (<c>ConfirmationPrompt</c>, <c>SelectionPrompt</c>, <c>TextPrompt</c>).
/// A blocking console read has no timeout and observes neither Ctrl+C nor a
/// detached/closed console, so when the controlling terminal or the launching
/// agent goes away mid-prompt the read never returns and the process is
/// orphaned alive — the reported stray <c>kcap.exe</c> processes left behind
/// after a user abandons <c>kcap setup</c> partway through.
///
/// Redirected/piped stdin is already safe: the prompt read throws
/// "Failed to read input in non-interactive mode" and the process exits. This
/// closes the two gaps that piped stdin doesn't cover:
///   1. Ctrl+C / SIGTERM / SIGHUP while blocked in a prompt.
///   2. The launching parent (shell or coding agent) exiting while we wait for
///      input — a detached pseudo-console delivers no signal at all, so a
///      liveness watchdog is the only thing that reaps it.
///
/// This mirrors what <see cref="Commands.WatchCommand"/> already does for the
/// long-lived watcher; every other interactive command previously had no such
/// protection. Idempotent — safe to call once per process.
/// </summary>
static class InteractiveLifetime {
    // Exit code for an interrupted interactive command (128 + SIGINT).
    const int InterruptExitCode = 130;

    // How often the parent-liveness watchdog polls. Short enough to reap a
    // stray promptly, long enough to be negligible overhead.
    static readonly TimeSpan ParentPollInterval = TimeSpan.FromSeconds(3);

    // PosixSignalRegistration.Create returns an IDisposable that owns the
    // underlying handler slot; if it isn't rooted for the process lifetime the
    // finalizer silently unregisters the handler (see WatchCommand). Hold the
    // registrations in static fields so they live until the process exits.
    static IDisposable? _sigterm;
    static IDisposable? _sighup;
    static int          _installed;

    /// <summary>
    /// Commands that drive interactive Spectre.Console prompts and therefore
    /// need the safety net. Kept as an explicit allow-list so non-interactive
    /// commands (hooks, mcp servers) and commands that manage their own
    /// lifetime (<c>watch</c>, <c>daemon</c>) are never affected.
    ///
    /// <c>feedback</c> joins this list for the same reason <c>import</c> does: on a real TTY with
    /// no <c>-m</c> flag it blocks on a plain <see cref="Console.ReadLine"/> loop, which has the
    /// same "no timeout, no signal awareness" gap as a Spectre prompt.
    /// </summary>
    public static bool IsInteractiveCommand(string command) => command is
        "setup" or "login" or "profile" or "use" or "import" or "uninstall" or "feedback";

    /// <summary>
    /// Installs the interrupt handlers and the parent-liveness watchdog. Best
    /// effort throughout: a failure to register any single guard must never
    /// break the command it is meant to protect.
    /// </summary>
    public static void Install() {
        if (Interlocked.Exchange(ref _installed, 1) != 0) {
            return;
        }

        // Ctrl+C: exit deterministically instead of relying on the default
        // terminate action, which a blocking prompt read can swallow.
        try {
            Console.CancelKeyPress += static (_, e) => {
                e.Cancel = true; // we own termination below
                Environment.Exit(InterruptExitCode);
            };
        } catch {
            // Some hosts don't support CancelKeyPress; the watchdog still covers us.
        }

        // SIGTERM / SIGHUP (Unix): a closed terminal or `kill` should reap us.
        // No-ops on platforms where the signal isn't delivered.
        try {
            _sigterm = PosixSignalRegistration.Create(PosixSignal.SIGTERM, static ctx => {
                ctx.Cancel = true;
                Environment.Exit(InterruptExitCode);
            });
        } catch {
            // best effort
        }

        try {
            _sighup = PosixSignalRegistration.Create(PosixSignal.SIGHUP, static ctx => {
                ctx.Cancel = true;
                Environment.Exit(InterruptExitCode);
            });
        } catch {
            // best effort
        }

        // Parent-liveness watchdog. On Windows, closing the console window or
        // force-killing the launching agent delivers no signal we can catch
        // from inside a blocking prompt read, so poll the parent instead: once
        // the process that launched us is gone there is no one left to answer
        // the prompt, so self-terminate rather than orphan.
        try {
            StartParentWatchdog(ProcessHelpers.GetParentPid());
        } catch {
            // best effort
        }
    }

    static void StartParentWatchdog(int? parentPid) {
        // pid <= 1 means no real parent (init / detached) — nothing sane to
        // monitor, so skip rather than risk self-terminating immediately.
        if (parentPid is not { } ppid || ppid <= 1 || !ProcessHelpers.IsProcessAlive(ppid)) {
            return;
        }

        var thread = new Thread(() => {
            while (true) {
                Thread.Sleep(ParentPollInterval);

                if (!ProcessHelpers.IsProcessAlive(ppid)) {
                    Environment.Exit(InterruptExitCode);
                }
            }
        }) {
            IsBackground = true,
            Name         = "kcap-interactive-parent-watchdog"
        };

        thread.Start();
    }
}
