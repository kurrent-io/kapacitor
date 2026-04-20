using System.Text.Json;
using System.Text.Json.Nodes;
using kapacitor.Commands;
using WireMock.RequestBuilders;
using WireMock.ResponseBuilders;
using WireMock.Server;

namespace kapacitor.Tests.Unit;

public class McpJudgeServerTests : IDisposable {
    readonly WireMockServer _server = WireMockServer.Start();

    public void Dispose() => _server.Stop();

    [Test]
    public async Task get_session_recap_forwards_session_id_and_chain_flag() {
        _server.Given(Request.Create()
                .WithPath("/api/sessions/abc-123/recap")
                .WithParam("chain", "true")
                .UsingGet())
            .RespondWith(Response.Create()
                .WithStatusCode(200)
                .WithBody("""[{"summary":"did a thing"}]"""));

        using var http = new HttpClient();

        var result = await McpJudgeServer.HandleToolCallForTests(
            toolName: "get_session_recap",
            arguments: new JsonObject { ["session_id"] = "abc-123" },
            client: http,
            baseUrl: _server.Url!,
            expectedSessionId: "abc-123"
        );

        using var doc = JsonDocument.Parse(result);

        await Assert.That(
                doc.RootElement.GetProperty("result")
                    .GetProperty("content")[0]
                    .GetProperty("text")
                    .GetString()
            )
            .IsEqualTo("""[{"summary":"did a thing"}]""");
    }

    [Test]
    public async Task get_session_recap_rejects_mismatched_session_id() {
        using var http = new HttpClient();

        var result = await McpJudgeServer.HandleToolCallForTests(
            toolName: "get_session_recap",
            arguments: new JsonObject { ["session_id"] = "other-session" },
            client: http,
            baseUrl: _server.Url!,
            expectedSessionId: "abc-123"
        );

        using var doc = JsonDocument.Parse(result);
        var content = doc.RootElement.GetProperty("result");

        await Assert.That(content.GetProperty("isError").GetBoolean()).IsTrue();
        await Assert.That(content.GetProperty("content")[0].GetProperty("text").GetString())
            .Contains("does not match this judge's bound session");
    }

    [Test]
    public async Task get_session_recap_errors_when_session_id_missing() {
        using var http = new HttpClient();

        var result = await McpJudgeServer.HandleToolCallForTests(
            toolName: "get_session_recap",
            arguments: new JsonObject(),
            client: http,
            baseUrl: _server.Url!,
            expectedSessionId: "abc-123"
        );

        using var doc = JsonDocument.Parse(result);
        var content = doc.RootElement.GetProperty("result");

        await Assert.That(content.GetProperty("isError").GetBoolean()).IsTrue();
        await Assert.That(content.GetProperty("content")[0].GetProperty("text").GetString())
            .Contains("session_id");
    }
}
