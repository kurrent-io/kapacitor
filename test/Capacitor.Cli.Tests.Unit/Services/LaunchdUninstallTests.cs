using Capacitor.Cli.Services;

namespace Capacitor.Cli.Tests.Unit.Services;

/// <summary>
/// Spec §3.4: a non-zero <c>bootout</c> is not automatically a failure — the label may simply already be
/// unloaded. These drive <see cref="LaunchdServiceManager.Uninstall(string, out string?)"/> through the injected launchctl runner
/// and a temp <c>HOME</c> so the plist path is real and its presence/absence is assertable.
/// </summary>
[NotInParallel("HomeEnvVarMutation")]
public class LaunchdUninstallTests {
    static string SetUpHome(string id, string home) {
        Environment.SetEnvironmentVariable("HOME", home);
        var dir = LaunchdUnit.AgentsDir();
        Directory.CreateDirectory(dir);
        var path = LaunchdUnit.PlistPath(id);
        File.WriteAllText(path, "<plist/>");
        return path;
    }

    static async Task WithHome(Func<string, Task> body) {
        var originalHome = Environment.GetEnvironmentVariable("HOME");
        var id = "test";
        using var tmp = new TempDir();
        var path = SetUpHome(id, tmp.Path);
        try {
            await body(path);
        } finally {
            Environment.SetEnvironmentVariable("HOME", originalHome);
        }
    }

    [Test]
    public async Task Bootout_success_deletes_plist_and_returns_true() {
        Skip.When(OperatingSystem.IsWindows(), "Uid() P/Invokes libc's getuid, POSIX-only");

        await WithHome(async path => {
            var mgr = new LaunchdServiceManager(runProcess: (_, _) => (0, "", ""));

            var ok = mgr.Uninstall("test", out var error);

            await Assert.That(ok).IsTrue();
            await Assert.That(error).IsNull();
            await Assert.That(File.Exists(path)).IsFalse();
        });
    }

    [Test]
    public async Task Bootout_failure_with_benign_absence_on_requery_deletes_plist_and_returns_true() {
        Skip.When(OperatingSystem.IsWindows(), "Uid() P/Invokes libc's getuid, POSIX-only");

        await WithHome(async path => {
            var mgr = new LaunchdServiceManager(runProcess: (_, args) =>
                args[0] == "bootout"
                    ? (113, "", "")
                    : (113, "", "Could not find service \"io.kurrent.kcap.daemon.test\" in domain for user gui: 501"));

            var ok = mgr.Uninstall("test", out var error);

            await Assert.That(ok).IsTrue();
            await Assert.That(error).IsNull();
            await Assert.That(File.Exists(path)).IsFalse();
        });
    }

    [Test]
    public async Task Bootout_failure_with_still_loaded_on_requery_retains_plist_and_returns_false() {
        Skip.When(OperatingSystem.IsWindows(), "Uid() P/Invokes libc's getuid, POSIX-only");

        await WithHome(async path => {
            var mgr = new LaunchdServiceManager(runProcess: (_, args) =>
                args[0] == "bootout"
                    ? (1, "", "Operation not permitted")
                    : (0, "state = running\npid = 924\n", ""));

            var ok = mgr.Uninstall("test", out var error);

            await Assert.That(ok).IsFalse();
            await Assert.That(error).IsNotNull();
            await Assert.That(File.Exists(path)).IsTrue();
        });
    }

    [Test]
    public async Task Bootout_failure_with_unknown_on_requery_retains_plist_and_returns_false() {
        Skip.When(OperatingSystem.IsWindows(), "Uid() P/Invokes libc's getuid, POSIX-only");

        await WithHome(async path => {
            var mgr = new LaunchdServiceManager(runProcess: (_, args) =>
                args[0] == "bootout"
                    ? (1, "", "Operation not permitted")
                    : (1, "", "Operation not permitted"));

            var ok = mgr.Uninstall("test", out var error);

            await Assert.That(ok).IsFalse();
            await Assert.That(error).IsNotNull();
            await Assert.That(File.Exists(path)).IsTrue();
        });
    }
}
