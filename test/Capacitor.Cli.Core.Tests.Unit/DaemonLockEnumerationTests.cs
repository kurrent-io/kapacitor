namespace Capacitor.Cli.Core.Tests.Unit;

/// <summary>
/// <see cref="DaemonStore.EnumerateNames"/> derives names from the state markers
/// (<c>*.pid</c>/<c>*.restart-pending</c>/<c>*.version</c>), not from a lone <c>*.lock</c>:
/// an orphan PID must stay visible to <c>doctor --clean</c>, while an inert leftover lock
/// (which cannot be safely deleted) must not, or the entry would be re-listed forever.
/// </summary>
public class DaemonLockEnumerationTests {
    [TempDaemonPaths] public required TempDaemonStore Daemons { get; init; }

    [Test]
    public async Task EnumerateNames_DerivesFromMarkers_NotFromBareLock() {
        // alpha has both lock and pid (a live/recently-live daemon — always writes .pid).
        // beta has ONLY a lock (doctor already cleaned its markers): an inert leftover
        //   that must NOT be listed — the lock file cannot be safely deleted, so listing
        //   it would re-surface the entry on every run (the bug this exclusion fixes).
        // gamma has only a pid (orphan from before migration).
        Daemons.CreateFile("alpha.lock", "instance-1");
        Daemons.CreateFile("alpha.pid",  "12345");
        Daemons.CreateFile("beta.lock",  "instance-2");
        Daemons.CreateFile("gamma.pid",  "67890");

        var names = Daemons.Store.EnumerateNames();

        await Assert.That(names).Count().IsEqualTo(2);
        await Assert.That(names).Contains("alpha");
        await Assert.That(names).Contains("gamma");
        await Assert.That(names).DoesNotContain("beta");
    }

    [Test]
    public async Task EnumerateNames_DeduplicatesNamesAppearingInMultipleMarkers() {
        Daemons.CreateFile("alpha.pid",     "12345");
        Daemons.CreateFile("alpha.version", "0.11.7");

        var names = Daemons.Store.EnumerateNames();

        await Assert.That(names).Count().IsEqualTo(1);
        await Assert.That(names[0]).IsEqualTo("alpha");
    }
}
