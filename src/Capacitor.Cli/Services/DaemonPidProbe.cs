using System.Diagnostics;
using Capacitor.Cli.Core;

namespace Capacitor.Cli.Services;

/// <summary>
/// PID-file owner, live identity-validated via the daemon's start token
/// (spec §3.4 <c>daemon_pid</c>). Extracted from <c>DaemonCommands</c>'
/// stop path, which used this exact check before killing a PID.
/// </summary>
public static class DaemonPidProbe {
    internal readonly record struct PidEntry(int Pid, string? StartToken);

    /// <summary>
    /// Validated live owner of <paramref name="daemonName"/>, or null when the PID file is
    /// absent/unusable, the PID is dead, or the start token doesn't match. Same semantics
    /// <c>daemon stop</c> uses before killing.
    /// </summary>
    public static int? ValidatedPid(string daemonName) =>
        ReadPidFile(daemonName) is { } entry && IsOurDaemon(entry.Pid, entry.StartToken)
            ? entry.Pid
            : null;

    internal static PidEntry? ReadPidFile(string daemonName) {
        var pidPath = DaemonLockPaths.PidPath(daemonName);

        if (!File.Exists(pidPath)) return null;

        var lines = File.ReadAllText(pidPath)
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        if (lines.Length == 0 || !int.TryParse(lines[0], out var pid)) {
            // report "no usable PID" but do NOT delete the file. The
            // daemon writes it with File.WriteAllText (truncate+write) under the
            // flock, so a SIGKILL/native abort mid-write can leave a present but
            // empty/partial/unparseable file — which is itself a hard-death
            // breadcrumb that DaemonLock.InspectPriorHolder reports as
            // (unclean, null). Since callers here (status, the pre-spawn guards)
            // read this before the successor daemon runs, unlinking a corrupt
            // file would erase that breadcrumb. Cleanup of corrupt/stale files is
            // left to the explicit paths (`daemon stop`, `daemon doctor --clean`).
            return null;
        }

        var startToken = lines.Length > 1 ? lines[1] : null;

        return new PidEntry(pid, startToken);
    }

    /// <summary>
    /// Verify that a PID belongs to our daemon. The strong check is start-token
    /// equality — PIDs get recycled, but a recycled process won't share the
    /// same kernel start instant (<see cref="ProcessStartToken"/>). A
    /// same-scheme token mismatch is conclusive (a different incarnation), so we
    /// return false rather than fall back to the weaker name check, which can't
    /// tell two of our own daemons apart.
    ///
    /// The name fallback applies only when the token can't be compared at all:
    /// no token recorded, the live token is unreadable, or the recorded token is
    /// a legacy/foreign scheme — notably a PID file that stored bare
    /// <c>Process.StartTime</c> ticks. Falling back there keeps a still-running
    /// old daemon manageable across an upgrade instead of stranding it.
    /// </summary>
    internal static bool IsOurDaemon(int pid, string? expectedStartToken) {
        try {
            using var process = Process.GetProcessById(pid);

            if (expectedStartToken is not null && ProcessStartToken.Matches(pid, expectedStartToken) is { } matched)
                return matched;

            // No token, unreadable, or a legacy/foreign scheme we can't compare:
            // best-effort match by process image name.
            var daemonPath = UnitIdentity.ResolveDaemonBinary();

            var ourName = daemonPath is not null
                ? Path.GetFileNameWithoutExtension(daemonPath)
                : "kcap-daemon";

            return string.Equals(process.ProcessName, ourName, StringComparison.OrdinalIgnoreCase);
        } catch (ArgumentException) {
            return false; // process doesn't exist
        }
    }
}
