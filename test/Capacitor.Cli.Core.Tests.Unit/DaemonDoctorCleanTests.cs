using Capacitor.Cli.Core.LocalIpc;

namespace Capacitor.Cli.Core.Tests.Unit;

/// <summary>
/// A cleaned stale entry must drop out of <see cref="DaemonLockPaths.EnumerateNames"/>.
/// The lock file is a flock mutex that <c>--clean</c> cannot safely delete, so the listing
/// keys on the state markers: once they are gone the name disappears even though the inert
/// lock lingers. Previously a lone lock kept re-surfacing the entry on every run.
/// </summary>
[NotInParallel(nameof(DaemonLockPaths) + ".OverrideDirectoryForTesting")]
public class DaemonDoctorCleanTests {
    [Test]
    public async Task RemovingMarkers_DropsEntryFromEnumeration_EvenWhenLockLingers() {
        using var dir = new TempDir();
        DaemonLockPaths.OverrideDirectoryForTesting(dir.Path);

        try {
            const string name = "ai1409";

            // A confirmed-stale entry with the full leftover set: markers + the inert lock
            // + a dead control socket.
            File.WriteAllText(DaemonLockPaths.LockPath(name),           "deadbeef");
            File.WriteAllText(DaemonLockPaths.PidPath(name),            "12345");
            File.WriteAllText(DaemonLockPaths.VersionPath(name),        "0.11.7");
            File.WriteAllText(DaemonLockPaths.RestartPendingPath(name), "");
            File.WriteAllText(LocalSocketPaths.Socket(name),           "");

            await Assert.That(DaemonLockPaths.EnumerateNames()).Contains(name);

            // What `doctor --clean` does for a confirmed-stale entry: delete the state
            // markers under the held flock, but LEAVE the lock file (it cannot be safely
            // unlinked — doing so would break the per-inode flock mutex).
            File.Delete(DaemonLockPaths.PidPath(name));
            File.Delete(DaemonLockPaths.VersionPath(name));
            File.Delete(DaemonLockPaths.RestartPendingPath(name));

            // The entry is gone from the listing even though the lock file is still there.
            await Assert.That(DaemonLockPaths.EnumerateNames()).DoesNotContain(name);
            await Assert.That(File.Exists(DaemonLockPaths.LockPath(name))).IsTrue();
        } finally {
            DaemonLockPaths.OverrideDirectoryForTesting(null);
        }
    }

    [Test]
    public async Task BareLockLeftover_IsNeverListed() {
        using var dir = new TempDir();
        DaemonLockPaths.OverrideDirectoryForTesting(dir.Path);

        try {
            const string name = "agy-cert";

            // The exact shape a user hit: a lock (+ dead socket + start lock) with no
            // state markers. It must not appear at all — there is nothing `--clean` can
            // safely remove, and listing it would be a phantom stale entry forever.
            File.WriteAllText(DaemonLockPaths.LockPath(name),      "deadbeef");
            File.WriteAllText(DaemonLockPaths.StartLockPath(name), "");
            File.WriteAllText(LocalSocketPaths.Socket(name),      "");

            await Assert.That(DaemonLockPaths.EnumerateNames()).DoesNotContain(name);
        } finally {
            DaemonLockPaths.OverrideDirectoryForTesting(null);
        }
    }
}
