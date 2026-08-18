namespace Capacitor.Cli.Core.Tests.Unit;

/// <summary>
/// <see cref="DaemonLockPaths.EnumerateNames"/> derives names from the state markers
/// (<c>*.pid</c>/<c>*.restart-pending</c>/<c>*.version</c>), not from a lone <c>*.lock</c>:
/// an orphan PID must stay visible to <c>doctor --clean</c>, while an inert leftover lock
/// (which cannot be safely deleted) must not, or the entry would be re-listed forever.
/// </summary>
[NotInParallel(nameof(DaemonLockPaths) + ".OverrideDirectoryForTesting")]
public class DaemonLockEnumerationTests {
    [Test]
    public async Task EnumerateNames_DerivesFromMarkers_NotFromBareLock() {
        using var tmp = new TempDir();
        DaemonLockPaths.OverrideDirectoryForTesting(tmp.Path);

        try {
            // alpha has both lock and pid (a live/recently-live daemon — always writes .pid).
            // beta has ONLY a lock (doctor already cleaned its markers): an inert leftover
            //   that must NOT be listed — the lock file cannot be safely deleted, so listing
            //   it would re-surface the entry on every run (the bug this exclusion fixes).
            // gamma has only a pid (orphan from before migration).
            tmp.CreateFile("alpha.lock", "instance-1");
            tmp.CreateFile("alpha.pid",  "12345");
            tmp.CreateFile("beta.lock",  "instance-2");
            tmp.CreateFile("gamma.pid",  "67890");

            var names = DaemonLockPaths.EnumerateNames();

            await Assert.That(names).Count().IsEqualTo(2);
            await Assert.That(names).Contains("alpha");
            await Assert.That(names).Contains("gamma");
            await Assert.That(names).DoesNotContain("beta");
        } finally {
            DaemonLockPaths.OverrideDirectoryForTesting(null);
        }
    }

    [Test]
    public async Task EnumerateNames_DeduplicatesNamesAppearingInMultipleMarkers() {
        using var tmp = new TempDir();
        DaemonLockPaths.OverrideDirectoryForTesting(tmp.Path);

        try {
            tmp.CreateFile("alpha.pid",     "12345");
            tmp.CreateFile("alpha.version", "0.11.7");

            var names = DaemonLockPaths.EnumerateNames();

            await Assert.That(names).Count().IsEqualTo(1);
            await Assert.That(names[0]).IsEqualTo("alpha");
        } finally {
            DaemonLockPaths.OverrideDirectoryForTesting(null);
        }
    }
}
