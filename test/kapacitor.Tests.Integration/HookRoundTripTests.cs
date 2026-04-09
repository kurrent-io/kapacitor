using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using WireMock.RequestBuilders;
using WireMock.ResponseBuilders;
using WireMock.Server;

namespace kapacitor.Tests.Integration;

/// <summary>
/// Validates the CLI's HTTP contract with the Capacitor server.
/// Uses WireMock to stub server endpoints and verify request shapes.
/// For full end-to-end tests against the real server, see the private repo.
/// </summary>
public class HookRoundTripTests : IDisposable {
    static readonly JsonSerializerOptions JsonOptions = new() {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower
    };

    readonly WireMockServer _server = WireMockServer.Start();

    public void Dispose() => _server.Stop();

    [Test]
    public async Task SessionStart_SendsCorrectPayload() {
        _server.Given(Request.Create().WithPath("/hooks/session-start").UsingPost())
            .RespondWith(Response.Create().WithStatusCode(200).WithBody("{}"));

        using var client    = new HttpClient();
        var       sessionId = $"test-{Guid.NewGuid():N}";

        var payload = new {
            session_id      = sessionId,
            cwd             = "/tmp/test",
            home_dir        = "/tmp",
            model           = "claude-sonnet-4-6",
            transcript_path = "/tmp/fake.jsonl",
            source          = "startup",
            hook_event_name = "session-start"
        };

        var response = await client.PostAsJsonAsync($"{_server.Url}/hooks/session-start", payload, JsonOptions);

        await Assert.That((int)response.StatusCode).IsEqualTo(200);

        var requests = _server.FindLogEntries(Request.Create().WithPath("/hooks/session-start").UsingPost());
        await Assert.That(requests.Count).IsEqualTo(1);

        var body = JsonNode.Parse(requests[0].RequestMessage.Body!)!;
        await Assert.That(body["session_id"]?.GetValue<string>()).IsEqualTo(sessionId);
        await Assert.That(body["cwd"]?.GetValue<string>()).IsEqualTo("/tmp/test");
        await Assert.That(body["model"]?.GetValue<string>()).IsEqualTo("claude-sonnet-4-6");
    }

    [Test]
    public async Task TranscriptBatch_SendsCorrectShape() {
        _server.Given(Request.Create().WithPath("/hooks/transcript").UsingPost())
            .RespondWith(
                Response.Create()
                    .WithStatusCode(200)
                    .WithHeader("Content-Type", "application/json")
                    .WithBody("""{"processed": 2}""")
            );

        using var client    = new HttpClient();
        var       sessionId = $"test-{Guid.NewGuid():N}";

        var payload = new {
            session_id = sessionId,
            lines = new[] {
                JsonSerializer.Serialize(new { type = "user", uuid      = Guid.NewGuid().ToString(), message = new { role = "user", content      = "hello" } }),
                JsonSerializer.Serialize(new { type = "assistant", uuid = Guid.NewGuid().ToString(), message = new { role = "assistant", content = "world" } })
            },
            line_numbers = new[] { 0, 1 }
        };

        var response = await client.PostAsJsonAsync($"{_server.Url}/hooks/transcript", payload, JsonOptions);

        await Assert.That((int)response.StatusCode).IsEqualTo(200);

        var responseJson = JsonNode.Parse(await response.Content.ReadAsStringAsync());
        await Assert.That(responseJson?["processed"]?.GetValue<int>()).IsEqualTo(2);

        var requests = _server.FindLogEntries(Request.Create().WithPath("/hooks/transcript").UsingPost());
        await Assert.That(requests.Count).IsEqualTo(1);

        var body = JsonDocument.Parse(requests[0].RequestMessage.Body!).RootElement;
        await Assert.That(body.GetProperty("session_id").GetString()).IsEqualTo(sessionId);
        await Assert.That(body.GetProperty("lines").GetArrayLength()).IsEqualTo(2);
        await Assert.That(body.GetProperty("line_numbers")[0].GetInt32()).IsEqualTo(0);
        await Assert.That(body.GetProperty("line_numbers")[1].GetInt32()).IsEqualTo(1);
    }
}
