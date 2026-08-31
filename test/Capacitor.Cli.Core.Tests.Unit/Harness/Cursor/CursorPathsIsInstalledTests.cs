using Capacitor.Cli.Core.Harness.Cursor;

namespace Capacitor.Cli.Core.Tests.Unit.Harness.Cursor;

public class CursorPathsIsInstalledTests {
    static CursorPaths Cur(string home) => new(new(home));

    [Test]
    public async Task IsInstalled_true_when_user_home_has_dot_cursor() {
        using var tmp = new TempDir();
        tmp.CreateDir(".cursor");

        await Assert.That(Cur(tmp.Path).IsInstalled).IsTrue();
    }

    /// The Electron user dir is the second signal, and the path comes from the class rather than
    /// from here — which layout it names is that host's business. Windows puts it under the real
    /// Roaming AppData, which no temp home can stand in for.
    [Test]
    public async Task IsInstalled_true_when_this_hosts_electron_user_dir_exists() {
        Skip.When(OperatingSystem.IsWindows(), "the Windows Electron dir lies outside any temp home");

        using var tmp = new TempDir();
        var       cur = Cur(tmp.Path);

        Directory.CreateDirectory(cur.UserDir);

        await Assert.That(cur.IsInstalled).IsTrue();
    }

    [Test]
    public async Task IsInstalled_false_when_no_cursor_dirs_exist() {
        using var tmp = new TempDir();

        await Assert.That(Cur(tmp.Path).IsInstalled).IsFalse();
    }

    [Test]
    public async Task UserHooksJson_is_dot_cursor_hooks_json_under_home() {
        await Assert.That(Cur("/tmp/h").UserHooksJson)
                    .IsEqualTo(Path.Combine("/tmp/h", ".cursor", "hooks.json"));
    }

    [Test]
    public async Task SpoolDir_is_dot_cursor_kcap_pending_under_home() {
        await Assert.That(Cur("/tmp/h").SpoolDir)
                    .IsEqualTo(Path.Combine("/tmp/h", ".cursor", "kcap-pending"));
    }
}
