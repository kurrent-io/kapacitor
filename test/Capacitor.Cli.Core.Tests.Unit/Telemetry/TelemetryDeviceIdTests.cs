using Capacitor.Cli.Core.Telemetry;

namespace Capacitor.Cli.Core.Tests.Unit.Telemetry;

/// <summary>
/// Tests for <see cref="TelemetryDeviceId"/> — the anonymous device id, split out of
/// <c>telemetry.json</c> into its own lock-free file (see that type's doc comment for why). Mirrors
/// <c>MachineIdFileTests</c>' shape, since the on-disk pattern is lifted from <see cref="Capacitor.Cli.Core.MachineId"/>.
///
/// A few tests here (the SetEnabled/re-enable ones) are inherently cross-cutting: they exercise
/// <see cref="TelemetryState.SetEnabled"/>'s documented side effect of deleting the device id file,
/// so they need both PathOverride seams set. Locks on both statics for that reason — see
/// TelemetryStateTests for the sibling half of that split.
/// </summary>
[NotInParallel([
    nameof(TelemetryState) + "." + nameof(TelemetryState.PathOverride),
    nameof(TelemetryDeviceId) + "." + nameof(TelemetryDeviceId.PathOverride),
])]
public class TelemetryDeviceIdTests {
    [Test]
    public async Task Read_of_missing_file_is_null() {
        using var tmp = TempDir.WithPathTo("telemetry-device.json", out var deviceIdPath);
        TelemetryDeviceId.PathOverride = deviceIdPath;

        await Assert.That(TelemetryDeviceId.ReadPersisted()).IsNull();
    }

    [Test]
    public async Task Device_id_is_created_once_and_is_stable() {
        using var tmp = TempDir.WithPathTo("telemetry-device.json", out var deviceIdPath);
        TelemetryDeviceId.PathOverride = deviceIdPath;

        var first  = TelemetryDeviceId.GetOrCreate();
        var second = TelemetryDeviceId.GetOrCreate();

        await Assert.That(first).IsNotNull();
        await Assert.That(second).IsEqualTo(first);
        await Assert.That(TelemetryDeviceId.ReadPersisted()).IsEqualTo(first);
    }

    [Test]
    public async Task Device_id_is_a_bare_guid_with_no_hyphens() {
        using var tmp = TempDir.WithPathTo("telemetry-device.json", out var deviceIdPath);
        TelemetryDeviceId.PathOverride = deviceIdPath;

        var id = TelemetryDeviceId.GetOrCreate()!;

        await Assert.That(id.Length).IsEqualTo(32);
        await Assert.That(id.Contains('-')).IsFalse();
    }

    [Test]
    public async Task Second_get_or_create_does_not_rewrite_file_when_id_exists() {
        using var tmp = TempDir.WithPathTo("telemetry-device.json", out var path);
        TelemetryDeviceId.PathOverride = path;

        // First call creates the ID and writes the file.
        TelemetryDeviceId.GetOrCreate();
        await Task.Delay(10);   // ensure timestamp granularity

        var timestampAfterFirstCall = File.GetLastWriteTimeUtc(path);
        await Task.Delay(10);   // ensure time passes before second call

        // Second call should return the same ID without rewriting.
        TelemetryDeviceId.GetOrCreate();

        var timestampAfterSecondCall = File.GetLastWriteTimeUtc(path);

        await Assert.That(timestampAfterSecondCall).IsEqualTo(timestampAfterFirstCall);
    }

    [Test]
    public async Task Corrupt_file_heals_on_get_or_create() {
        using var tmp = new TempDir();
        var path = tmp.CreateFile("telemetry-device.json", "{ not json");
        TelemetryDeviceId.PathOverride = path;

        var first  = TelemetryDeviceId.GetOrCreate();
        var second = TelemetryDeviceId.GetOrCreate();

        await Assert.That(first).IsNotNull();
        await Assert.That(second).IsEqualTo(first);
        await Assert.That(TelemetryDeviceId.ReadPersisted()).IsEqualTo(first);
    }

    // Unlike the old coupled telemetry.json, GetOrCreate has no notion of "enabled" at all — the
    // file it owns doesn't carry that field. This pins the property that used to require a separate
    // precedence rule (an earlier revision of the coupled GetOrCreateDeviceId re-checked
    // state.Enabled itself, which broke KCAP_TELEMETRY=1 overriding a persisted opt-out): calling it
    // directly always mints/returns an id, full stop.
    [Test]
    public async Task Get_or_create_is_unaffected_by_telemetry_state() {
        using var tmp = new TempDir();
        TelemetryState.PathOverride    = tmp.PathTo("state", "telemetry.json");
        TelemetryDeviceId.PathOverride = tmp.PathTo("telemetry-device.json");
        TelemetryState.SetEnabled(false);

        var id = TelemetryDeviceId.GetOrCreate();

        await Assert.That(id).IsNotNull();
    }

    // Closes a documented gap: the spec justifies the device id living in its own file on the
    // grounds that opt-out can delete it outright, not merely stop minting new ones.
    [Test]
    public async Task Set_enabled_false_deletes_an_existing_device_id() {
        using var tmp = new TempDir();
        TelemetryState.PathOverride    = tmp.PathTo("state", "telemetry.json");
        TelemetryDeviceId.PathOverride = tmp.PathTo("telemetry-device.json");
        var id = TelemetryDeviceId.GetOrCreate();
        await Assert.That(id).IsNotNull();

        TelemetryState.SetEnabled(false);

        await Assert.That(TelemetryDeviceId.ReadPersisted()).IsNull();
        await Assert.That(TelemetryState.PersistedEnabled()).IsFalse();
    }

    // Re-enabling must not resurrect the discarded id — GetOrCreate mints a fresh one, which is the
    // more private behaviour the spec calls for.
    [Test]
    public async Task Re_enabling_after_disable_mints_a_fresh_device_id() {
        using var tmp = new TempDir();
        TelemetryState.PathOverride    = tmp.PathTo("state", "telemetry.json");
        TelemetryDeviceId.PathOverride = tmp.PathTo("telemetry-device.json");
        var original = TelemetryDeviceId.GetOrCreate();

        TelemetryState.SetEnabled(false);
        TelemetryState.SetEnabled(true);
        var fresh = TelemetryDeviceId.GetOrCreate();

        await Assert.That(fresh).IsNotNull();
        await Assert.That(fresh).IsNotEqualTo(original);
    }

    [Test]
    public async Task Set_enabled_true_preserves_existing_device_id() {
        using var tmp = new TempDir();
        TelemetryState.PathOverride    = tmp.PathTo("state", "telemetry.json");
        TelemetryDeviceId.PathOverride = tmp.PathTo("telemetry-device.json");
        var id = TelemetryDeviceId.GetOrCreate();

        TelemetryState.SetEnabled(true);

        await Assert.That(TelemetryDeviceId.ReadPersisted()).IsEqualTo(id);
    }
}
