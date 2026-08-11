using System.Diagnostics;

namespace Capacitor.Cli.Services;

/// <summary>
/// Raw-kill helper for the <c>install --replace</c> ownership matrix (spec §3.4): terminate a
/// VALIDATED owner of a daemon name with none of <c>DaemonCommands.StopByName</c>'s guard rails —
/// no console I/O, no "is this managed by a service?" check (that public guard would no-op exactly
/// in the takeover case <c>--replace</c> exists for), and no lock acquisition (the calling
/// transaction already holds the service flock). The kill mechanics themselves mirror
/// <c>StopByName</c>'s exception handling exactly, so the two never drift apart on what counts as
/// "already gone" vs. a real failure.
/// </summary>
static class DaemonKill {
    /// <summary>
    /// Kill <paramref name="validatedPid"/> (already identity-validated by the caller — this makes
    /// no ownership check of its own) and wait up to <paramref name="wait"/> for it to exit. Returns
    /// whether the name is gone afterward, re-checked via <see cref="DaemonPidProbe.ValidatedPid"/>
    /// rather than assumed from the kill call succeeding.
    /// </summary>
    public static bool KillValidatedOwner(string daemonName, int validatedPid, TimeSpan wait) {
        try {
            var process = Process.GetProcessById(validatedPid);

            try {
                process.Kill(entireProcessTree: true);
            } catch (InvalidOperationException) when (process.HasExited) {
                // Benign race: it exited between GetProcessById and Kill — the outcome we wanted.
                return true;
            } catch (InvalidOperationException) {
                // .NET's safety refusal: the tree contains the calling process (self or an
                // ancestor). Never expected in production — a daemon is never the process running
                // `install --replace` — but if it ever happens, do not report a kill that didn't.
                return false;
            }

            try { process.WaitForExit(wait); } catch { /* best-effort */ }
        } catch (ArgumentException) {
            return true; // already dead before we even got here
        }

        return DaemonPidProbe.ValidatedPid(daemonName) is not { } stillPid || stillPid != validatedPid;
    }
}
