using System.Diagnostics;

namespace Capacitor.Cli.Daemon.Tests.Unit;

public class DaemonLockAwaitTests {
    [Test]
    public async Task Await_acquires_after_holder_releases() {
        using var daemons = new TempDaemonStore();

        var first = DaemonLock.TryAcquire(daemons.Store, "await-test");
        await Assert.That(first).IsNotNull();

        // Release the holder after a short delay, on a background task.
        var releaser = Task.Run(async () => { await Task.Delay(300); first!.Dispose(); });

        var sw     = Stopwatch.StartNew();
        var second = DaemonLock.TryAcquire(daemons.Store, "await-test", TimeSpan.FromSeconds(5));
        sw.Stop();

        await Assert.That(second).IsNotNull();
        await Assert.That(sw.ElapsedMilliseconds).IsGreaterThanOrEqualTo(200);
        second!.Dispose();
        await releaser;
    }

    [Test]
    public async Task Await_returns_null_if_never_released() {
        using var daemons = new TempDaemonStore();

        var first = DaemonLock.TryAcquire(daemons.Store, "held");
        await Assert.That(first).IsNotNull();

        var second = DaemonLock.TryAcquire(daemons.Store, "held", TimeSpan.FromMilliseconds(500));
        await Assert.That(second).IsNull();

        first!.Dispose();
    }
}
