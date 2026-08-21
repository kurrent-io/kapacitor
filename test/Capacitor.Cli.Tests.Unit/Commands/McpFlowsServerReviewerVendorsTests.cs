using System.Text.Json.Nodes;
using Capacitor.Cli.Commands;
using Capacitor.Cli.Core;
using WireMock.RequestBuilders;
using WireMock.ResponseBuilders;
using WireMock.Server;

namespace Capacitor.Cli.Tests.Unit.Commands;

public class McpFlowsServerReviewerVendorsTests {
    static JsonObject ToolCall() => new() {
        ["params"] = new JsonObject { ["name"] = "list_reviewer_vendors", ["arguments"] = new JsonObject() }
    };

    // The tool payload is carried as the single content item's text (an MCP tool result).
    static JsonNode ResultJson(string response)
        => JsonNode.Parse(JsonNode.Parse(response)!["result"]!["content"]![0]!["text"]!.GetValue<string>())!;

    [Test]
    public async Task Lists_repo_hosting_reviewers_for_this_machine() {
        var machine = MachineId.Get(); // the tool matches the daemon on the requester's own machine id
        using var server = WireMockServer.Start();
        server.Given(Request.Create().WithPath("/api/daemons").UsingGet())
            .RespondWith(Response.Create().WithStatusCode(200).WithBody($$"""
            [{"name":"d1","repoPaths":["/repo/a"],"machineId":"{{machine}}",
              "supportedVendors":["codex","claude"],"unattendedVendors":["codex","claude"]}]
            """));
        using var client = new HttpClient();

        var response = await McpFlowsServer.HandleToolCallAsync(
            JsonNode.Parse("1")!, ToolCall(), client, server.Url!,
            cwd: "/repo/a", repoRoot: "/repo/a", repoInfo: null, driverVendor: "claude");

        await Assert.That(JsonNode.Parse(response)!["result"]!["isError"]).IsNull();
        var parsed = ResultJson(response);
        await Assert.That(parsed["reviewers"]!.AsArray().Count).IsEqualTo(2);
        await Assert.That(parsed["driver_vendor"]!.GetValue<string>()).IsEqualTo("claude");
        // reason lives under diagnostics; it is omitted (null) when reviewers are present.
        await Assert.That(parsed["diagnostics"]!["reason"]).IsNull();
    }

    [Test]
    public async Task Server_error_maps_to_lookup_failed() {
        using var server = WireMockServer.Start();
        server.Given(Request.Create().WithPath("/api/daemons").UsingGet())
            .RespondWith(Response.Create().WithStatusCode(500));
        using var client = new HttpClient();

        var response = await McpFlowsServer.HandleToolCallAsync(
            JsonNode.Parse("1")!, ToolCall(), client, server.Url!,
            cwd: "/r", repoRoot: "/r", repoInfo: null, driverVendor: null);

        var parsed = ResultJson(response);
        await Assert.That(parsed["diagnostics"]!["reason"]!.GetValue<string>()).IsEqualTo("lookup_failed");
        await Assert.That(parsed["reviewers"]!.AsArray().Count).IsEqualTo(0);
    }

    [Test]
    public async Task Repo_unresolved_when_no_repo_root() {
        using var server = WireMockServer.Start();
        server.Given(Request.Create().WithPath("/api/daemons").UsingGet())
            .RespondWith(Response.Create().WithStatusCode(200).WithBody("[]"));
        using var client = new HttpClient();

        var response = await McpFlowsServer.HandleToolCallAsync(
            JsonNode.Parse("1")!, ToolCall(), client, server.Url!,
            cwd: "/x", repoRoot: null, repoInfo: null, driverVendor: null);

        var parsed = ResultJson(response);
        await Assert.That(parsed["diagnostics"]!["reason"]!.GetValue<string>()).IsEqualTo("repo_unresolved");
    }
}
