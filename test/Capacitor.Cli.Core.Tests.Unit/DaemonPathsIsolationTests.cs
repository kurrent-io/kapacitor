namespace Capacitor.Cli.Core.Tests.Unit;

/// <summary>
/// Regression guard: a CLI unit-test run must NEVER be able to resolve the developer's REAL
/// <c>~/.config/kcap/daemons/</c> directory.
///
/// <para><see cref="DaemonLockPaths"/> deliberately ignores <c>KCAP_CONFIG_DIR</c>, and the default
/// daemon name is the OS username. Before this guard, a daemon test that read
/// <see cref="DaemonLockPaths.Directory"/> while the process-wide override was <c>null</c> — a
/// teardown reset, or a test that never set it — resolved the live launchd daemon's PID file
/// (<c>~/.config/kcap/daemons/{username}.pid</c>) and <c>Process.Kill(entireProcessTree: true)</c>'d
/// it, SIGKILLing the developer's running daemon and its hosted agents. <c>DaemonPathsGlobalSetup</c>
/// pins the assembly's default daemons dir to a temp path (via <c>KCAP_DAEMONS_DIR</c>) so the real
/// directory is unreachable no matter which test runs, in what order, or in parallel.</para>
/// </summary>
[NotInParallel(nameof(DaemonLockPaths) + ".OverrideDirectoryForTesting")]
public class DaemonPathsIsolationTests {
    static string RealHomeDaemonsDir => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".config", "kcap", "daemons");

    [Test]
    public async Task Directory_WithNoOverride_IsNotTheRealHomeDaemonsDir() {
        // Reproduce the dangerous state a daemon test's teardown leaves behind.
        DaemonLockPaths.OverrideDirectoryForTesting(null);

        await Assert.That(DaemonLockPaths.Directory).IsNotEqualTo(RealHomeDaemonsDir);
    }

    [Test]
    public async Task PidPath_ForUsernameDefaultName_IsNotUnderRealHomeDir() {
        // The exact path the offending test read to find (and kill) the live daemon.
        DaemonLockPaths.OverrideDirectoryForTesting(null);

        var pidPath = DaemonLockPaths.PidPath(Environment.UserName);

        await Assert.That(pidPath.StartsWith(RealHomeDaemonsDir, StringComparison.Ordinal)).IsFalse();
    }
}
