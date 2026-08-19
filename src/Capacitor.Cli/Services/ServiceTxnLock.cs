using Capacitor.Cli.Core;

namespace Capacitor.Cli.Services;

/// <summary>
/// Per-label cross-process lock for serializing mutating <c>kcap daemon service</c> verbs.
/// Lock file lives in the daemons directory, distinct from <see cref="DaemonStore.LockPath"/>,
/// and is never unlinked.
/// </summary>
sealed class ServiceTxnLock : IDisposable {
    readonly FileStream _stream;

    ServiceTxnLock(FileStream stream) => _stream = stream;

    /// <summary>
    /// Non-blocking probe: true iff some process currently holds the lock.
    /// </summary>
    public static bool IsHeld(DaemonStore store, string daemonName) {
        var path = store.ServiceLockPath(daemonName);

        if (!File.Exists(path)) return false;

        try {
            using var stream = new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
            return false;
        } catch (IOException) {
            return true;
        }
    }

    /// <summary>
    /// Blocks up to <paramref name="wait"/>; null on contention timeout. Lock file is created but NEVER deleted.
    /// </summary>
    public static ServiceTxnLock? TryAcquire(DaemonStore store, string daemonName, TimeSpan wait) {
        store.EnsureDirectory();
        var path = store.ServiceLockPath(daemonName);
        var deadline = DateTime.UtcNow.Add(wait);

        while (true) {
            try {
                var stream = new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
                return new ServiceTxnLock(stream);
            } catch (IOException) {
                if (DateTime.UtcNow >= deadline) {
                    return null;
                }

                System.Threading.Thread.Sleep(100);
            }
        }
    }

    public void Dispose() => _stream.Dispose();
}
