using Capacitor.Cli.Core.Harness.Cursor;

namespace Capacitor.Cli.Core.Tests.Unit.Harness.Cursor;

public class CursorPathsIsInstalledTests {
    static CursorPaths Cur(string home, OsPlatform platform, string? appData) =>
        new(new(home), platform, appData);

    [Test]
    public async Task IsInstalled_true_when_user_home_has_dot_cursor() {
        using var tmp = new TempDir();
        tmp.CreateDir(".cursor");
        await Assert.That(Cur(tmp.Path, OsPlatform.Linux, null).IsInstalled).IsTrue();
    }

    [Test]
    public async Task IsInstalled_true_when_macos_user_dir_exists() {
        using var tmp = new TempDir();
        tmp.CreateDir("Library", "Application Support", "Cursor", "User");
        await Assert.That(Cur(tmp.Path, OsPlatform.MacOs, null).IsInstalled).IsTrue();
    }

    [Test]
    public async Task IsInstalled_true_when_linux_config_user_dir_exists() {
        using var tmp = new TempDir();
        tmp.CreateDir(".config", "Cursor", "User");
        await Assert.That(Cur(tmp.Path, OsPlatform.Linux, null).IsInstalled).IsTrue();
    }

    [Test]
    public async Task IsInstalled_true_when_windows_appdata_user_dir_exists() {
        using var tmp = new TempDir();
        var appData = tmp.PathTo("AppData", "Roaming");
        Directory.CreateDirectory(Path.Combine(appData, "Cursor", "User"));
        await Assert.That(Cur(tmp.Path, OsPlatform.Windows, appData).IsInstalled).IsTrue();
    }

    [Test]
    public async Task IsInstalled_false_when_no_cursor_dirs_exist() {
        using var tmp = new TempDir();
        await Assert.That(Cur(tmp.Path, OsPlatform.Linux, null).IsInstalled).IsFalse();
        await Assert.That(Cur(tmp.Path, OsPlatform.MacOs, null).IsInstalled).IsFalse();
    }

    [Test]
    public async Task UserHooksJson_is_dot_cursor_hooks_json_under_home() {
        var resolved = Cur("/tmp/h", OsPlatform.Linux, null).UserHooksJson;
        await Assert.That(resolved).IsEqualTo(Path.Combine("/tmp/h", ".cursor", "hooks.json"));
    }

    [Test]
    public async Task SpoolDir_is_dot_cursor_kcap_pending_under_home() {
        var resolved = Cur("/tmp/h", OsPlatform.Linux, null).SpoolDir;
        await Assert.That(resolved).IsEqualTo(Path.Combine("/tmp/h", ".cursor", "kcap-pending"));
    }
}
