using Capacitor.Cli.Core;

namespace Capacitor.Cli.Tests.Unit.Daemon;

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
        var dir = Path.Combine(Path.GetTempPath(), "kcap-enum-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        DaemonLockPaths.OverrideDirectoryForTesting(dir);

        try {
            // alpha has both lock and pid (a live/recently-live daemon — always writes .pid).
            // beta has ONLY a lock (doctor already cleaned its markers): an inert leftover
            //   that must NOT be listed — the lock file cannot be safely deleted, so listing
            //   it would re-surface the entry on every run (the bug this exclusion fixes).
            // gamma has only a pid (orphan from before migration).
            File.WriteAllText(Path.Combine(dir, "alpha.lock"), "instance-1");
            File.WriteAllText(Path.Combine(dir, "alpha.pid"),  "12345");
            File.WriteAllText(Path.Combine(dir, "beta.lock"),  "instance-2");
            File.WriteAllText(Path.Combine(dir, "gamma.pid"),  "67890");

            var names = DaemonLockPaths.EnumerateNames();

            await Assert.That(names).Count().IsEqualTo(2);
            await Assert.That(names).Contains("alpha");
            await Assert.That(names).Contains("gamma");
            await Assert.That(names).DoesNotContain("beta");
        } finally {
            DaemonLockPaths.OverrideDirectoryForTesting(null);
            try { Directory.Delete(dir, recursive: true); } catch { /* best-effort */ }
        }
    }

    [Test]
    public async Task EnumerateNames_DeduplicatesNamesAppearingInMultipleMarkers() {
        var dir = Path.Combine(Path.GetTempPath(), "kcap-enum-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        DaemonLockPaths.OverrideDirectoryForTesting(dir);

        try {
            File.WriteAllText(Path.Combine(dir, "alpha.pid"),     "12345");
            File.WriteAllText(Path.Combine(dir, "alpha.version"), "0.11.7");

            var names = DaemonLockPaths.EnumerateNames();

            await Assert.That(names).Count().IsEqualTo(1);
            await Assert.That(names[0]).IsEqualTo("alpha");
        } finally {
            DaemonLockPaths.OverrideDirectoryForTesting(null);
            try { Directory.Delete(dir, recursive: true); } catch { /* best-effort */ }
        }
    }
}
