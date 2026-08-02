using Capacitor.Cli.Commands;
using Capacitor.Cli.Core;

namespace Capacitor.Cli.Tests.Unit.Daemon;

/// <summary>
/// AI-1645 — <c>daemon stop</c> must refuse to kill a PID that is this very process.
///
/// <para>This is a REGRESSION test for a real kcap-cli CI failure, and it reproduces it exactly
/// rather than approximating it. A live <see cref="DaemonLock"/> writes the acquiring process's own
/// pid and start token, so after acquiring one here, the daemons directory contains a pid file that
/// names the test runner. Every identity check in <c>StopByName</c> then legitimately passes — the
/// process exists, and its start token matches — and the old code proceeded to
/// <c>Process.Kill(entireProcessTree: true)</c> on itself, which .NET refuses with
/// <c>InvalidOperationException: Cannot be used to terminate a process tree containing the calling
/// process</c>.</para>
///
/// <para>The point worth keeping: the pid was never stale or recycled, so no amount of extra
/// validation could have caught it. A daemon simply is never the process running <c>daemon stop</c>,
/// which is why identity is the wrong question and self-reference is the right one.</para>
///
/// <para>In CI this arrived as a random <c>UninstallCommandTests</c> failure — uninstall runs
/// <c>daemon stop --yes</c>, which enumerates the daemons directory, and that directory is chosen by
/// a process-global static that the daemon tests redirect. That parallelism hole is fixed separately
/// by the constraint key on <c>UninstallCommandTests</c>; this test covers the production behaviour
/// on its own, so the guard survives even if the test-isolation fix is later refactored away.</para>
///
/// <para><b>Mutation evidence, stated precisely.</b> Removing BOTH the self-pid guard and the
/// <c>InvalidOperationException</c> catch fails this test with the exact CI error
/// ("Cannot be used to terminate a process tree containing the calling process"). Removing either
/// one ALONE does not — the other still returns a clean exit code. That is deliberate: they are two
/// layers over the same hazard, the second covering the ancestor case that cannot be detected
/// portably in advance. So this test pins the PROPERTY (a self-referencing pid file never escapes as
/// an unhandled exception) rather than either individual guard, and it is worth knowing that is what
/// it does — a future reader deleting one layer will not be told by this test.</para>
/// </summary>
[NotInParallel(nameof(DaemonLockPaths) + ".OverrideDirectoryForTesting")]
public class DaemonStopSelfPidTests {
    [Test]
    public async Task Stop_refuses_to_kill_a_pid_file_naming_the_current_process() {
        var dir = Path.Combine(Path.GetTempPath(), "kcap-stop-self-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        DaemonLockPaths.OverrideDirectoryForTesting(dir);

        try {
            // Write the pid file directly rather than acquiring a real DaemonLock. The first
            // version of this test DID acquire one — and was vacuous: a held lock leaves the pid
            // file unreadable, so StopByName returns 1 down its "PID file unreadable" branch and
            // never reaches the kill at all. It passed with the guard removed, which is the only
            // reason that was caught. What matters is reaching the kill, not how the file got there.
            //
            // The token must be the REAL one for this pid: that is what makes IsOurDaemon return
            // true, which is the whole point. A fabricated token would be rejected earlier and the
            // test would again pass for the wrong reason.
            var token = ProcessStartToken.ForPid(Environment.ProcessId);

            await File.WriteAllTextAsync(
                DaemonLockPaths.PidPath("self"), $"{Environment.ProcessId}\n{token}\n");

            // Before the fix this threw InvalidOperationException out of Process.Kill. The exit code
            // matters less than the fact that it RETURNS: an unhandled exception here takes down
            // whichever unrelated test happens to be running — which is exactly how it showed up in
            // CI, as a random UninstallCommandTests failure.
            var exit = await DaemonCommands.HandleAsync(["daemon", "stop", "--name", "self", "--yes"]);

            await Assert.That(exit).IsEqualTo(1);
        } finally {
            DaemonLockPaths.OverrideDirectoryForTesting(null);

            try { Directory.Delete(dir, recursive: true); } catch { /* best effort */ }
        }
    }
}
