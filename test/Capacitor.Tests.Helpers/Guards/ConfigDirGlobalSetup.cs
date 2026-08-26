using Capacitor.Cli.Core;

namespace Capacitor.Tests.Helpers.Guards;

/// <summary>
/// Assembly-wide pin for the config directory. Unpinned, anything resolving a
/// <see cref="ConfigRoot"/> for itself reads the developer's real <c>~/.config/kcap</c> — a stale
/// token there once read as an auth lapse two layers down. The value cannot be created on purpose:
/// every test takes a <c>[TempConfigRoot]</c> and <see cref="KcapProcess"/> pins a usable one per
/// spawn, so whatever reaches this has bypassed both.
/// </summary>
public class ConfigDirGlobalSetup {
    static readonly TempDir Dir = new("noconfig");

    /// <summary>A path whose PARENT is a regular file, so <c>CreateDirectory</c> fails with ENOTDIR.
    /// Permissions would not do: CI running as root ignores an unwritable parent.</summary>
    public static string SentinelConfigDir => Path.Combine(Dir.Path, "not-a-directory", "kcap");

    [BeforeEvery(Assembly)]
    public static void PinConfigDir() {
        Dir.CreateFile("not-a-directory");
        Environment.SetEnvironmentVariable(ConfigRoot.ConfigDirEnvVar, SentinelConfigDir);
    }

    [AfterEvery(Assembly)]
    public static void CleanupConfigDir() {
        Environment.SetEnvironmentVariable(ConfigRoot.ConfigDirEnvVar, null);
        Dir.Dispose();
    }
}
