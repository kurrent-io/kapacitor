namespace Capacitor.Cli.Core.Tests.Unit;

public class DaemonRestartMarkerTests {
    [Test]
    public async Task Write_then_read_round_trips() {
        using var daemons = new TempDaemonStore();
        var when = new DateTimeOffset(2026, 6, 25, 12, 3, 0, TimeSpan.Zero);

        DaemonRestartMarker.Write(daemons.Store, "laptop", new DaemonRestartMarker("v0.4.11", "self-detected", when));
        var read = DaemonRestartMarker.TryRead(daemons.Store, "laptop");

        await Assert.That(read).IsNotNull();
        await Assert.That(read!.RunningVersion).IsEqualTo("v0.4.11");
        await Assert.That(read.Reason).IsEqualTo("self-detected");
        await Assert.That(read.QueuedAt).IsEqualTo(when);
    }

    [Test]
    public async Task TryRead_returns_null_when_absent() {
        using var daemons = new TempDaemonStore();

        await Assert.That(DaemonRestartMarker.TryRead(daemons.Store, "nope")).IsNull();
    }

    [Test]
    public async Task EnumerateNames_includes_marker_only_entry() {
        using var daemons = new TempDaemonStore();
        File.WriteAllText(daemons.Store.RestartPendingPath("orphan"), "{}");

        await Assert.That(daemons.Store.EnumerateNames()).Contains("orphan");
    }
}
