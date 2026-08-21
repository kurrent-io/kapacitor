using System.Text.Json.Nodes;
using Capacitor.Cli.Commands;

namespace Capacitor.Cli.Tests.Unit.Commands;

public class McpProtocolTests {
    static JsonObject Init(string? version) {
        var p = new JsonObject();
        if (version is not null) p["protocolVersion"] = version;
        return new JsonObject { ["params"] = p };
    }
    static JsonObject Parse(string s) => (JsonObject)JsonNode.Parse(s)!;

    [Test]
    public async Task NegotiateVersion_echoes_a_supported_client_version() {
        await Assert.That(McpProtocol.NegotiateVersion(Init("2025-06-18"))).IsEqualTo("2025-06-18");
    }

    [Test]
    public async Task NegotiateVersion_falls_back_to_baseline_for_unknown_or_missing() {
        await Assert.That(McpProtocol.NegotiateVersion(Init("1999-01-01"))).IsEqualTo("2024-11-05");
        await Assert.That(McpProtocol.NegotiateVersion(Init(null))).IsEqualTo("2024-11-05");
    }

    [Test]
    public async Task NegotiateVersion_falls_back_when_params_is_not_an_object() {
        await Assert.That(McpProtocol.NegotiateVersion(new JsonObject { ["params"] = "oops" })).IsEqualTo("2024-11-05");
    }

    [Test]
    public async Task NegotiateVersion_falls_back_when_version_is_not_a_string() {
        var req = new JsonObject { ["params"] = new JsonObject { ["protocolVersion"] = 2025 } };
        await Assert.That(McpProtocol.NegotiateVersion(req)).IsEqualTo("2024-11-05");
    }

    [Test]
    public async Task ResourcesList_returns_empty_array_result() {
        var r = Parse(McpProtocol.TryHandleStandardMethod("resources/list", 7)!);
        await Assert.That((string)r["jsonrpc"]!).IsEqualTo("2.0");
        await Assert.That((int)r["id"]!).IsEqualTo(7);
        await Assert.That(r["result"]!["resources"]!.AsArray().Count).IsEqualTo(0);
    }

    [Test]
    public async Task PromptsList_returns_empty_array_result() {
        var r = Parse(McpProtocol.TryHandleStandardMethod("prompts/list", "abc")!);
        await Assert.That(r["result"]!["prompts"]!.AsArray().Count).IsEqualTo(0);
    }

    [Test]
    public async Task Ping_returns_empty_object_result() {
        var r = Parse(McpProtocol.TryHandleStandardMethod("ping", 1)!);
        await Assert.That(r["result"] is JsonObject o && o.Count == 0).IsTrue();
    }

    [Test]
    public async Task Unknown_method_returns_null() {
        await Assert.That(McpProtocol.TryHandleStandardMethod("tools/call", 1)).IsNull();
    }
}
