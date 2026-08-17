using Capacitor.Cli.Commands;
using Capacitor.Cli.Core;

namespace Capacitor.Cli.Tests.Unit.Commands;

public class WatcherHeartbeatTests {
    [Test]
    public async Task within_startup_grace_is_never_stale() {
        var start = DateTimeOffset.UtcNow;
        var stale = WatcherHeartbeat.IsStale(lastBeat: null, startupAt: start, now: start.AddSeconds(5),
                                             grace: TimeSpan.FromSeconds(30), threshold: TimeSpan.FromSeconds(20));
        await Assert.That(stale).IsFalse();
    }

    [Test]
    public async Task old_heartbeat_after_grace_is_stale() {
        var start = DateTimeOffset.UtcNow;
        var stale = WatcherHeartbeat.IsStale(lastBeat: start.AddSeconds(35), startupAt: start,
                                             now: start.AddSeconds(90),
                                             grace: TimeSpan.FromSeconds(30), threshold: TimeSpan.FromSeconds(20));
        await Assert.That(stale).IsTrue();
    }

    [Test]
    public async Task recent_heartbeat_after_grace_is_not_stale() {
        var start = DateTimeOffset.UtcNow;
        var stale = WatcherHeartbeat.IsStale(lastBeat: start.AddSeconds(89), startupAt: start,
                                             now: start.AddSeconds(90),
                                             grace: TimeSpan.FromSeconds(30), threshold: TimeSpan.FromSeconds(20));
        await Assert.That(stale).IsFalse();
    }

    [Test]
    public async Task null_heartbeat_after_grace_is_stale() {
        // A watcher that never wrote a heartbeat at all (e.g. crashed before its first
        // main-loop iteration) must read as stale once past the startup grace — otherwise
        // a null lastBeat could be mistaken for "healthy" forever.
        var start = DateTimeOffset.UtcNow;
        var stale = WatcherHeartbeat.IsStale(lastBeat: null, startupAt: start, now: start.AddSeconds(31),
                                             grace: TimeSpan.FromSeconds(30), threshold: TimeSpan.FromSeconds(20));
        await Assert.That(stale).IsTrue();
    }

    [Test]
    public async Task touch_then_read_roundtrips() {
        using var tmp = new TempDir();
        var p = WatcherHeartbeat.HeartbeatPath(tmp.Path, "sess");
        var now = DateTimeOffset.UtcNow;
        WatcherHeartbeat.Touch(p, now);
        var read = WatcherHeartbeat.Read(p);
        await Assert.That(read).IsNotNull();
        await Assert.That((read!.Value - now).Duration()).IsLessThan(TimeSpan.FromSeconds(1));
    }

    [Test]
    public async Task read_missing_file_returns_null() {
        var p = WatcherHeartbeat.HeartbeatPath(Path.GetTempPath(), $"missing-{Guid.NewGuid():N}");
        await Assert.That(WatcherHeartbeat.Read(p)).IsNull();
    }

    [Test]
    public async Task touch_overwrites_previous_value() {
        using var tmp = new TempDir();
        var p = WatcherHeartbeat.HeartbeatPath(tmp.Path, "sess");
        WatcherHeartbeat.Touch(p, DateTimeOffset.UtcNow.AddMinutes(-5));
        var second = DateTimeOffset.UtcNow;
        WatcherHeartbeat.Touch(p, second);
        var read = WatcherHeartbeat.Read(p);
        await Assert.That(read).IsNotNull();
        await Assert.That((read!.Value - second).Duration()).IsLessThan(TimeSpan.FromSeconds(1));
    }

    // --- Connect-retry / reconnect heartbeat freshness ---
    // The connect-retry backoff grows to 30s, longer than the 20s staleness threshold. The wait
    // is chunked via HeartbeatSlices so the heartbeat is refreshed between slices; if any single
    // chunk could exceed the threshold, a healthy-but-reconnecting watcher would be falsely reaped.

    [Test]
    public async Task heartbeat_slices_never_exceed_the_max_slice() {
        // The worst case: a full 30s backoff wait. Every chunk must stay under the staleness
        // threshold so a reconnecting watcher can never look wedged.
        var slices = WatchCommand.HeartbeatSlices(TimeSpan.FromSeconds(30), WatchCommand.HeartbeatSlice);

        await Assert.That(slices.Count).IsGreaterThan(1);
        foreach (var s in slices) {
            await Assert.That(s).IsLessThanOrEqualTo(WatchCommand.HeartbeatSlice);
            await Assert.That(s).IsLessThan(WatcherHeartbeat.Threshold);
        }
    }

    [Test]
    public async Task heartbeat_slices_sum_to_the_total_wait() {
        var total  = TimeSpan.FromSeconds(23);
        var slices = WatchCommand.HeartbeatSlices(total, WatchCommand.HeartbeatSlice);

        var sum = TimeSpan.Zero;
        foreach (var s in slices) sum += s;

        await Assert.That(sum).IsEqualTo(total);
    }

    [Test]
    public async Task heartbeat_slices_short_wait_is_a_single_chunk() {
        var slices = WatchCommand.HeartbeatSlices(TimeSpan.FromSeconds(3), WatchCommand.HeartbeatSlice);

        await Assert.That(slices.Count).IsEqualTo(1);
        await Assert.That(slices[0]).IsEqualTo(TimeSpan.FromSeconds(3));
    }

    [Test]
    public async Task heartbeat_slices_non_positive_total_yields_no_chunks() {
        await Assert.That(WatchCommand.HeartbeatSlices(TimeSpan.Zero, WatchCommand.HeartbeatSlice).Count).IsEqualTo(0);
        await Assert.That(WatchCommand.HeartbeatSlices(TimeSpan.FromSeconds(-5), WatchCommand.HeartbeatSlice).Count).IsEqualTo(0);
    }
}
