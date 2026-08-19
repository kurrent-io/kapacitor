namespace Capacitor.Cli.Commands;

/// <summary>An agent session that was already running when kcap's integration was first installed.</summary>
public sealed record StaleAgentProcess(string Vendor, int Pid, string? Cwd);

/// <summary>A vendor whose running sessions predate a first install, and what its process is called.</summary>
public sealed record StaleAgentTarget(string Vendor, string ProcessName);

/// <summary>
/// Finds agent sessions that were already running when the integration first arrived, so the one case
/// worth mentioning can be mentioned and every other case stays silent.
/// </summary>
/// <remarks>
/// Keyed on a first install rather than a clock: an install rewrites its own extension, so dating
/// staleness from that file would tell a long-installed, perfectly captured session it is not
/// captured — on every re-run, and on every npm upgrade.
///
/// Locates the session but offers no restart: only the destructive half of one is ours to do, on a
/// terminal we do not own.
/// </remarks>
public static class StaleAgentDetector {
    public static IReadOnlyList<StaleAgentProcess> Find(
            IEnumerable<StaleAgentTarget> targets,
            Func<string, IEnumerable<int>> running,
            Func<int, string?>             cwdOf) =>
        [.. targets.SelectMany(t => running(t.ProcessName)
                                        .Select(pid => new StaleAgentProcess(t.Vendor, pid, cwdOf(pid))))];

    /// <summary>
    /// One line per session, naming where it is so the user can find the right window, and pointing at
    /// the remedy that does exist — the transcript is on disk either way, so the session is
    /// recoverable by import even though the live capture is not.
    /// </summary>
    public static IEnumerable<string> Describe(IEnumerable<StaleAgentProcess> stale) =>
        stale.Select(s => {
            var where = s.Cwd is { Length: > 0 } cwd ? $" in {cwd}" : "";

            return $"A {s.Vendor} session was already running{where} (pid {s.Pid}) — it won't be captured "
                 + $"live, but `kcap import --{s.Vendor}` will backfill it once it ends. "
                 + "Anything you start from now on is captured as it happens.";
        });
}
