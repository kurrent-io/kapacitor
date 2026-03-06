using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using Kurrent.Capacitor.TestHelpers.Fixtures;

namespace kapacitor.Tests.Integration;

[ClassDataSource<KurrentDbFixture>(Shared = SharedType.PerTestSession)]
public class HookRoundTripTests(KurrentDbFixture db) {
    static readonly JsonSerializerOptions JsonOptions = new() {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower
    };

    static object MakeSessionStartPayload(string sessionId) => new {
        session_id      = sessionId,
        cwd             = "/tmp/test",
        home_dir        = "/tmp",
        model           = "claude-sonnet-4-6",
        transcript_path = "/tmp/fake.jsonl",
        source          = "startup",
        hook_event_name = "session-start"
    };

    static async Task<HttpResponseMessage> PollUntilOk(HttpClient client, string url, int maxRetries = 5) {
        HttpResponseMessage? response = null;
        for (var i = 0; i < maxRetries; i++) {
            response = await client.GetAsync(url);
            if (response.StatusCode == HttpStatusCode.OK) return response;
            await Task.Delay(500);
        }
        return response!;
    }

    [Test]
    public async Task SessionStart_CreatesSession() {
        await using var factory = new CapacitorFactory(db.ConnectionString);
        var client    = factory.CreateClient();
        var sessionId = $"test-{Guid.NewGuid():N}";

        var response = await client.PostAsJsonAsync("/hooks/session-start", MakeSessionStartPayload(sessionId), JsonOptions);
        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.OK);

        await Task.Delay(1000);
        var detail = await PollUntilOk(client, $"/api/sessions/{sessionId}/detail");

        await Assert.That(detail.StatusCode).IsEqualTo(HttpStatusCode.OK);
        var json = JsonNode.Parse(await detail.Content.ReadAsStringAsync());
        await Assert.That(json!["session_id"]?.GetValue<string>()).IsEqualTo(sessionId);
    }

    [Test]
    public async Task TranscriptBatch_StoresEvents() {
        await using var factory = new CapacitorFactory(db.ConnectionString);
        var client    = factory.CreateClient();
        var sessionId = $"test-{Guid.NewGuid():N}";

        await client.PostAsJsonAsync("/hooks/session-start", MakeSessionStartPayload(sessionId), JsonOptions);
        await Task.Delay(1000);

        var transcriptPayload = new {
            session_id   = sessionId,
            lines        = new[] {
                JsonSerializer.Serialize(new { type = "user", uuid = Guid.NewGuid().ToString(), message = new { role = "user", content = "hello" } }),
                JsonSerializer.Serialize(new { type = "assistant", uuid = Guid.NewGuid().ToString(), message = new { role = "assistant", content = "world" } })
            },
            line_numbers = new[] { 0, 1 }
        };

        var transcriptResponse = await client.PostAsJsonAsync("/hooks/transcript", transcriptPayload, JsonOptions);
        await Assert.That(transcriptResponse.StatusCode).IsEqualTo(HttpStatusCode.OK);

        var transcriptJson = JsonNode.Parse(await transcriptResponse.Content.ReadAsStringAsync());
        await Assert.That(transcriptJson?["processed"]?.GetValue<int>()).IsEqualTo(2);

        await Task.Delay(1000);
        var eventsResponse = await PollUntilOk(client, $"/api/sessions/{sessionId}/events");

        await Assert.That(eventsResponse.StatusCode).IsEqualTo(HttpStatusCode.OK);
        var events = JsonNode.Parse(await eventsResponse.Content.ReadAsStringAsync())!["events"]?.AsArray();
        await Assert.That(events).IsNotNull();
        await Assert.That(events!.Count).IsGreaterThanOrEqualTo(3);
    }
}
