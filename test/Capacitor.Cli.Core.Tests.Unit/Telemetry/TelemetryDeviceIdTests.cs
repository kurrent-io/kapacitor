using Capacitor.Cli.Core.Telemetry;

namespace Capacitor.Cli.Core.Tests.Unit.Telemetry;

/// <summary>
/// Tests for <see cref="TelemetryDeviceId"/> — the anonymous device id, split out of
/// <c>telemetry.json</c> into its own lock-free file (see that type's doc comment for why). Mirrors
/// <c>MachineIdFileTests</c>' shape, since the on-disk pattern is lifted from <see cref="Capacitor.Cli.Core.MachineId"/>.
///
/// A few tests here (the SetEnabled/re-enable ones) are inherently cross-cutting: they exercise
/// <see cref="TelemetryState.SetEnabled"/>'s documented side effect of deleting the device id file,
/// so state and device id have to share one root — which is what they share in production too. See
/// TelemetryStateTests for the sibling half of that split.
/// </summary>
public class TelemetryDeviceIdTests {
    [TempConfigRoot] public required TempConfigRoot Config { get; init; }

    [Test]
    public async Task Read_of_missing_file_is_null() {
        await Assert.That(TelemetryDeviceId.ReadPersisted(Config.Root)).IsNull();
    }

    [Test]
    public async Task Device_id_is_created_once_and_is_stable() {
        var first  = TelemetryDeviceId.GetOrCreate(Config.Root);
        var second = TelemetryDeviceId.GetOrCreate(Config.Root);

        await Assert.That(first).IsNotNull();
        await Assert.That(second).IsEqualTo(first);
        await Assert.That(TelemetryDeviceId.ReadPersisted(Config.Root)).IsEqualTo(first);
    }

    [Test]
    public async Task Device_id_is_a_bare_guid_with_no_hyphens() {
        var id = TelemetryDeviceId.GetOrCreate(Config.Root)!;

        await Assert.That(id.Length).IsEqualTo(32);
        await Assert.That(id.Contains('-')).IsFalse();
    }

    [Test]
    public async Task Second_get_or_create_does_not_rewrite_file_when_id_exists() {
        var path = Config.PathTo("telemetry-device.json");

        // First call creates the ID and writes the file.
        TelemetryDeviceId.GetOrCreate(Config.Root);
        await Task.Delay(10);   // ensure timestamp granularity

        var timestampAfterFirstCall = File.GetLastWriteTimeUtc(path);
        await Task.Delay(10);   // ensure time passes before second call

        // Second call should return the same ID without rewriting.
        TelemetryDeviceId.GetOrCreate(Config.Root);

        var timestampAfterSecondCall = File.GetLastWriteTimeUtc(path);

        await Assert.That(timestampAfterSecondCall).IsEqualTo(timestampAfterFirstCall);
    }

    [Test]
    public async Task Corrupt_file_heals_on_get_or_create() {
        Config.CreateFile("telemetry-device.json", "{ not json");

        var first  = TelemetryDeviceId.GetOrCreate(Config.Root);
        var second = TelemetryDeviceId.GetOrCreate(Config.Root);

        await Assert.That(first).IsNotNull();
        await Assert.That(second).IsEqualTo(first);
        await Assert.That(TelemetryDeviceId.ReadPersisted(Config.Root)).IsEqualTo(first);
    }

    // Unlike the old coupled telemetry.json, GetOrCreate has no notion of "enabled" at all — the
    // file it owns doesn't carry that field. This pins the property that used to require a separate
    // precedence rule (an earlier revision of the coupled GetOrCreateDeviceId re-checked
    // state.Enabled itself, which broke KCAP_TELEMETRY=1 overriding a persisted opt-out): calling it
    // directly always mints/returns an id, full stop.
    [Test]
    public async Task Get_or_create_is_unaffected_by_telemetry_state() {
        TelemetryState.SetEnabled(false, Config.Root);

        var id = TelemetryDeviceId.GetOrCreate(Config.Root);

        await Assert.That(id).IsNotNull();
    }

    // Closes a documented gap: the spec justifies the device id living in its own file on the
    // grounds that opt-out can delete it outright, not merely stop minting new ones.
    [Test]
    public async Task Set_enabled_false_deletes_an_existing_device_id() {
        var id = TelemetryDeviceId.GetOrCreate(Config.Root);
        await Assert.That(id).IsNotNull();

        TelemetryState.SetEnabled(false, Config.Root);

        await Assert.That(TelemetryDeviceId.ReadPersisted(Config.Root)).IsNull();
        await Assert.That(TelemetryState.PersistedEnabled(Config.Root)).IsFalse();
    }

    // Re-enabling must not resurrect the discarded id — GetOrCreate mints a fresh one, which is the
    // more private behaviour the spec calls for.
    [Test]
    public async Task Re_enabling_after_disable_mints_a_fresh_device_id() {
        var original = TelemetryDeviceId.GetOrCreate(Config.Root);

        TelemetryState.SetEnabled(false, Config.Root);
        TelemetryState.SetEnabled(true, Config.Root);
        var fresh = TelemetryDeviceId.GetOrCreate(Config.Root);

        await Assert.That(fresh).IsNotNull();
        await Assert.That(fresh).IsNotEqualTo(original);
    }

    [Test]
    public async Task Set_enabled_true_preserves_existing_device_id() {
        var id = TelemetryDeviceId.GetOrCreate(Config.Root);

        TelemetryState.SetEnabled(true, Config.Root);

        await Assert.That(TelemetryDeviceId.ReadPersisted(Config.Root)).IsEqualTo(id);
    }
}
