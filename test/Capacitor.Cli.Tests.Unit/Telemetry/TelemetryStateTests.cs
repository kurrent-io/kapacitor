using Capacitor.Cli.Core.Telemetry;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;

namespace Capacitor.Cli.Tests.Unit.Telemetry;

// Lock on TelemetryState.PathOverride (the shared resource) rather than this class name, so that
// other test classes in Tasks 7, 9, and 11 which also use PathOverride can reuse the same lock key.
[NotInParallel(nameof(TelemetryState) + "." + nameof(TelemetryState.PathOverride))]
public class TelemetryStateTests {
    static string NewTempPath() =>
        Path.Combine(Path.GetTempPath(), $"kcap-telemetry-{Guid.NewGuid():N}", "telemetry.json");

    [Test]
    public async Task Read_of_missing_file_is_all_defaults() {
        TelemetryState.PathOverride = NewTempPath();

        var state = TelemetryState.Read();

        await Assert.That(state.Id).IsNull();
        await Assert.That(state.Enabled).IsNull();
        await Assert.That(state.NoticeShown).IsFalse();
    }

    [Test]
    public async Task Device_id_is_created_once_and_is_stable() {
        TelemetryState.PathOverride = NewTempPath();

        var first  = TelemetryState.GetOrCreateDeviceId();
        var second = TelemetryState.GetOrCreateDeviceId();

        await Assert.That(first).IsNotNull();
        await Assert.That(second).IsEqualTo(first);
        await Assert.That(TelemetryState.Read().Id).IsEqualTo(first);
    }

    [Test]
    public async Task Device_id_is_a_bare_guid_with_no_hyphens() {
        TelemetryState.PathOverride = NewTempPath();

        var id = TelemetryState.GetOrCreateDeviceId()!;

        await Assert.That(id.Length).IsEqualTo(32);
        await Assert.That(id.Contains('-')).IsFalse();
    }

    // Opting out before first run must not mint an analytics identifier at all.
    [Test]
    public async Task No_device_id_is_written_while_disabled() {
        TelemetryState.PathOverride = NewTempPath();
        TelemetryState.SetEnabled(false);

        var id = TelemetryState.GetOrCreateDeviceId();

        await Assert.That(id).IsNull();
        await Assert.That(TelemetryState.Read().Id).IsNull();
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
    public async Task Set_enabled_preserves_existing_device_id() {
        TelemetryState.PathOverride = NewTempPath();
        var id = TelemetryState.GetOrCreateDeviceId();

        TelemetryState.SetEnabled(false);

        await Assert.That(TelemetryState.Read().Id).IsEqualTo(id);
    }

    [Test]
    public async Task Notice_shown_marker_persists() {
        TelemetryState.PathOverride = NewTempPath();

        await Assert.That(TelemetryState.Read().NoticeShown).IsFalse();
        TelemetryState.MarkNoticeShown();
        await Assert.That(TelemetryState.Read().NoticeShown).IsTrue();
    }

    [Test]
    public async Task Mark_notice_shown_preserves_existing_device_id_and_enabled() {
        TelemetryState.PathOverride = NewTempPath();
        var id = TelemetryState.GetOrCreateDeviceId();
        TelemetryState.SetEnabled(false);

        TelemetryState.MarkNoticeShown();

        var state = TelemetryState.Read();
        await Assert.That(state.Id).IsEqualTo(id);
        await Assert.That(state.Enabled).IsEqualTo((bool?)false);
        await Assert.That(state.NoticeShown).IsTrue();
    }

    [Test]
    public async Task Corrupt_file_reads_as_defaults_and_does_not_throw() {
        var path = NewTempPath();
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, "{ not json");
        TelemetryState.PathOverride = path;

        var state = TelemetryState.Read();

        await Assert.That(state.Id).IsNull();
        await Assert.That(state.NoticeShown).IsFalse();
    }

    [Test]
    public async Task Corrupt_file_heals_on_device_id_creation() {
        var path = NewTempPath();
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, "{ not json");
        TelemetryState.PathOverride = path;

        var first  = TelemetryState.GetOrCreateDeviceId();
        var second = TelemetryState.GetOrCreateDeviceId();

        await Assert.That(first).IsNotNull();
        await Assert.That(second).IsEqualTo(first);
    }
}
