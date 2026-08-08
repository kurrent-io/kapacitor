using Capacitor.Cli.Core.Telemetry;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;

namespace Capacitor.Cli.Tests.Unit.Telemetry;

// Shares the TelemetryState.PathOverride lock key with CliTelemetryTests/TelemetryStateTests
// (Task 2's convention): keying on the resource, not the class, so any test class touching this
// shared static serialises against every other one.
[NotInParallel(nameof(TelemetryState) + "." + nameof(TelemetryState.PathOverride))]
public class McpTelemetryTests {
    static List<TelemetryEvent> StartCapturing() {
        TelemetryState.PathOverride =
            Path.Combine(Path.GetTempPath(), $"kcap-mcp-{Guid.NewGuid():N}", "telemetry.json");
        var sink = new List<TelemetryEvent>();
        CliTelemetry.TestSink = sink;
        CliTelemetry.Initialize("mcp-server", null, loggedIn: false);
        sink.Clear();

        return sink;
    }

    [Test]
    public async Task Tool_call_records_server_tool_and_outcome() {
        var sink = StartCapturing();

        McpTelemetry.ToolCalled("kcap-memory", "search_memories", ok: true, durationMs: 120);

        var e = sink.Single();
        await Assert.That(e.Name).IsEqualTo("mcp_tool_called");
        await Assert.That(e.Properties["server"]!.GetValue<string>()).IsEqualTo("kcap-memory");
        await Assert.That(e.Properties["tool"]!.GetValue<string>()).IsEqualTo("search_memories");
        await Assert.That(e.Properties["ok"]!.GetValue<bool>()).IsTrue();
        await Assert.That(e.Properties["duration_ms"]!.GetValue<long>()).IsEqualTo(120L);
    }

    [Test]
    public async Task Failed_tool_call_is_recorded_as_not_ok() {
        var sink = StartCapturing();

        McpTelemetry.ToolCalled("kcap-sessions", "get_turn", ok: false, durationMs: 5);

        await Assert.That(sink.Single().Properties["ok"]!.GetValue<bool>()).IsFalse();
    }

    // Tool arguments can contain repo paths, prompts, and session ids.
    [Test]
    public async Task No_argument_data_is_carried() {
        var sink = StartCapturing();

        McpTelemetry.ToolCalled("kcap-memory", "save_memory", ok: true, durationMs: 1);

        var keys = sink.Single().Properties.Select(p => p.Key).ToArray();
        await Assert.That(keys.Contains("arguments")).IsFalse();
        await Assert.That(keys.Contains("params")).IsFalse();
        await Assert.That(keys.Contains("input")).IsFalse();
    }
}
