using Capacitor.Cli.Core;
using Capacitor.Cli.Services;

namespace Capacitor.Cli.Tests.Unit.Services;

public class ServiceTxnLockTests {
    [TempDaemonPaths] public required TempDaemonStore Daemons { get; init; }

    [Test]
    public async Task Acquire_release_and_probe() {
        await Assert.That(ServiceTxnLock.IsHeld(Daemons.Store, "a")).IsFalse();
        var l = ServiceTxnLock.TryAcquire(Daemons.Store, "a", TimeSpan.Zero);
        await Assert.That(l).IsNotNull();
        try {
            await Assert.That(ServiceTxnLock.IsHeld(Daemons.Store, "a")).IsTrue();
            await Assert.That(ServiceTxnLock.TryAcquire(Daemons.Store, "a", TimeSpan.FromMilliseconds(50))).IsNull();
        } finally {
            l!.Dispose();
        }

        await Assert.That(ServiceTxnLock.IsHeld(Daemons.Store, "a")).IsFalse();
        await Assert.That(File.Exists(Daemons.Store.ServiceLockPath("a"))).IsTrue();
    }

    [Test]
    public async Task Creates_missing_lock_directory() {
        var lockDir = Daemons.PathTo("nonexistent-subdir");
        var paths   = new DaemonStore(lockDir);

        var l = ServiceTxnLock.TryAcquire(paths, "b", TimeSpan.Zero);
        await Assert.That(l).IsNotNull();
        try {
            await Assert.That(Directory.Exists(lockDir)).IsTrue();
        } finally {
            l!.Dispose();
        }
    }

    [Test]
    public async Task Distinct_from_daemon_lock_path() {
        await Assert.That(Daemons.Store.ServiceLockPath("a"))
            .IsNotEqualTo(Daemons.Store.LockPath("a"));
    }
}
