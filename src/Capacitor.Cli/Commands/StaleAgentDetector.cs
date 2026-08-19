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
/// The trigger is a FIRST install, not a timestamp. An install rewrites its own extension file, so
/// dating staleness from that file's mtime would tell a months-old, perfectly recorded session that it
/// is not being recorded every time the user re-ran the installer — and the npm postinstall re-runs it
/// on every upgrade. On a first install the question needs no clock: nothing running can have loaded
/// an integration that did not exist.
///
/// We state the fact and locate the process rather than offering to fix it. The restart decomposes
/// into kill and relaunch and only the destructive half is ours: their session is interactive on a
/// terminal we do not own, so killing it discards a conversation nothing can bring back.
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
