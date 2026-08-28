namespace Capacitor.Cli.Core.Tests.Unit;

/// <summary>
/// Regression guard: a test run must NEVER be able to resolve the developer's REAL
/// <c>~/.config/kcap/daemons/</c> directory.
///
/// <para><see cref="DaemonStore"/> ignores <c>KCAP_CONFIG_DIR</c> and the default daemon name is the
/// OS username, so an unisolated resolution once found the live launchd daemon's PID file and
/// <c>Process.Kill(entireProcessTree: true)</c>'d it, taking out the developer's daemon and its
/// hosted agents. In-process the compiler forces a context to be passed; what is left to guard is a
/// spawned <c>kcap</c>, covered by the assembly-wide pin — see <c>DaemonPathsGlobalSetup</c>.</para>
/// </summary>
public class DaemonStoreIsolationTests {
    /// <summary>A spawn that bypasses <c>KcapProcess</c> inherits this, and must break loudly rather
    /// than resolve somewhere writable.</summary>
    [Test]
    public async Task The_inherited_pin_is_a_directory_that_cannot_be_created() {
        var pinned = Environment.GetEnvironmentVariable(DaemonStore.DaemonsDirEnvVar);
        await Assert.That(pinned).IsNotNullOrEmpty();

        var realHomeDaemons = Path.Combine(
#pragma warning disable RS0030 // the real home is what the pin must not resolve to
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".config", "kcap", "daemons");
#pragma warning restore RS0030

        var paths = new DaemonStore(pinned!);
        await Assert.That(paths.PidPath(Environment.UserName)
            .StartsWith(realHomeDaemons, StringComparison.Ordinal)).IsFalse();
        // ENOTDIR, because the parent is a regular file — the one failure root cannot ignore.
        await Assert.That(paths.EnsureDirectory).Throws<IOException>();
    }
}
