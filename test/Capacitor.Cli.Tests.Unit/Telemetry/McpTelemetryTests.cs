using System.Text.Json.Nodes;
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
    // CliTelemetry holds process-global static state (Enabled, TestSink, ...). A prior test
    // elsewhere in the suite (e.g. one that persists `telemetry off`) can leave Enabled=false
    // behind via CliTelemetry.DiscardAndDisable — reset before touching TestSink so every test
    // here starts from pristine state rather than inheriting whatever ran before it.
    [Before(Test)]
    public void ResetTelemetry() => CliTelemetry.Reset();

    static List<TelemetryEvent> StartCapturing() {
        TelemetryState.PathOverride =
            Path.Combine(Path.GetTempPath(), $"kcap-mcp-{Guid.NewGuid():N}", "telemetry.json");
        var sink = new List<TelemetryEvent>();
        CliTelemetry.TestSink = sink;
        CliTelemetry.Initialize("mcp-server", null, loggedIn: false);

        // This fired once on Ubuntu CI as an opaque "Sequence contains no elements" from a
        // downstream Single(), and the cause was never pinned down. Enabled can be false for
        // three distinct reasons, so report which one rather than guessing again: the resolver
        // said no (env var or persisted flag), the command isn't reportable, or the device-id
        // write failed and Initialize bailed. Whatever recurs, the message should name it.
        if (!CliTelemetry.Enabled) {
            var decision = TelemetrySettings.Resolve(TelemetryState.PersistedEnabled());
            throw new InvalidOperationException(
                $"CliTelemetry did not enable. resolver={decision.Enabled} (reason={decision.Reason}), "
              + $"reportable={CommandEvents.IsReportable("mcp-server")}, "
              + $"deviceId={(TelemetryState.Read().Id is null ? "null" : "present")}, "
              + $"path={TelemetryState.PathOverride}. "
              + "If resolver=True and reportable=True, the device-id write failed; otherwise state "
              + "leaked from an earlier test and this helper's Reset() is not covering it.");
        }

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

    // Tool arguments can contain repo paths, prompts, and session ids. An earlier draft of this
    // test checked the absence of three literal key names (arguments/params/input), which would
    // still pass against an implementation that leaked argument data under any OTHER key.
    // Inverted to an allowlist so ANY unexpected key fails the test, regardless of its name.
    [Test]
    public async Task No_argument_data_is_carried() {
        var sink = StartCapturing();

        McpTelemetry.ToolCalled("kcap-memory", "save_memory", ok: true, durationMs: 1);

        // The four event-specific properties, plus every shared property CliTelemetry.Capture
        // merges in (see CliTelemetry.Initialize's `_shared` object) — nothing else may appear.
        var allowed = new HashSet<string> {
            "server", "tool", "ok", "duration_ms",
            "source", "cli_version", "os", "arch", "is_ci", "is_headless", "has_server", "logged_in"
        };

        var keys = sink.Single().Properties.Select(p => p.Key).ToArray();
        await Assert.That(keys.All(allowed.Contains)).IsTrue();
    }

    [Test]
    public async Task SafeToolName_returns_unknown_when_params_is_missing() {
        var request = new JsonObject();

        await Assert.That(McpTelemetry.SafeToolName(request)).IsEqualTo("unknown");
    }

    [Test]
    public async Task SafeToolName_returns_unknown_when_params_is_not_an_object() {
        var request = new JsonObject { ["params"] = "not-an-object" };

        await Assert.That(McpTelemetry.SafeToolName(request)).IsEqualTo("unknown");
    }

    [Test]
    public async Task SafeToolName_returns_unknown_when_name_is_missing() {
        var request = new JsonObject { ["params"] = new JsonObject() };

        await Assert.That(McpTelemetry.SafeToolName(request)).IsEqualTo("unknown");
    }

    [Test]
    public async Task SafeToolName_returns_unknown_when_name_is_not_a_string() {
        var request = new JsonObject { ["params"] = new JsonObject { ["name"] = 123 } };

        await Assert.That(McpTelemetry.SafeToolName(request)).IsEqualTo("unknown");
    }

    // ── ResponseOk: every kcap MCP server's tools/call dispatch catches its own exceptions and
    // returns isError:true rather than throwing, so "dispatch returned a string" says nothing
    // about success — this is what the wrapper reads instead. ───────────────────────────────────

    [Test]
    public async Task ResponseOk_is_false_when_the_result_carries_isError_true() {
        var response = """{"jsonrpc":"2.0","id":1,"result":{"content":[{"type":"text","text":"boom"}],"isError":true}}""";

        await Assert.That(McpTelemetry.ResponseOk(response)).IsFalse();
    }

    [Test]
    public async Task ResponseOk_is_true_when_isError_is_absent() {
        // The real wire shape on success: BuildToolResult's `isError ? true : null` combined with
        // DefaultIgnoreCondition.WhenWritingNull means a successful call omits the key entirely.
        var response = """{"jsonrpc":"2.0","id":1,"result":{"content":[{"type":"text","text":"ok"}]}}""";

        await Assert.That(McpTelemetry.ResponseOk(response)).IsTrue();
    }

    [Test]
    public async Task ResponseOk_is_true_when_isError_is_explicitly_false() {
        var response = """{"jsonrpc":"2.0","id":1,"result":{"content":[],"isError":false}}""";

        await Assert.That(McpTelemetry.ResponseOk(response)).IsTrue();
    }

    [Test]
    public async Task ResponseOk_never_throws_on_malformed_json() {
        await Assert.That(McpTelemetry.ResponseOk("not json at all")).IsTrue();
        await Assert.That(McpTelemetry.ResponseOk("")).IsTrue();
        await Assert.That(McpTelemetry.ResponseOk("""{"result":"not-an-object"}""")).IsTrue();
        await Assert.That(McpTelemetry.ResponseOk("""{"result":{"isError":"not-a-bool"}}""")).IsTrue();
    }

    [Test]
    public async Task ResponseOk_is_true_for_a_jsonrpc_error_envelope() {
        // A protocol-level error (bad method, malformed request) is a different failure shape
        // entirely — {"error":...}, no "result" at all — distinct from the tool-level
        // isError:true this helper exists to read. Not this helper's concern either way: it
        // returns true (no isError:true found), same as any other shape lacking that flag.
        var response = """{"jsonrpc":"2.0","id":1,"error":{"code":-32601,"message":"Method not found"}}""";

        await Assert.That(McpTelemetry.ResponseOk(response)).IsTrue();
    }
}
