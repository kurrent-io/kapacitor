namespace Capacitor.Cli.Core.Tests.Unit;

/// <summary>
/// Tests for <see cref="DaemonStore"/> — the name-sanitization rule and the per-name path layout
/// that prevents two daemons under the same name from racing on the lock.
/// </summary>
public class DaemonStoreTests {
    [Test]
    [Arguments("laptop", "laptop")]
    [Arguments("LAPTOP", "laptop")]
    [Arguments("My Daemon", "my-daemon")]
    [Arguments("user@host", "user-host")]
    [Arguments("a/b\\c", "a-b-c")]
    [Arguments("  spaced  ", "spaced")]
    [Arguments("dots.are.fine", "dots.are.fine")]
    [Arguments("dashes-ok", "dashes-ok")]
    [Arguments("under_score", "under_score")] // underscores survive: filesystem-safe and common
    [Arguments("collapse---dashes", "collapse-dashes")]
    public async Task Sanitize_NormalisesNames(string input, string expected) {
        await Assert.That(DaemonStore.Sanitize(input)).IsEqualTo(expected);
    }

    [Test]
    [Arguments("")]
    [Arguments("   ")]
    [Arguments("???")]
    [Arguments("///")]
    public async Task Sanitize_FallsBackToDaemonForEmptyOrAllInvalid(string input) {
        await Assert.That(DaemonStore.Sanitize(input)).IsEqualTo("daemon");
    }

    /// <summary>Callers hand on a name that has already been through here — the service id is derived
    /// from it independently — so a second pass has to be a no-op.</summary>
    [Test]
    public async Task Sanitize_IsIdempotent() {
        var once = DaemonStore.Sanitize("a/b c");

        await Assert.That(DaemonStore.Sanitize(once)).IsEqualTo(once);
    }

    [Test]
    public async Task EveryPer_name_path_shares_the_sanitized_name() {
        const string root = "/daemons";

        var paths = new DaemonStore(root);

        await Assert.That(paths.LockPath("My Daemon")).IsEqualTo(Under("my-daemon.lock"));
        await Assert.That(paths.PidPath("My Daemon")).IsEqualTo(Under("my-daemon.pid"));
        await Assert.That(paths.StartLockPath("My Daemon")).IsEqualTo(Under("my-daemon.start"));
        await Assert.That(paths.RestartPendingPath("My Daemon")).IsEqualTo(Under("my-daemon.restart-pending"));
        await Assert.That(paths.VersionPath("My Daemon")).IsEqualTo(Under("my-daemon.version"));
        await Assert.That(paths.SocketPath("My Daemon")).IsEqualTo(Under("my-daemon.sock"));
        await Assert.That(paths.ConsentLogPath("My Daemon")).IsEqualTo(Under("my-daemon", "consent-decisions.jsonl"));

        return;

        static string Under(params ReadOnlySpan<string> parts) => Path.Combine([root, .. parts]);
    }

    [Test]
    public async Task Default_directory_lives_under_the_daemons_folder() {
        // The PRODUCTION fallback, with no KCAP_DAEMONS_DIR set: the ~/.config/kcap/daemons/ path,
        // not the legacy agents/ one. Asserted against the pure resolver rather than by clearing the
        // env var, which would be a process-global mutation racing every parallel test.
        await Assert.That(DaemonStore.ResolveDefaultDir(null).Replace('\\', '/'))
            .EndsWith("/.config/kcap/daemons");
    }

    [Test]
    public async Task Environment_value_wins_over_the_home_fallback() {
        await Assert.That(DaemonStore.ResolveDefaultDir("/elsewhere")).IsEqualTo("/elsewhere");
        await Assert.That(DaemonStore.ResolveDefaultDir("").Replace('\\', '/'))
            .EndsWith("/.config/kcap/daemons");
    }

    /// <summary>
    /// Pins the socket-path budget. A control socket binds inside the daemons directory and macOS
    /// caps <c>sockaddr_un</c> at 103 characters — measured: <c>bind()</c> succeeds at 103 and fails
    /// at 104. A <see cref="TempDaemonStore"/> plus the longest daemon name any suite uses has to fit,
    /// and on a Mac the overhead is already 72 of those characters
    /// (<c>$TMPDIR</c> 49 + <c>kcap-test-</c> + a 6-char hint + a 6-char random suffix + <c>.sock</c>).
    /// Without this the overflow would be invisible on the Linux CI leg (short <c>/tmp</c>, 108-byte
    /// limit) and only surface on a developer's Mac.
    /// </summary>
    [Test]
    public async Task Temp_socket_path_fits_the_platform_limit() {
        if (OperatingSystem.IsWindows()) return; // no AF_UNIX path limit to blow, and temp roots differ

        using var daemons = new TempDaemonStore();
        var longestNameInUse = new string('x', 22); // "test-consent-subscribe"

        await Assert.That(daemons.Store.SocketPath(longestNameInUse).Length).IsLessThanOrEqualTo(103);
    }
}
