using Capacitor.Cli.Core.Telemetry;

namespace Capacitor.Cli.Core.Tests.Unit.Telemetry;

[NotInParallel(nameof(CliTelemetry) + "." + nameof(CliTelemetry.TestSink))]
public class SpawnMarkerTests {
    [TempConfigRoot] public required TempConfigRoot Config { get; init; }

    [Test]
    public async Task Consume_removes_marker_and_reports_presence() {
        CliTelemetry.Reset();
        var env = new Dictionary<string, string?> { [CliTelemetry.SpawnNoTelemetryVar] = "1" };
        var suppressed = CliTelemetry.ConsumeSpawnMarker(k => env.GetValueOrDefault(k), k => env.Remove(k));

        await Assert.That(suppressed).IsTrue();
        await Assert.That(env.ContainsKey(CliTelemetry.SpawnNoTelemetryVar)).IsFalse();
    }

    [Test]
    public async Task Consume_without_marker_is_inert() {
        CliTelemetry.Reset();
        var env = new Dictionary<string, string?>();
        var suppressed = CliTelemetry.ConsumeSpawnMarker(k => env.GetValueOrDefault(k), k => env.Remove(k));

        await Assert.That(suppressed).IsFalse();
    }

    [Test]
    public async Task Marker_does_not_touch_users_own_KCAP_TELEMETRY() {
        CliTelemetry.Reset();
        var env = new Dictionary<string, string?> {
            [CliTelemetry.SpawnNoTelemetryVar] = "1",
            ["KCAP_TELEMETRY"] = "1",
        };
        CliTelemetry.ConsumeSpawnMarker(k => env.GetValueOrDefault(k), k => env.Remove(k));

        await Assert.That(env["KCAP_TELEMETRY"]).IsEqualTo("1");
    }

    [Test]
    public async Task Initialize_with_suppressed_true_keeps_telemetry_disabled() {
        CliTelemetry.Reset();
        CliTelemetry.TestSink = new();

        CliTelemetry.Initialize("login", null, false, Config.Root, suppressed: true);

        await Assert.That(CliTelemetry.Enabled).IsFalse();
        await Assert.That(CliTelemetry.TestSink).Count().IsEqualTo(0);
    }

    [Test]
    public async Task Suppression_is_sticky_across_multiple_initialize_calls() {
        CliTelemetry.Reset();
        CliTelemetry.TestSink = new();

        // First init: consume marker and suppress
        var env = new Dictionary<string, string?> { [CliTelemetry.SpawnNoTelemetryVar] = "1" };
        var consumed = CliTelemetry.ConsumeSpawnMarker(k => env.GetValueOrDefault(k), k => env.Remove(k));
        await Assert.That(consumed).IsTrue();

        // First initialize call with suppression
        CliTelemetry.Initialize("mcp", null, false, Config.Root, suppressed: true);
        await Assert.That(CliTelemetry.Enabled).IsFalse();

        // Second initialize call WITHOUT suppressed parameter (like MCP handlers do)
        // This mimics McpWorkItemsServer.cs:34 calling Initialize("mcp-server", baseUrl, loggedIn)
        CliTelemetry.Initialize("mcp-server", null, false, Config.Root);

        // Telemetry must still be suppressed (sticky suppression prevents re-enabling)
        await Assert.That(CliTelemetry.Enabled).IsFalse();
        await Assert.That(CliTelemetry.TestSink).Count().IsEqualTo(0);
    }
}
