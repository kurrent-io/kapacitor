using Capacitor.Cli.Services;

namespace Capacitor.Cli.Tests.Unit.Services;

/// <summary>
/// A non-zero <c>bootout</c> is not automatically a failure — the label may simply already be
/// unloaded. These drive <see cref="LaunchdServiceManager.Uninstall(string, out string?)"/> through the injected launchctl runner
/// and an ephemeral home so the plist path is real and its presence/absence is assertable.
/// </summary>
public class LaunchdUninstallTests {
    [TempHome] public required TempHome Home { get; init; }

    /// <summary>Seeds a plist under the ephemeral home and hands back its path.</summary>
    string SeedPlist() {
        Directory.CreateDirectory(LaunchdUnit.AgentsDir(Home));
        var path = LaunchdUnit.PlistPath(Home, "test");
        File.WriteAllText(path, "<plist/>");
        return path;
    }

    [Test]
    public async Task Bootout_success_deletes_plist_and_returns_true() {
        Skip.When(OperatingSystem.IsWindows(), "Uid() P/Invokes libc's getuid, POSIX-only");

        var path = SeedPlist();
        var mgr  = new LaunchdServiceManager(Home, runProcess: (_, _) => (0, "", ""));

        var ok = mgr.Uninstall("test", out var error);

        await Assert.That(ok).IsTrue();
        await Assert.That(error).IsNull();
        await Assert.That(File.Exists(path)).IsFalse();
    }

    [Test]
    public async Task Bootout_failure_with_benign_absence_on_requery_deletes_plist_and_returns_true() {
        Skip.When(OperatingSystem.IsWindows(), "Uid() P/Invokes libc's getuid, POSIX-only");

        var path = SeedPlist();
        var mgr  = new LaunchdServiceManager(Home, runProcess: (_, args) =>
            args[0] == "bootout"
                ? (113, "", "")
                : (113, "", "Could not find service \"io.kurrent.kcap.daemon.test\" in domain for user gui: 501"));

        var ok = mgr.Uninstall("test", out var error);

        await Assert.That(ok).IsTrue();
        await Assert.That(error).IsNull();
        await Assert.That(File.Exists(path)).IsFalse();
    }

    [Test]
    public async Task Bootout_failure_with_still_loaded_on_requery_retains_plist_and_returns_false() {
        Skip.When(OperatingSystem.IsWindows(), "Uid() P/Invokes libc's getuid, POSIX-only");

        var path = SeedPlist();
        var mgr  = new LaunchdServiceManager(Home, runProcess: (_, args) =>
            args[0] == "bootout"
                ? (1, "", "Operation not permitted")
                : (0, "state = running\npid = 924\n", ""));

        var ok = mgr.Uninstall("test", out var error);

        await Assert.That(ok).IsFalse();
        await Assert.That(error).IsNotNull();
        await Assert.That(File.Exists(path)).IsTrue();
    }

    [Test]
    public async Task Bootout_failure_with_unknown_on_requery_retains_plist_and_returns_false() {
        Skip.When(OperatingSystem.IsWindows(), "Uid() P/Invokes libc's getuid, POSIX-only");

        var path = SeedPlist();
        var mgr  = new LaunchdServiceManager(Home, runProcess: (_, args) =>
            args[0] == "bootout"
                ? (1, "", "Operation not permitted")
                : (1, "", "Operation not permitted"));

        var ok = mgr.Uninstall("test", out var error);

        await Assert.That(ok).IsFalse();
        await Assert.That(error).IsNotNull();
        await Assert.That(File.Exists(path)).IsTrue();
    }
}
