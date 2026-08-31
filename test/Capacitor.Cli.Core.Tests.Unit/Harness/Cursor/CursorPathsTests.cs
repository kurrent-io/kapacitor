using Capacitor.Cli.Core.Harness.Cursor;

namespace Capacitor.Cli.Core.Tests.Unit.Harness.Cursor;

/// <summary>
/// Cursor's layout as resolved on the host running these tests. Each per-OS expectation runs only on
/// that OS: the class asks the running OS, so no other statement about a layout is honest here.
/// </summary>
public class CursorPathsTests {
    static CursorPaths Cur(string home) => new(new(home));

    [Test]
    public async Task The_user_dir_sits_under_application_support_on_a_mac() {
        Skip.When(!OperatingSystem.IsMacOS(), "states the macOS layout");

        var p = Cur("/Users/me");

        await Assert.That(p.UserDir).IsEqualTo("/Users/me/Library/Application Support/Cursor/User");
        await Assert.That(p.WorkspaceStorageDir)
                    .IsEqualTo("/Users/me/Library/Application Support/Cursor/User/workspaceStorage");
    }

    [Test]
    public async Task The_user_dir_sits_under_dot_config_on_linux() {
        Skip.When(!OperatingSystem.IsLinux(), "states the Linux layout");

        await Assert.That(Cur("/home/me").UserDir).IsEqualTo("/home/me/.config/Cursor/User");
    }

    /// Roaming AppData is the OS's answer, not a path this test can spell. What it must NOT be is
    /// anything under the home — that is where every other OS puts the Electron dir.
    [Test]
    public async Task The_user_dir_sits_under_roaming_appdata_on_windows() {
        Skip.When(!OperatingSystem.IsWindows(), "states the Windows layout");

        var p = Cur(@"C:\Users\me");

        await Assert.That(p.UserDir).EndsWith(Path.Combine("Cursor", "User"));
        await Assert.That(Path.IsPathRooted(p.UserDir)).IsTrue();
        await Assert.That(p.UserDir).DoesNotContain(@"C:\Users\me");
    }

    [Test]
    public async Task ProjectsDir_is_under_dot_cursor_on_every_platform() {
        await Assert.That(Cur("/Users/me").ProjectsDir)
                    .IsEqualTo(Path.Combine("/Users/me", ".cursor", "projects"));
    }

    [Test]
    public async Task UserMcpJson_is_dot_cursor_mcp_json_under_home() {
        await Assert.That(Cur("/h").UserMcpJson).IsEqualTo(Path.Combine("/h", ".cursor", "mcp.json"));
    }
}
