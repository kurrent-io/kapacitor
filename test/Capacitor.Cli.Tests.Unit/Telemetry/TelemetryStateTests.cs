using Capacitor.Cli.Core.Telemetry;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;

namespace Capacitor.Cli.Tests.Unit.Telemetry;

// Lock on both PathOverride statics (the shared resources): TelemetryState's directly, and
// TelemetryDeviceId's because SetEnabled(false) deletes the device id file as a side effect (see
// TelemetryState.SetEnabled) — a test here that didn't isolate that path could delete a sibling
// test's device id out from under it. Keying on the resource rather than the class means every
// other test class in the suite that touches either shared static serialises against this one too.
// See TelemetryDeviceIdTests for the device-id-specific tests this class used to own before the
// split.
[NotInParallel([
    nameof(TelemetryState) + "." + nameof(TelemetryState.PathOverride),
    nameof(TelemetryDeviceId) + "." + nameof(TelemetryDeviceId.PathOverride),
])]
public class TelemetryStateTests {
    // Side effect: also points TelemetryDeviceId at a fresh, colocated file, so SetEnabled's
    // device-id deletion never reaches outside this test's own temp dir.
    static string NewTempPath() {
        var dir = Path.Combine(Path.GetTempPath(), $"kcap-telemetry-{Guid.NewGuid():N}");
        TelemetryDeviceId.PathOverride = Path.Combine(dir, "telemetry-device.json");
        return Path.Combine(dir, "telemetry.json");
    }

    [Test]
    public async Task Read_of_missing_file_is_all_defaults() {
        TelemetryState.PathOverride = NewTempPath();

        var state = TelemetryState.Read();

        await Assert.That(state.Enabled).IsNull();
        await Assert.That(state.NoticeShown).IsFalse();
    }

    [Test]
    public async Task Set_enabled_persists_and_survives_reread() {
        TelemetryState.PathOverride = NewTempPath();

        TelemetryState.SetEnabled(false);
        await Assert.That(TelemetryState.PersistedEnabled()).IsEqualTo((bool?)false);

        TelemetryState.SetEnabled(true);
        await Assert.That(TelemetryState.PersistedEnabled()).IsEqualTo((bool?)true);
    }

    [Test]
    public async Task Notice_shown_marker_persists() {
        TelemetryState.PathOverride = NewTempPath();

        await Assert.That(TelemetryState.Read().NoticeShown).IsFalse();
        TelemetryState.MarkNoticeShown();
        await Assert.That(TelemetryState.Read().NoticeShown).IsTrue();
    }

    [Test]
    public async Task Mark_notice_shown_preserves_enabled() {
        TelemetryState.PathOverride = NewTempPath();
        TelemetryState.SetEnabled(true);

        TelemetryState.MarkNoticeShown();

        var state = TelemetryState.Read();
        await Assert.That(state.Enabled).IsEqualTo((bool?)true);
        await Assert.That(state.NoticeShown).IsTrue();
    }

    [Test]
    public async Task Corrupt_file_reads_as_defaults_and_does_not_throw() {
        var path = NewTempPath();
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, "{ not json");
        TelemetryState.PathOverride = path;

        var state = TelemetryState.Read();

        await Assert.That(state.Enabled).IsNull();
        await Assert.That(state.NoticeShown).IsFalse();
    }

    // Guards the litter risk the temp-file-then-rename write introduced: a failed or half-finished
    // write must not leave a stray file next to telemetry.json.
    //
    // It deliberately does NOT prove atomicity, and would have passed against the old
    // File.WriteAllText too — interleaving a read with a write to observe a torn file isn't
    // something a unit test can do deterministically across platforms. Atomicity rests on
    // File.Move(overwrite: true) being a same-volume rename; this test only covers the cleanup
    // half of that change.
    [Test]
    public async Task Write_leaves_no_temp_file_behind_after_a_successful_mutation() {
        var path = NewTempPath();
        TelemetryState.PathOverride = path;

        TelemetryState.SetEnabled(false);

        var dir     = Path.GetDirectoryName(path)!;
        var entries = Directory.GetFiles(dir);
        await Assert.That(entries).IsEquivalentTo(new[] { path });
    }
}
