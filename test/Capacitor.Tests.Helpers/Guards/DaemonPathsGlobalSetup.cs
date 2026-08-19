using Capacitor.Cli.Core;

namespace Capacitor.Tests.Helpers.Guards;

/// <summary>
/// Assembly-wide pin for the daemons directory. Unpinned, a spawned <c>kcap</c> resolves the
/// developer's real <c>~/.config/kcap/daemons/</c> — one test SIGKILLed a live daemon that way.
/// The value cannot be created on purpose: <see cref="KcapProcess"/> pins a usable one per spawn,
/// so whatever reaches this has bypassed it.
/// </summary>
public class DaemonPathsGlobalSetup {
    static readonly TempDir Dir = new("nodaemons");

    /// <summary>A path whose PARENT is a regular file, so <c>CreateDirectory</c> fails with ENOTDIR.
    /// Permissions would not do: CI running as root ignores an unwritable parent.</summary>
    public static string SentinelDaemonsDir => Path.Combine(Dir.Path, "not-a-directory", "daemons");

    [BeforeEvery(Assembly)]
    public static void PinDaemonsDir() {
        Dir.CreateFile("not-a-directory");
        Environment.SetEnvironmentVariable(DaemonStore.DaemonsDirEnvVar, SentinelDaemonsDir);
    }

    [AfterEvery(Assembly)]
    public static void CleanupDaemonsDir() {
        Environment.SetEnvironmentVariable(DaemonStore.DaemonsDirEnvVar, null);
        Dir.Dispose();
    }
}
