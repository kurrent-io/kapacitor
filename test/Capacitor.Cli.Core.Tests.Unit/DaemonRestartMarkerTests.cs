namespace Capacitor.Cli.Core.Tests.Unit;

[NotInParallel(nameof(DaemonLockPaths) + ".OverrideDirectoryForTesting")]
public class DaemonRestartMarkerTests {
    [Test]
    public async Task Write_then_read_round_trips() {
        using var dir = new TempDir();
        DaemonLockPaths.OverrideDirectoryForTesting(dir.Path);
        try {
            var when = new DateTimeOffset(2026, 6, 25, 12, 3, 0, TimeSpan.Zero);
            DaemonRestartMarker.Write("laptop", new DaemonRestartMarker("v0.4.11", "self-detected", when));

            var read = DaemonRestartMarker.TryRead("laptop");

            await Assert.That(read).IsNotNull();
            await Assert.That(read!.RunningVersion).IsEqualTo("v0.4.11");
            await Assert.That(read.Reason).IsEqualTo("self-detected");
            await Assert.That(read.QueuedAt).IsEqualTo(when);
        } finally {
            DaemonLockPaths.OverrideDirectoryForTesting(null);
        }
    }

    [Test]
    public async Task TryRead_returns_null_when_absent() {
        using var dir = new TempDir();
        DaemonLockPaths.OverrideDirectoryForTesting(dir.Path);
        try {
            await Assert.That(DaemonRestartMarker.TryRead("nope")).IsNull();
        } finally {
            DaemonLockPaths.OverrideDirectoryForTesting(null);
        }
    }

    [Test]
    public async Task EnumerateNames_includes_marker_only_entry() {
        using var dir = new TempDir();
        DaemonLockPaths.OverrideDirectoryForTesting(dir.Path);
        try {
            File.WriteAllText(DaemonLockPaths.RestartPendingPath("orphan"), "{}");
            await Assert.That(DaemonLockPaths.EnumerateNames()).Contains("orphan");
        } finally {
            DaemonLockPaths.OverrideDirectoryForTesting(null);
        }
    }
}
