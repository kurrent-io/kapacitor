using Capacitor.Cli.Core;

namespace Capacitor.Cli.Tests.Unit;

/// <summary>
/// Regression test for the config-dir isolation failure mode: <c>PathHelpers.ConfigDir</c>
/// is <c>static readonly</c>, captured once per process. If anything in this assembly
/// touches <see cref="PathHelpers"/> before <see cref="RepoPathStoreGlobalSetup"/>'s
/// <c>[ModuleInitializer]</c> has pinned <c>KCAP_CONFIG_DIR</c> to an isolated temp
/// directory, the whole test process silently reads and writes the developer's real
/// <c>~/.config/kcap</c> for its entire lifetime instead.
///
/// This assertion is the load-bearing safety net: it fails whenever isolation is set
/// up too late (e.g. reverted to a TUnit <c>[Before(Assembly)]</c> hook, which runs
/// after Main starts and is not guaranteed to beat every other static touch), and
/// passes whenever it is pinned via a module initializer, which the CLR guarantees
/// runs before any type in the module is used.
/// </summary>
public class ConfigDirIsolationTests {
    [Test]
    public async Task ConfigPath_DoesNotResolveUnderTheRealUserConfigDirectory() {
        var realConfigDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            ".config",
            "kcap"
        );

        var resolved = PathHelpers.ConfigPath("config.json");

        await Assert.That(Path.GetDirectoryName(resolved)).IsNotEqualTo(realConfigDir);
    }

    [Test]
    public async Task ConfigPath_ResolvesUnderTheSharedTestConfigDirectory() {
        var resolved = PathHelpers.ConfigPath("config.json");

        await Assert.That(Path.GetDirectoryName(resolved)).IsEqualTo(RepoPathStoreGlobalSetup.SharedConfigDir);
    }
}
