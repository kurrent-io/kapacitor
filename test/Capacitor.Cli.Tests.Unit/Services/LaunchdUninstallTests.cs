using Capacitor.Cli.Services;

namespace Capacitor.Cli.Tests.Unit.Services;

/// <summary>
/// Spec §3.4: a non-zero <c>bootout</c> is not automatically a failure — the label may simply already be
/// unloaded. These drive <see cref="LaunchdServiceManager.Uninstall"/> through the injected launchctl runner
/// and a temp <c>HOME</c> so the plist path is real and its presence/absence is assertable.
/// </summary>
[NotInParallel("HomeEnvVarMutation")]
public class LaunchdUninstallTests {
    static (string Home, string PlistPath) SetUpHome(string id) {
        var home = Directory.CreateTempSubdirectory("kcap-uninstall-").FullName;
        Environment.SetEnvironmentVariable("HOME", home);
        var dir = LaunchdUnit.AgentsDir();
        Directory.CreateDirectory(dir);
        var path = LaunchdUnit.PlistPath(id);
        File.WriteAllText(path, "<plist/>");
        return (home, path);
    }

    static async Task WithHome(Func<string, Task> body) {
        var originalHome = Environment.GetEnvironmentVariable("HOME");
        var id = "test";
        var (home, path) = SetUpHome(id);
        try {
            await body(path);
        } finally {
            Environment.SetEnvironmentVariable("HOME", originalHome);
            try { Directory.Delete(home, recursive: true); } catch { /* best-effort */ }
        }
    }

    [Test]
    public async Task Bootout_success_deletes_plist_and_returns_true() {
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
