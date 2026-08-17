namespace Capacitor.Cli.Core.Tests.Unit;

[NotInParallel(nameof(DaemonLockPaths) + ".OverrideDirectoryForTesting")]
public class DaemonVersionMarkerTests {
    [Test]
    public async Task Write_then_read_round_trips() {
        using var dir = new TempDir();
        DaemonLockPaths.OverrideDirectoryForTesting(dir.Path);
        try {
            DaemonVersionMarker.Write("laptop", "0.4.11+sha.abc1234");

            await Assert.That(DaemonVersionMarker.TryRead("laptop")).IsEqualTo("0.4.11+sha.abc1234");
        } finally {
            DaemonLockPaths.OverrideDirectoryForTesting(null);
        }
    }

    [Test]
    public async Task TryRead_returns_null_when_absent() {
        using var dir = new TempDir();
        DaemonLockPaths.OverrideDirectoryForTesting(dir.Path);
        try {
            await Assert.That(DaemonVersionMarker.TryRead("nope")).IsNull();
        } finally {
            DaemonLockPaths.OverrideDirectoryForTesting(null);
        }
    }

    [Test]
    public async Task TryRead_returns_null_for_blank_marker() {
        using var dir = new TempDir();
        DaemonLockPaths.OverrideDirectoryForTesting(dir.Path);
        try {
            File.WriteAllText(DaemonLockPaths.VersionPath("laptop"), "   \n");

            await Assert.That(DaemonVersionMarker.TryRead("laptop")).IsNull();
        } finally {
            DaemonLockPaths.OverrideDirectoryForTesting(null);
        }
    }

    [Test]
    public async Task Delete_removes_marker() {
        using var dir = new TempDir();
        DaemonLockPaths.OverrideDirectoryForTesting(dir.Path);
        try {
            DaemonVersionMarker.Write("laptop", "0.4.11");
            DaemonVersionMarker.Delete("laptop");

            await Assert.That(File.Exists(DaemonLockPaths.VersionPath("laptop"))).IsFalse();
        } finally {
            DaemonLockPaths.OverrideDirectoryForTesting(null);
        }
    }

    [Test]
    public async Task EnumerateNames_includes_marker_only_entry() {
        using var dir = new TempDir();
        DaemonLockPaths.OverrideDirectoryForTesting(dir.Path);
        try {
            DaemonVersionMarker.Write("orphan", "0.4.11");

            await Assert.That(DaemonLockPaths.EnumerateNames()).Contains("orphan");
        } finally {
            DaemonLockPaths.OverrideDirectoryForTesting(null);
        }
    }
}
