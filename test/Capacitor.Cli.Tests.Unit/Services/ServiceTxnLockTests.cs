using Capacitor.Cli.Core;
using Capacitor.Cli.Services;

namespace Capacitor.Cli.Tests.Unit.Services;

[NotInParallel(nameof(DaemonLockPaths) + ".OverrideDirectoryForTesting")]
public class ServiceTxnLockTests {
    [Test]
    public async Task Acquire_release_and_probe() {
        var dir = Directory.CreateTempSubdirectory().FullName;
        DaemonLockPaths.OverrideDirectoryForTesting(dir);
        try {
            await Assert.That(ServiceTxnLock.IsHeld("a")).IsFalse();
            var l = ServiceTxnLock.TryAcquire("a", TimeSpan.Zero);
            await Assert.That(l).IsNotNull();
            await Assert.That(ServiceTxnLock.IsHeld("a")).IsTrue();
            await Assert.That(ServiceTxnLock.TryAcquire("a", TimeSpan.FromMilliseconds(50))).IsNull();
            l!.Dispose();
            await Assert.That(ServiceTxnLock.IsHeld("a")).IsFalse();
            await Assert.That(File.Exists(ServiceTxnLock.LockPath("a"))).IsTrue();
        } finally {
            DaemonLockPaths.OverrideDirectoryForTesting(null);
        }
    }

    [Test]
    public async Task Distinct_from_daemon_lock_path() {
        await Assert.That(ServiceTxnLock.LockPath("a")).IsNotEqualTo(DaemonLockPaths.LockPath("a"));
    }
}
