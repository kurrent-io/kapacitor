using Capacitor.Cli.Core.Harness.Cursor;

namespace Capacitor.Cli.Core.Tests.Unit.Harness.Cursor;

/// <summary>
/// D0/D1 — round-trips CursorMarkers' quarantine/barrier/heartbeat path helpers and the
/// quarantine read/write cycle, each test under its own config root.
/// </summary>
public class CursorMarkersTests {
    CursorMarkers Markers => new(Config.Root);

    [TempConfigRoot] public required TempConfigRoot Config { get; init; }

    static string NewSessionId() => Guid.NewGuid().ToString("N");

    [Test]
    public async Task Paths_are_dot_namespaced_under_the_shared_config_dir() {
        var sid = NewSessionId();

        await Assert.That(Markers.QuarantinePath(sid))
            .IsEqualTo(Path.Combine(Config.Directory, "cursor-quarantine", $"{sid}.json"));
        await Assert.That(Markers.BarrierPath(sid))
            .IsEqualTo(Path.Combine(Config.Directory, "cursor-barrier", $"{sid}.json"));
        await Assert.That(Markers.HeartbeatPath(sid))
            .IsEqualTo(Path.Combine(Config.Directory, "cursor-heartbeat", $"{sid}.json"));
    }

    [Test]
    public async Task IsQuarantined_false_before_any_marker_is_written() {
        var sid = NewSessionId();

        await Assert.That(Markers.IsQuarantined(sid)).IsFalse();
    }

    [Test]
    public async Task Quarantine_writes_a_marker_IsQuarantined_reads_it_back() {
        var sid = NewSessionId();

        Markers.Quarantine(sid, "rewrite detected");

        await Assert.That(Markers.IsQuarantined(sid)).IsTrue();

        var marker = Markers.ReadMarker(sid);

        await Assert.That(marker).IsNotNull();
        await Assert.That(marker!.Value.Reason).IsEqualTo("rewrite detected");
    }

    [Test]
    public async Task Quarantine_keeps_the_first_reason_on_a_second_call() {
        var sid = NewSessionId();

        Markers.Quarantine(sid, "first reason");
        Markers.Quarantine(sid, "second reason");

        var marker = Markers.ReadMarker(sid);

        await Assert.That(marker!.Value.Reason).IsEqualTo("first reason");
    }

    // Qodo PR #324 finding #4 — Quarantine's file operations must be fail-open, like
    // IsQuarantined/ReadMarker, since it's invoked from CursorRewriteGuard.Reject deep in the
    // watcher's drain loop with no broad exception handler above it. Occupy the marker's own
    // path with a directory so the final File.Move onto it fails; the call must swallow that
    // rather than throw, and a later read correctly still reports "not quarantined" (the
    // marker never actually landed).
    [Test]
    public async Task Quarantine_swallows_a_write_failure_instead_of_throwing() {
        var sid  = NewSessionId();
        var path = Markers.QuarantinePath(sid);

        Directory.CreateDirectory(path); // occupies the marker's own file path as a directory

        try {
            Markers.Quarantine(sid, "rewrite detected"); // must not throw

            await Assert.That(Markers.IsQuarantined(sid)).IsFalse();
        } finally {
            Directory.Delete(path);
        }
    }

    [Test]
    public async Task ReadMarker_null_when_no_marker_written() {
        var sid = NewSessionId();

        await Assert.That(Markers.ReadMarker(sid)).IsNull();
    }

    [Test]
    public async Task BarrierPending_false_when_no_barrier_created() {
        var sid = NewSessionId();

        await Assert.That(Markers.BarrierPending(sid, DateTimeOffset.UtcNow, TimeSpan.FromSeconds(60))).IsFalse();
    }

    [Test]
    public async Task Barrier_pending_until_cleared_then_expires_past_bound() {
        var sid = NewSessionId();
        var now = DateTimeOffset.UtcNow;

        Markers.CreateBarrier(sid, now);

        await Assert.That(Markers.BarrierPending(sid, now.AddSeconds(5), TimeSpan.FromSeconds(60))).IsTrue();
        await Assert.That(Markers.BarrierPending(sid, now.AddSeconds(61), TimeSpan.FromSeconds(60))).IsFalse(); // expired — proceeds

        Markers.ClearBarrier(sid);

        await Assert.That(Markers.BarrierPending(sid, now.AddSeconds(5), TimeSpan.FromSeconds(60))).IsFalse();
    }

    [Test]
    public async Task ClearBarrier_is_a_noop_when_nothing_was_created() {
        var sid = NewSessionId();

        Markers.ClearBarrier(sid); // must not throw

        await Assert.That(Markers.BarrierPending(sid, DateTimeOffset.UtcNow, TimeSpan.FromSeconds(60))).IsFalse();
    }

    [Test]
    public async Task TouchHeartbeat_writes_a_timestamp_WatcherHeartbeat_can_read_back() {
        var sid = NewSessionId();
        var now = DateTimeOffset.UtcNow;

        Markers.TouchHeartbeat(sid, now);

        await Assert.That(WatcherHeartbeat.Read(Markers.HeartbeatPath(sid))).IsEqualTo(now);
    }

    // the durable per-child subagent-start-acknowledgement marker.
    [Test]
    public async Task HasSubagentStartAck_false_before_any_ack_is_recorded() {
        var childSid = NewSessionId();

        await Assert.That(Markers.HasSubagentStartAck(childSid)).IsFalse();
    }

    [Test]
    public async Task MarkSubagentStartAcked_makes_HasSubagentStartAck_true() {
        var childSid = NewSessionId();

        Markers.MarkSubagentStartAcked(childSid);

        await Assert.That(Markers.HasSubagentStartAck(childSid)).IsTrue();
    }

    [Test]
    public async Task SubagentStartAckPath_is_dot_namespaced_under_the_shared_config_dir_and_keyed_by_child() {
        var childSid = NewSessionId();

        await Assert.That(Markers.SubagentStartAckPath(childSid))
            .IsEqualTo(Path.Combine(Config.Directory, "cursor-subagent-start-ack", $"{childSid}.json"));
    }
}
