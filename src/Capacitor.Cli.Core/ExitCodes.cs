namespace Capacitor.Cli.Core;

/// <summary>Daemon process exit codes wrappers/supervisors interpret.</summary>
public static class ExitCodes {
    /// <summary>
    /// Controlled restart-after-update for a supervised daemon. Non-zero so the
    /// failure-restart policy relaunches us (launchd KeepAlive/SuccessfulExit=false,
    /// systemd Restart=on-failure). Distinct from 1 (config error) and 2/3 (name-in-use).
    ///
    /// <para>2/3 are a MANUAL daemon's exit codes for a deliberate refusal — the local name-lock
    /// (2) and the server's <c>NameInUse</c> rejection AT THE INITIAL CONNECT (3) — kept non-zero
    /// so scripts can tell a refusal from a clean exit. A SUPERVISED daemon
    /// (<c>KCAP_DAEMON_SUPERVISED</c> matches its sanitized name) exits 0 for those same two
    /// refusals instead: under <c>KeepAlive SuccessfulExit=false</c>, 2/3 would respin the unit
    /// forever against a name it can never win, and a deliberate refusal isn't a crash. See
    /// <c>DaemonRunner.LockRefusalExit</c> / <c>NameInUseExit</c>.</para>
    ///
    /// <para>A <c>NameInUse</c> discovered MID-RUN (the server contests the slot well after a
    /// successful initial connect — <c>DaemonRunner.cs</c>'s other <c>nameInUse ? 3 : 0</c>) is
    /// unconditionally 3, supervised or not: out of decision-6 scope. The one resulting respawn's
    /// own fresh initial connect is what actually settles the contest — winning it and exiting 0
    /// normally, or losing it and hitting the initial-connect 0/3 path above — so the loop is
    /// bounded either way.</para>
    /// </summary>
    public const int RestartRequested = 10;
}
