using Capacitor.Cli.Commands;
using Capacitor.Cli.Core.Config;   // Profile, for the not-a-profile-key guard
using Capacitor.Cli.Core.Telemetry;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;

namespace Capacitor.Cli.Tests.Unit.Telemetry;

[NotInParallel(nameof(TelemetryState) + "." + nameof(TelemetryState.PathOverride))]
public class ConfigTelemetryKeyTests {
    static void FreshState() =>
        TelemetryState.PathOverride =
            Path.Combine(Path.GetTempPath(), $"kcap-cfg-{Guid.NewGuid():N}", "telemetry.json");

    [Test]
    [Arguments("off")]
    [Arguments("false")]
    [Arguments("0")]
    [Arguments("no")]
    public async Task Telemetry_off_persists_disabled(string value) {
        FreshState();

        await Assert.That(ConfigCommand.TryApplyTelemetry("telemetry", value)).IsTrue();
        await Assert.That(TelemetryState.PersistedEnabled()).IsEqualTo((bool?)false);
    }

    [Test]
    [Arguments("on")]
    [Arguments("true")]
    [Arguments("1")]
    [Arguments("yes")]
    public async Task Telemetry_on_persists_enabled(string value) {
        FreshState();

        await Assert.That(ConfigCommand.TryApplyTelemetry("telemetry", value)).IsTrue();
        await Assert.That(TelemetryState.PersistedEnabled()).IsEqualTo((bool?)true);
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
