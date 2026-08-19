namespace Capacitor.Cli.Core.Tests.Unit;

/// <summary>
/// A cleaned stale entry must drop out of <see cref="DaemonStore.EnumerateNames"/>.
/// The lock file is a flock mutex that <c>--clean</c> cannot safely delete, so the listing
/// keys on the state markers: once they are gone the name disappears even though the inert
/// lock lingers. Previously a lone lock kept re-surfacing the entry on every run.
/// </summary>
public class DaemonDoctorCleanTests {
    [TempDaemonPaths] public required TempDaemonStore Daemons { get; init; }

    [Test]
    public async Task RemovingMarkers_DropsEntryFromEnumeration_EvenWhenLockLingers() {
        const string name = "ai1409";

        // A confirmed-stale entry with the full leftover set: markers + the inert lock
        // + a dead control socket.
        File.WriteAllText(Daemons.Store.LockPath(name),           "deadbeef");
        File.WriteAllText(Daemons.Store.PidPath(name),            "12345");
        File.WriteAllText(Daemons.Store.VersionPath(name),        "0.11.7");
        File.WriteAllText(Daemons.Store.RestartPendingPath(name), "");
        File.WriteAllText(Daemons.Store.SocketPath(name),             "");

        await Assert.That(Daemons.Store.EnumerateNames()).Contains(name);

        // What `doctor --clean` does for a confirmed-stale entry: delete the state
        // markers under the held flock, but LEAVE the lock file (it cannot be safely
        // unlinked — doing so would break the per-inode flock mutex).
        File.Delete(Daemons.Store.PidPath(name));
        File.Delete(Daemons.Store.VersionPath(name));
        File.Delete(Daemons.Store.RestartPendingPath(name));

        // The entry is gone from the listing even though the lock file is still there.
        await Assert.That(Daemons.Store.EnumerateNames()).DoesNotContain(name);
        await Assert.That(File.Exists(Daemons.Store.LockPath(name))).IsTrue();
    }

    [Test]
    public async Task BareLockLeftover_IsNeverListed() {
        const string name = "agy-cert";

        // The exact shape a user hit: a lock (+ dead socket + start lock) with no
        // state markers. It must not appear at all — there is nothing `--clean` can
        // safely remove, and listing it would be a phantom stale entry forever.
        File.WriteAllText(Daemons.Store.LockPath(name),      "deadbeef");
        File.WriteAllText(Daemons.Store.StartLockPath(name), "");
        File.WriteAllText(Daemons.Store.SocketPath(name),        "");

        await Assert.That(Daemons.Store.EnumerateNames()).DoesNotContain(name);
    }
}
