using Capacitor.Cli.Core.LocalIpc;

namespace Capacitor.Cli.Core.Tests.Unit.LocalIpc;

public class LocalSocketPathsTests {
    [Test]
    public async Task Socket_path_is_under_daemon_dir_and_name_sanitized() {
        var p = LocalSocketPaths.Socket("My Daemon!");
        await Assert.That(p).EndsWith("my-daemon.sock");
        await Assert.That(p).StartsWith(DaemonLockPaths.Directory);
    }
}
