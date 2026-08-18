using Capacitor.Cli.Daemon.Services;

namespace Capacitor.Cli.Daemon.Tests.Unit.Services;

public class DetachedRespawnArgsTests {
    [Test]
    public async Task Appends_await_lock_once() {
        var args = DetachedRespawnStrategy.BuildChildArgs(["--name", "laptop", "--log-file", "/tmp/d.log"]);
        await Assert.That(args).IsEquivalentTo(new[] { "--name", "laptop", "--log-file", "/tmp/d.log", "--await-lock" });
    }

    [Test]
    public async Task Does_not_duplicate_existing_await_lock() {
        var args = DetachedRespawnStrategy.BuildChildArgs(["--name", "laptop", "--await-lock"]);
        await Assert.That(args.Count(a => a == "--await-lock")).IsEqualTo(1);
    }
}
