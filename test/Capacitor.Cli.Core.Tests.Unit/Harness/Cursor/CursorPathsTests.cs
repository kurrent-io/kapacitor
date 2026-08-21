using Capacitor.Cli.Core.Harness.Cursor;

namespace Capacitor.Cli.Core.Tests.Unit.Harness.Cursor;

public class CursorPathsTests {
    [Test]
    public async Task Mac_default_dir_under_application_support() {
        var p = CursorPaths.Resolve(home: "/Users/me", platform: OsPlatform.MacOs);
        await Assert.That(p.UserDir).IsEqualTo("/Users/me/Library/Application Support/Cursor/User");
        await Assert.That(p.WorkspaceStorageDir).IsEqualTo("/Users/me/Library/Application Support/Cursor/User/workspaceStorage");
    }

    [Test]
    public async Task Linux_default_dir_under_config() {
        var p = CursorPaths.Resolve(home: "/home/me", platform: OsPlatform.Linux);
        await Assert.That(p.UserDir).IsEqualTo("/home/me/.config/Cursor/User");
    }

    [Test]
    public async Task Windows_default_dir_under_appdata() {
        var p = CursorPaths.Resolve(home: @"C:\Users\me", platform: OsPlatform.Windows, appData: @"C:\Users\me\AppData\Roaming");
        await Assert.That(p.UserDir).IsEqualTo(@"C:\Users\me\AppData\Roaming\Cursor\User");
    }

    [Test]
    public async Task ProjectsDir_is_under_dot_cursor_on_every_platform() {
        await Assert.That(CursorPaths.ProjectsDir(home: "/Users/me")).IsEqualTo(Path.Combine("/Users/me", ".cursor", "projects"));
    }

    [Test]
    public async Task UserMcpJson_is_dot_cursor_mcp_json_under_home() {
        await Assert.That(CursorPaths.UserMcpJson("/h")).IsEqualTo(Path.Combine("/h", ".cursor", "mcp.json"));
    }
}
