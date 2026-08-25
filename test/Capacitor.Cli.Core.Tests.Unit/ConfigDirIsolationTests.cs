namespace Capacitor.Cli.Core.Tests.Unit;

/// <summary>
/// Regression guard: a test run must NEVER be able to resolve the developer's REAL
/// <c>~/.config/kcap/</c>.
///
/// <para>A token left there by a real <c>kcap login</c> reads as this run's own, and the failure
/// surfaces two layers down as a request-count assertion that never mentions auth. In-process the
/// compiler forces a <see cref="ConfigRoot"/> to be passed; what is left to guard is a spawned
/// <c>kcap</c>, covered by the assembly-wide pin — see <c>ConfigDirGlobalSetup</c>.</para>
/// </summary>
public class ConfigDirIsolationTests {
    /// <summary>A spawn that bypasses <c>KcapProcess</c> inherits this, and must break loudly rather
    /// than resolve somewhere writable.</summary>
    [Test, NotInParallel]
    public async Task The_inherited_pin_is_a_directory_that_cannot_be_created() {
        var pinned = Environment.GetEnvironmentVariable(ConfigRoot.ConfigDirEnvVar);
        await Assert.That(pinned).IsNotNullOrEmpty();

        var realHomeConfig = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".config", "kcap");

        var root = ConfigRoot.FromEnvironment();
        await Assert.That(root.Directory.StartsWith(realHomeConfig, StringComparison.Ordinal)).IsFalse();
        // ENOTDIR, because the parent is a regular file — the one failure root cannot ignore.
        await Assert.That(() => Directory.CreateDirectory(root.Directory)).Throws<IOException>();
    }
}
