using Capacitor.Cli.Core;
using Capacitor.Cli.Services;

namespace Capacitor.Cli.Tests.Unit.Services;

[NotInParallel(nameof(DaemonLockPaths) + ".OverrideDirectoryForTesting")]
public class ServiceTxnLockTests {
    [Test]
    public async Task Acquire_release_and_probe() {
        using var tmp = new TempDir();
        var dir = tmp.Path;
        DaemonLockPaths.OverrideDirectoryForTesting(dir);
        try {
            await Assert.That(ServiceTxnLock.IsHeld("a")).IsFalse();
            var l = ServiceTxnLock.TryAcquire("a", TimeSpan.Zero);
            await Assert.That(l).IsNotNull();
            try {
                await Assert.That(ServiceTxnLock.IsHeld("a")).IsTrue();
                await Assert.That(ServiceTxnLock.TryAcquire("a", TimeSpan.FromMilliseconds(50))).IsNull();
            } finally {
                l!.Dispose();
            }

            await Assert.That(ServiceTxnLock.IsHeld("a")).IsFalse();
            await Assert.That(File.Exists(ServiceTxnLock.LockPath("a"))).IsTrue();
        } finally {
            DaemonLockPaths.OverrideDirectoryForTesting(null);
        }
    }

    [Test]
    public async Task Creates_missing_lock_directory() {
        using var tmp = new TempDir();
        var lockDir = tmp.PathTo("nonexistent-subdir");
        DaemonLockPaths.OverrideDirectoryForTesting(lockDir);
        try {
            var l = ServiceTxnLock.TryAcquire("b", TimeSpan.Zero);
            await Assert.That(l).IsNotNull();
            try {
                await Assert.That(Directory.Exists(lockDir)).IsTrue();
            } finally {
                l!.Dispose();
            }
        } finally {
            DaemonLockPaths.OverrideDirectoryForTesting(null);
        }
    }

    [Test]
    public async Task Distinct_from_daemon_lock_path() {
        await Assert.That(ServiceTxnLock.LockPath("a")).IsNotEqualTo(DaemonLockPaths.LockPath("a"));
    }
}
