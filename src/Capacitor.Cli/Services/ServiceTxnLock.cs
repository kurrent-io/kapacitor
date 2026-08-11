using Capacitor.Cli.Core;

namespace Capacitor.Cli.Services;

/// <summary>
/// Per-label cross-process lock for serializing mutating <c>kcap daemon service</c> verbs.
/// Lock file lives under <see cref="DaemonLockPaths.Directory"/>, distinct from
/// <see cref="DaemonLockPaths.LockPath"/>, and is never unlinked.
/// </summary>
sealed class ServiceTxnLock : IDisposable {
    readonly FileStream _stream;

    ServiceTxnLock(FileStream stream) => _stream = stream;

    public static string LockPath(string serviceId) =>
        Path.Combine(DaemonLockPaths.Directory, $"{serviceId}.service-lock");

    /// <summary>
    /// Non-blocking probe: true iff some process currently holds the lock.
    /// </summary>
    public static bool IsHeld(string serviceId) {
        var path = LockPath(serviceId);

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
    public static ServiceTxnLock? TryAcquire(string serviceId, TimeSpan wait) {
        DaemonLockPaths.EnsureDirectory();
        var path = LockPath(serviceId);
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
