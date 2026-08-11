using Capacitor.Cli.Core;
using Capacitor.Cli.Services;

namespace Capacitor.Cli.Tests.Unit.Services;

[NotInParallel(nameof(DaemonLockPaths) + ".OverrideDirectoryForTesting")]
public class ServiceTxnMarkerTests {
    static TxnMarker M(string phase = "captured") => new(1, "install", phase, "absent|nounit||pid=", "no-unit", null);

    [Test]
    public async Task Roundtrip_and_phase_update() {
        var dir = Directory.CreateTempSubdirectory().FullName;
        DaemonLockPaths.OverrideDirectoryForTesting(dir);
        try {
            ServiceTxnMarker.Write("a", M());
            await Assert.That(ServiceTxnMarker.Read("a")!.Phase).IsEqualTo("captured");
            ServiceTxnMarker.Write("a", M("written") with { PlistFingerprint = ServiceTxnMarker.Fingerprint("<plist/>") });
            await Assert.That(ServiceTxnMarker.Read("a")!.Phase).IsEqualTo("written");
            ServiceTxnMarker.Delete("a");
            await Assert.That(ServiceTxnMarker.Exists("a")).IsFalse();
        } finally { DaemonLockPaths.OverrideDirectoryForTesting(null); }
    }

    [Test]
    public async Task Corrupt_marker_reads_null() {
        var dir = Directory.CreateTempSubdirectory().FullName;
        DaemonLockPaths.OverrideDirectoryForTesting(dir);
        try {
            File.WriteAllText(ServiceTxnMarker.MarkerPath("a"), "{not json");
            await Assert.That(ServiceTxnMarker.Read("a")).IsNull();
        } finally { DaemonLockPaths.OverrideDirectoryForTesting(null); }
    }

    [Test]
    public async Task Missing_marker_reads_null_and_exists_is_false() {
        var dir = Directory.CreateTempSubdirectory().FullName;
        DaemonLockPaths.OverrideDirectoryForTesting(dir);
        try {
            await Assert.That(ServiceTxnMarker.Exists("missing")).IsFalse();
            await Assert.That(ServiceTxnMarker.Read("missing")).IsNull();
        } finally { DaemonLockPaths.OverrideDirectoryForTesting(null); }
    }

    [Test]
    public async Task Marker_path_is_under_daemon_lock_paths_directory() {
        var dir = Directory.CreateTempSubdirectory().FullName;
        DaemonLockPaths.OverrideDirectoryForTesting(dir);
        try {
            await Assert.That(ServiceTxnMarker.MarkerPath("a")).IsEqualTo(Path.Combine(DaemonLockPaths.Directory, "a.service-txn"));
        } finally { DaemonLockPaths.OverrideDirectoryForTesting(null); }
    }

    [Test]
    public async Task Fingerprint_is_stable_hex() {
        var fp1 = ServiceTxnMarker.Fingerprint("<plist/>");
        var fp2 = ServiceTxnMarker.Fingerprint("<plist/>");
        await Assert.That(fp1).IsEqualTo(fp2);
        await Assert.That(fp1.Length).IsEqualTo(64);
        await Assert.That(fp1).Matches("^[0-9a-f]{64}$");
    }

    [Test]
    public async Task Delete_of_missing_marker_does_not_throw() {
        var dir = Directory.CreateTempSubdirectory().FullName;
        DaemonLockPaths.OverrideDirectoryForTesting(dir);
        try {
            ServiceTxnMarker.Delete("never-written");
            await Assert.That(ServiceTxnMarker.Exists("never-written")).IsFalse();
        } finally { DaemonLockPaths.OverrideDirectoryForTesting(null); }
    }

    // File.Delete on a path that is actually a directory throws (UnauthorizedAccessException on
    // every platform .NET runs Delete on) — this is what the try/catch in Delete swallows.
    [Test]
    public async Task Delete_swallows_the_exception_when_the_path_cannot_be_deleted_as_a_file() {
        var dir = Directory.CreateTempSubdirectory().FullName;
        DaemonLockPaths.OverrideDirectoryForTesting(dir);
        try {
            var path = ServiceTxnMarker.MarkerPath("a");
            Directory.CreateDirectory(path);
            ServiceTxnMarker.Delete("a");
            await Assert.That(Directory.Exists(path)).IsTrue();
        } finally { DaemonLockPaths.OverrideDirectoryForTesting(null); }
    }
}
