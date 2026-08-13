using Capacitor.Cli.Core.Telemetry;

namespace Capacitor.Cli.Tests.Unit.Telemetry;

public class SpawnMarkerTests {
    [Test]
    public async Task Consume_removes_marker_and_reports_presence() {
        var env = new Dictionary<string, string?> { [CliTelemetry.SpawnNoTelemetryVar] = "1" };
        var suppressed = CliTelemetry.ConsumeSpawnMarker(k => env.GetValueOrDefault(k), k => env.Remove(k));

        await Assert.That(suppressed).IsTrue();
        await Assert.That(env.ContainsKey(CliTelemetry.SpawnNoTelemetryVar)).IsFalse();
    }

    [Test]
    public async Task Consume_without_marker_is_inert() {
        var env = new Dictionary<string, string?>();
        var suppressed = CliTelemetry.ConsumeSpawnMarker(k => env.GetValueOrDefault(k), k => env.Remove(k));

        await Assert.That(suppressed).IsFalse();
    }

    [Test]
    public async Task Marker_does_not_touch_users_own_KCAP_TELEMETRY() {
        var env = new Dictionary<string, string?> {
            [CliTelemetry.SpawnNoTelemetryVar] = "1",
            ["KCAP_TELEMETRY"] = "1",
        };
        CliTelemetry.ConsumeSpawnMarker(k => env.GetValueOrDefault(k), k => env.Remove(k));

        await Assert.That(env["KCAP_TELEMETRY"]).IsEqualTo("1");
    }
}
