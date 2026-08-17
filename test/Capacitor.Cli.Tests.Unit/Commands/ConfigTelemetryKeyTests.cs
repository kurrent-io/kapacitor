using Capacitor.Cli.Commands;
using Capacitor.Cli.Core.Config;
using Capacitor.Cli.Core.Telemetry;

namespace Capacitor.Cli.Tests.Unit.Commands;

[NotInParallel([
    nameof(TelemetryState) + "." + nameof(TelemetryState.PathOverride),
    nameof(TelemetryDeviceId) + "." + nameof(TelemetryDeviceId.PathOverride),
])]
public class ConfigTelemetryKeyTests : IDisposable {
    readonly TempDir _tmp = new();

    public void Dispose() {
        TelemetryState.PathOverride    = null;
        TelemetryDeviceId.PathOverride = null;
        _tmp.Dispose();
    }

    // Also points TelemetryDeviceId at a fresh, colocated file: TryApplyTelemetry("telemetry",
    // "off") -> TelemetryState.SetEnabled(false) deletes the device id file as a side effect, so
    // leaving that static unset here would reach outside this test's own temp dir.
    void FreshState() {
        TelemetryState.PathOverride    = _tmp.PathTo("telemetry.json");
        TelemetryDeviceId.PathOverride = _tmp.PathTo("telemetry-device.json");
    }

    [Test]
    [Arguments("off")]
    [Arguments("false")]
    [Arguments("0")]
    [Arguments("no")]
    public async Task Telemetry_off_persists_disabled(string value) {
        FreshState();

        await Assert.That(ConfigCommand.TryApplyTelemetry("telemetry", value)).IsTrue();
        await Assert.That(TelemetryState.PersistedEnabled()).IsFalse();
    }

    [Test]
    [Arguments("on")]
    [Arguments("true")]
    [Arguments("1")]
    [Arguments("yes")]
    public async Task Telemetry_on_persists_enabled(string value) {
        FreshState();

        await Assert.That(ConfigCommand.TryApplyTelemetry("telemetry", value)).IsTrue();
        await Assert.That(TelemetryState.PersistedEnabled()).IsTrue();
    }

    [Test]
    public async Task Other_keys_are_not_claimed() {
        FreshState();

        await Assert.That(ConfigCommand.TryApplyTelemetry("server_url", "https://acme.kcap.ai")).IsFalse();
    }

    [Test]
    public async Task Invalid_telemetry_value_throws_with_an_actionable_message() {
        FreshState();

        var ex = Assert.Throws<ArgumentException>(() => ConfigCommand.TryApplyTelemetry("telemetry", "banana"));

        await Assert.That(ex!.Message.Contains("on")).IsTrue();
        await Assert.That(ex.Message.Contains("off")).IsTrue();
    }

    // Machine-scoped, so it must not have been written into the active profile.
    [Test]
    public async Task Telemetry_is_not_a_profile_key() {
        var ex = Assert.Throws<ArgumentException>(() => ConfigCommand.ApplySet(new Profile(), "telemetry", "off"));

        await Assert.That(ex!.Message.Contains("Unknown config key")).IsTrue();
    }
}
