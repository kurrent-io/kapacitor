namespace Capacitor.Cli.Core;

/// <summary>Daemon process exit codes wrappers/supervisors interpret.</summary>
public static class ExitCodes {
    /// <summary>
    /// Controlled restart-after-update for a supervised daemon. Non-zero so the
    /// failure-restart policy relaunches us (launchd KeepAlive/SuccessfulExit=false,
    /// systemd Restart=on-failure). Distinct from 1 (config error) and 2/3 (name-in-use).
    ///
    /// <para>2/3 are a MANUAL daemon's exit codes for a deliberate refusal — the local name-lock
    /// (2) and the server's <c>NameInUse</c> rejection (3) — kept non-zero so scripts can tell a
    /// refusal from a clean exit. A SUPERVISED daemon (<c>KCAP_DAEMON_SUPERVISED</c> matches its
    /// sanitized name) exits 0 for the same two refusals instead: under <c>KeepAlive
    /// SuccessfulExit=false</c>, 2/3 would respin the unit forever against a name it can never win,
    /// and a deliberate refusal isn't a crash. See <c>DaemonRunner.LockRefusalExit</c> /
    /// <c>NameInUseExit</c>.</para>
    /// </summary>
    public const int RestartRequested = 10;
}
