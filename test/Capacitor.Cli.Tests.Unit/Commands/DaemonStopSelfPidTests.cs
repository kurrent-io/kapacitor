using Capacitor.Cli.Commands;
using Capacitor.Cli.Core;
using Capacitor.Cli.Core.Setup;

namespace Capacitor.Cli.Tests.Unit.Commands;

/// <summary>
/// <c>daemon stop</c> must refuse to kill a PID that is this very process.
///
/// <para>This is a REGRESSION test for a real kcap-cli CI failure, and it reproduces it exactly
/// rather than approximating it. A live <see cref="Capacitor.Cli.Daemon.DaemonLock"/> writes the acquiring process's own
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
/// <c>daemon stop --yes</c>, which enumerates the daemons directory, and that suite was reaching the
/// runner's own. Isolating the directory is what closes that hole; this test covers the production
/// behaviour on its own, so the guard survives even if the isolation is later refactored away.</para>
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
public class DaemonStopSelfPidTests {
    [TempDaemonPaths]  public required TempDaemonStore Daemons { get; init; }
    [TempConfigRoot]   public required TempConfigRoot  Config  { get; init; }
    [TempHome]         public required TempHome        Home    { get; init; }

    [Test]
    public async Task Stop_refuses_to_kill_a_pid_file_naming_the_current_process() {
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
            Daemons.Store.PidPath("self"), $"{Environment.ProcessId}\n{token}\n");

        // Before the fix this threw InvalidOperationException out of Process.Kill. The exit code
        // matters less than the fact that it RETURNS: an unhandled exception here takes down
        // whichever unrelated test happens to be running — which is exactly how it showed up in
        // CI, as a random UninstallCommandTests failure.
        var exit = await new DaemonCommands(
                Daemons.Store, Config.Root, Resolutions.None(Config.Root), Home,
                TestHarnesses.All(), BinaryProbe.Searching(null))
            .HandleAsync(["daemon", "stop", "--name", "self", "--yes"]);

        await Assert.That(exit).IsEqualTo(1);
    }
}
