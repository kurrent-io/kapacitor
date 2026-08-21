namespace Capacitor.Cli.Core.Tests.Unit;

public class DaemonVersionMarkerTests {
    [Test]
    public async Task Write_then_read_round_trips() {
        using var daemons = new TempDaemonStore();

        DaemonVersionMarker.Write(daemons.Store, "laptop", "0.4.11+sha.abc1234");

        await Assert.That(DaemonVersionMarker.TryRead(daemons.Store, "laptop")).IsEqualTo("0.4.11+sha.abc1234");
    }

    [Test]
    public async Task TryRead_returns_null_when_absent() {
        using var daemons = new TempDaemonStore();

        await Assert.That(DaemonVersionMarker.TryRead(daemons.Store, "nope")).IsNull();
    }

    [Test]
    public async Task TryRead_returns_null_for_blank_marker() {
        using var daemons = new TempDaemonStore();
        File.WriteAllText(daemons.Store.VersionPath("laptop"), "   \n");

        await Assert.That(DaemonVersionMarker.TryRead(daemons.Store, "laptop")).IsNull();
    }

    [Test]
    public async Task Delete_removes_marker() {
        using var daemons = new TempDaemonStore();

        DaemonVersionMarker.Write(daemons.Store, "laptop", "0.4.11");
        DaemonVersionMarker.Delete(daemons.Store, "laptop");

        await Assert.That(File.Exists(daemons.Store.VersionPath("laptop"))).IsFalse();
    }

    [Test]
    public async Task EnumerateNames_includes_marker_only_entry() {
        using var daemons = new TempDaemonStore();

        DaemonVersionMarker.Write(daemons.Store, "orphan", "0.4.11");

        await Assert.That(daemons.Store.EnumerateNames()).Contains("orphan");
    }
}
