using Capacitor.Cli.Commands;
using WireMock.RequestBuilders;
using WireMock.ResponseBuilders;
using WireMock.Server;

namespace Capacitor.Cli.Tests.Integration;

public class ImportPrivateTests : IDisposable {
    readonly WireMockServer _server  = WireMockServer.Start();
    readonly TempDir        _tmp     = new();

    public void Dispose() {
        _server.Stop();
        _tmp.Dispose();
    }

    [Test]
    public async Task SetVisibilityNoneForAll_calls_PUT_for_each_session_id() {
        _server.Given(Request.Create().WithPath("/api/sessions/*/visibility").UsingPut())
            .RespondWith(Response.Create().WithStatusCode(200));

        using var client = new HttpClient();

        await ImportCommand.SetVisibilityNoneForAll(
            client,
            _server.Url!,
            ["sess1", "sess2", "sess3"]);

        var requests = _server.LogEntries
            .Where(e => e.RequestMessage.Method == "PUT")
            .OrderBy(e => e.RequestMessage.Path)
            .ToArray();

        await Assert.That(requests.Select(r => r.RequestMessage.Path).ToArray()).IsEquivalentTo(
        [
            "/api/sessions/sess1/visibility",
            "/api/sessions/sess2/visibility",
            "/api/sessions/sess3/visibility"
        ]
        );

        foreach (var r in requests) {
            await Assert.That(r.RequestMessage.Body).IsEqualTo("""{"visibility":"none"}""");
        }
    }

    [Test]
    public async Task SetVisibilityNoneForAll_continues_on_per_session_failure() {
        _server.Given(Request.Create().WithPath("/api/sessions/sess2/visibility").UsingPut())
            .RespondWith(Response.Create().WithStatusCode(500));
        _server.Given(Request.Create().WithPath("/api/sessions/sess*/visibility").UsingPut())
            .RespondWith(Response.Create().WithStatusCode(200));

        using var client = new HttpClient();

        // Should not throw even though sess2 returns 500.
        await ImportCommand.SetVisibilityNoneForAll(
            client,
            _server.Url!,
            ["sess1", "sess2", "sess3"]);

        var attempted = _server.LogEntries
            .Count(e => e.RequestMessage.Method == "PUT");

        await Assert.That(attempted).IsEqualTo(3);
    }
}

