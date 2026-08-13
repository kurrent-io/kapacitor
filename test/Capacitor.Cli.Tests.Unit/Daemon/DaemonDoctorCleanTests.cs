using Capacitor.Cli.Core;
using Capacitor.Cli.Core.LocalIpc;

namespace Capacitor.Cli.Tests.Unit.Daemon;

/// <summary>
/// Regression cover for the bug where <c>kcap daemon doctor --clean</c> could never
/// remove a confirmed-stale entry whose leftover included the <c>.lock</c> file.
///
/// <para>The lock is a per-inode <c>flock</c> mutex that cannot be safely deleted, so
/// <c>--clean</c> leaves it and only removes the state markers. The listing must
/// therefore key on the markers, not the lock: once the markers are gone the entry
/// disappears from <see cref="DaemonLockPaths.EnumerateNames"/> even though the inert
/// lock file remains on disk. Previously the lock alone kept the name in the listing,
/// so the entry was re-surfaced on every run.</para>
/// </summary>
[NotInParallel(nameof(DaemonLockPaths) + ".OverrideDirectoryForTesting")]
public class DaemonDoctorCleanTests {
    static string NewDir() {
        var dir = Path.Combine(Path.GetTempPath(), "kcap-doctor-clean-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);

        return dir;
    }

    [Test]
    public async Task RemovingMarkers_DropsEntryFromEnumeration_EvenWhenLockLingers() {
        var dir = NewDir();
        DaemonLockPaths.OverrideDirectoryForTesting(dir);

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
            try { Directory.Delete(dir, recursive: true); } catch { /* best-effort */ }
        }
    }

    [Test]
    public async Task BareLockLeftover_IsNeverListed() {
        var dir = NewDir();
        DaemonLockPaths.OverrideDirectoryForTesting(dir);

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
            try { Directory.Delete(dir, recursive: true); } catch { /* best-effort */ }
        }
    }
}
