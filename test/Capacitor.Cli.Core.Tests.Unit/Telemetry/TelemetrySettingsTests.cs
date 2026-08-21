using Capacitor.Cli.Core.Telemetry;

namespace Capacitor.Cli.Core.Tests.Unit.Telemetry;

public class TelemetrySettingsTests {
    static TelemetryDecision Resolve(bool? persisted = null, params (string Key, string? Value)[] env) =>
        TelemetrySettings.Resolve(env.ToDictionary(e => e.Key, e => e.Value), persisted);

    [Test]
    public async Task Enabled_by_default() {
        await Assert.That(Resolve().Enabled).IsTrue();
    }

    [Test]
    [Arguments("0")]
    [Arguments("off")]
    [Arguments("false")]
    [Arguments("no")]
    [Arguments("OFF")]
    public async Task Kcap_telemetry_disables(string value) {
        await Assert.That(Resolve(null, ("KCAP_TELEMETRY", value)).Enabled).IsFalse();
    }

    [Test]
    [Arguments("1")]
    [Arguments("on")]
    [Arguments("true")]
    [Arguments("yes")]
    public async Task Kcap_telemetry_enables(string value) {
        await Assert.That(Resolve(null, ("KCAP_TELEMETRY", value)).Enabled).IsTrue();
    }

    [Test]
    public async Task Do_not_track_disables() {
        await Assert.That(Resolve(null, ("DO_NOT_TRACK", "1")).Enabled).IsFalse();
    }

    [Test]
    public async Task Do_not_track_zero_does_not_disable() {
        await Assert.That(Resolve(null, ("DO_NOT_TRACK", "0")).Enabled).IsTrue();
    }

    // Documented precedence: the kcap-specific variable is the deliberate, more specific
    // statement and is the only way to opt back in on a machine with a blanket DO_NOT_TRACK.
    [Test]
    public async Task Kcap_telemetry_outranks_do_not_track_in_both_directions() {
        await Assert.That(Resolve(null, ("DO_NOT_TRACK", "1"), ("KCAP_TELEMETRY", "1")).Enabled).IsTrue();
        await Assert.That(Resolve(null, ("DO_NOT_TRACK", "0"), ("KCAP_TELEMETRY", "0")).Enabled).IsFalse();
    }

    [Test]
    public async Task Persisted_flag_applies_when_no_env_override() {
        await Assert.That(Resolve(persisted: false).Enabled).IsFalse();
        await Assert.That(Resolve(persisted: true).Enabled).IsTrue();
    }

    [Test]
    public async Task Env_outranks_persisted_flag() {
        await Assert.That(Resolve(persisted: false, ("KCAP_TELEMETRY", "1")).Enabled).IsTrue();
        await Assert.That(Resolve(persisted: true, ("DO_NOT_TRACK", "1")).Enabled).IsFalse();
    }

    [Test]
    public async Task Blank_env_values_are_ignored() {
        await Assert.That(Resolve(null, ("KCAP_TELEMETRY", ""), ("DO_NOT_TRACK", "")).Enabled).IsTrue();
    }

    [Test]
    public async Task Unparseable_kcap_telemetry_falls_through_to_default() {
        await Assert.That(Resolve(null, ("KCAP_TELEMETRY", "banana")).Enabled).IsTrue();
    }

    [Test]
    public async Task Reason_names_the_winning_source() {
        await Assert.That(Resolve(null, ("DO_NOT_TRACK", "1")).Reason).IsEqualTo("DO_NOT_TRACK");
        await Assert.That(Resolve(null, ("KCAP_TELEMETRY", "0")).Reason).IsEqualTo("KCAP_TELEMETRY");
        await Assert.That(Resolve(persisted: false).Reason).IsEqualTo("config");
        await Assert.That(Resolve().Reason).IsEqualTo("default");
    }
}
