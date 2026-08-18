namespace Capacitor.Cli.Core.Tests.Unit;

/// <summary>
/// Tests for <see cref="DaemonLockPaths"/> — the name-sanitization rules and
/// the per-name path layout that prevents two daemons under the same name
/// from racing on the lock.
/// </summary>
public class DaemonLockPathsTests {
    [Test]
    [Arguments("alexey", "alexey")]
    [Arguments("ALEXEY", "alexey")]
    [Arguments("My Daemon", "my-daemon")]
    [Arguments("user@host", "user-host")]
    [Arguments("a/b\\c", "a-b-c")]
    [Arguments("  spaced  ", "spaced")]
    [Arguments("dots.are.fine", "dots.are.fine")]
    [Arguments("dashes-ok", "dashes-ok")]
    [Arguments("under_score", "under_score")] // underscores survive: filesystem-safe and common
    [Arguments("collapse---dashes", "collapse-dashes")]
    public async Task Sanitize_NormalisesNames(string input, string expected) {
        await Assert.That(DaemonLockPaths.Sanitize(input)).IsEqualTo(expected);
    }

    [Test]
    [Arguments("")]
    [Arguments("   ")]
    [Arguments("???")]
    [Arguments("///")]
    public async Task Sanitize_FallsBackToDaemonForEmptyOrAllInvalid(string input) {
        await Assert.That(DaemonLockPaths.Sanitize(input)).IsEqualTo("daemon");
    }

    [Test]
    public async Task LockPidStart_ShareSanitizedName() {
        await Assert.That(DaemonLockPaths.LockPath("My Daemon")).EndsWith("my-daemon.lock");
        await Assert.That(DaemonLockPaths.PidPath("My Daemon")).EndsWith("my-daemon.pid");
        await Assert.That(DaemonLockPaths.StartLockPath("My Daemon")).EndsWith("my-daemon.start");
    }

    [Test]
    public async Task Directory_LivesUnderDaemonsFolder() {
        // Verify the PRODUCTION fallback (no KCAP_DAEMONS_DIR set) uses the renamed
        // ~/.config/kcap/daemons/ path, not the legacy agents/ path. Asserted against the pure
        // ResolveDefaultDir(null) rather than by clearing the process-global env var — clearing it
        // would race any parallel test that reads DaemonLockPaths.Directory and briefly re-expose the
        // real daemons dir (Qodo #289 finding 2/3). This is deterministic and touches no shared state.
        await Assert.That(DaemonLockPaths.ResolveDefaultDir(null).Replace('\\', '/'))
            .EndsWith("/.config/kcap/daemons");
    }
}
