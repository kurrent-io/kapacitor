using Capacitor.Cli.Core.Telemetry;

namespace Capacitor.Cli.Core.Tests.Unit.Telemetry;

// A root per test covers both files: SetEnabled(false) deletes the device id as a side effect (see
// TelemetryState.SetEnabled), and one root puts it where this test alone can see it. See
// TelemetryDeviceIdTests for the device-id-specific tests.
public class TelemetryStateTests {
    [TempConfigRoot] public required TempConfigRoot Config { get; init; }

    [Test]
    public async Task Read_of_missing_file_is_all_defaults() {
        var state = TelemetryState.Read(Config.Root);

        await Assert.That(state.Enabled).IsNull();
        await Assert.That(state.NoticeShown).IsFalse();
    }

    [Test]
    public async Task Set_enabled_persists_and_survives_reread() {
        TelemetryState.SetEnabled(false, Config.Root);
        await Assert.That(TelemetryState.PersistedEnabled(Config.Root)).IsFalse();

        TelemetryState.SetEnabled(true, Config.Root);
        await Assert.That(TelemetryState.PersistedEnabled(Config.Root)).IsTrue();
    }

    [Test]
    public async Task Notice_shown_marker_persists() {
        await Assert.That(TelemetryState.Read(Config.Root).NoticeShown).IsFalse();
        TelemetryState.MarkNoticeShown(Config.Root);
        await Assert.That(TelemetryState.Read(Config.Root).NoticeShown).IsTrue();
    }

    [Test]
    public async Task Mark_notice_shown_preserves_enabled() {
        TelemetryState.SetEnabled(true, Config.Root);

        TelemetryState.MarkNoticeShown(Config.Root);

        var state = TelemetryState.Read(Config.Root);
        await Assert.That(state.Enabled).IsTrue();
        await Assert.That(state.NoticeShown).IsTrue();
    }

    [Test]
    public async Task Corrupt_file_reads_as_defaults_and_does_not_throw() {
        File.WriteAllText(Config.PathTo("telemetry.json"), "{ not json");

        var state = TelemetryState.Read(Config.Root);

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
        var path = Config.PathTo("telemetry.json");

        TelemetryState.SetEnabled(false, Config.Root);

        var dir     = Path.GetDirectoryName(path)!;
        var entries = Directory.GetFiles(dir);
        await Assert.That(entries).IsEquivalentTo(new[] { path });
    }
}
