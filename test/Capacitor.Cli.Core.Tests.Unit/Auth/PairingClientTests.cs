using System.Text.Json.Nodes;
using Capacitor.Cli.Core.Auth;
using WireMock.RequestBuilders;
using WireMock.ResponseBuilders;
using WireMock.Server;

namespace Capacitor.Cli.Core.Tests.Unit.Auth;

// The wire shape the tenant is written against. Nothing else checks that the header name, the body
// casing and the paths match the server, and all three are silent when wrong: a mistyped header
// reads as "wrong secret", a mistyped field as "no machine id".
public class PairingClientTests {
    const string Secret = "s3cret";

    [Test]
    public async Task MintAsync_sends_snake_case_fields_and_parses_the_response() {
        using var server = WireMockServer.Start();
        server.Given(Request.Create().WithPath("/api/pairings").UsingPost())
            .RespondWith(Response.Create().WithStatusCode(201)
                .WithBody("""
                    {"pairing_id":"p1","user_code":"7Q2F-KX9M","secret":"s3cret",
                     "expires_at":"2026-08-19T12:15:00Z","poll_interval_seconds":2,
                     "setup_url":"https://acme.kcap.ai/setup?p=p1"}
                    """)
                .WithHeader("Content-Type", "application/json"));

        using var http = new HttpClient();

        var outcome = await new PairingClient(http).MintAsync(server.Urls[0], "m-1", "nostromo", CancellationToken.None);

        await Assert.That(outcome.StatusCode).IsEqualTo(201);
        await Assert.That(outcome.Body!.PairingId).IsEqualTo("p1");
        await Assert.That(outcome.Body.UserCode).IsEqualTo("7Q2F-KX9M");
        await Assert.That(outcome.Body.Secret).IsEqualTo("s3cret");
        await Assert.That(outcome.Body.PollIntervalSeconds).IsEqualTo(2);
        await Assert.That(outcome.Body.SetupUrl).IsEqualTo("https://acme.kcap.ai/setup?p=p1");

        var body = JsonNode.Parse(
            server.FindLogEntries(Request.Create().WithPath("/api/pairings").UsingPost())[0].RequestMessage.Body!)!;

        await Assert.That(body["machine_id"]!.GetValue<string>()).IsEqualTo("m-1");
        await Assert.That(body["machine_name"]!.GetValue<string>()).IsEqualTo("nostromo");
    }

    // The oracle the whole browser flow turns on, so it has to survive the client rather than be
    // flattened into a transport failure.
    [Test]
    public async Task MintAsync_surfaces_a_404_rather_than_degrading() {
        using var server = WireMockServer.Start();
        server.Given(Request.Create().WithPath("/api/pairings").UsingPost())
            .RespondWith(Response.Create().WithStatusCode(404));

        using var http = new HttpClient();

        await Assert.That((await new PairingClient(http).MintAsync(server.Urls[0], "m-1", "n", CancellationToken.None)).StatusCode)
            .IsEqualTo(404);
    }

    // A server that answered must not be reported as one that could not be reached.
    [Test]
    public async Task MintAsync_keeps_the_status_when_the_body_is_unreadable() {
        using var server = WireMockServer.Start();
        server.Given(Request.Create().WithPath("/api/pairings").UsingPost())
            .RespondWith(Response.Create().WithStatusCode(201)
                .WithBody("not json").WithHeader("Content-Type", "application/json"));

        using var http = new HttpClient();

        var outcome = await new PairingClient(http).MintAsync(server.Urls[0], "m-1", "n", CancellationToken.None);

        await Assert.That(outcome.StatusCode).IsEqualTo(201);
        await Assert.That(outcome.Body).IsNull();
    }

    [Test]
    public async Task MintAsync_degrades_to_zero_when_the_server_is_unreachable() {
        using var http = new HttpClient { Timeout = TimeSpan.FromMilliseconds(200) };

        // Reserved for documentation and guaranteed not to route.
        var outcome = await new PairingClient(http).MintAsync("http://192.0.2.1:9", "m-1", "n", CancellationToken.None);

        await Assert.That(outcome.StatusCode).IsEqualTo(0);
    }

    [Test]
    public async Task PollAsync_authenticates_with_the_secret_header_and_parses_an_approval() {
        using var server = WireMockServer.Start();
        server.Given(Request.Create().WithPath("/api/pairings/p1/status").UsingGet())
            .RespondWith(Response.Create().WithStatusCode(200)
                .WithBody("""
                    {"status":"approved","server_url":"https://acme.kcap.ai",
                     "user":{"id":"github:4242"},"state_version":3}
                    """)
                .WithHeader("Content-Type", "application/json"));

        using var http = new HttpClient();

        var outcome = await new PairingClient(http).PollAsync(server.Urls[0], "p1", Secret, CancellationToken.None);

        await Assert.That(outcome.StatusCode).IsEqualTo(200);
        await Assert.That(outcome.Body!.Status).IsEqualTo("approved");
        await Assert.That(outcome.Body.ServerUrl).IsEqualTo("https://acme.kcap.ai");
        await Assert.That(outcome.Body.User!.Id).IsEqualTo("github:4242");
        await Assert.That(outcome.Body.StateVersion).IsEqualTo(3);

        var sent = server.FindLogEntries(Request.Create().WithPath("/api/pairings/p1/status").UsingGet())[0].RequestMessage;

        await Assert.That(sent.Headers![HttpClientExtensions.PairingSecretHeader][0]).IsEqualTo(Secret);
    }

    // A pairing id goes into the path, so it is escaped rather than concatenated — otherwise an id
    // containing a slash silently addresses a different route.
    [Test]
    public async Task PollAsync_escapes_the_pairing_id() {
        using var server = WireMockServer.Start();
        server.Given(Request.Create().WithPath("/*").UsingGet())
            .RespondWith(Response.Create().WithStatusCode(410));

        using var http = new HttpClient();

        await new PairingClient(http).PollAsync(server.Urls[0], "a/b", Secret, CancellationToken.None);

        // WireMock decodes before matching, so the raw URL is what carries the evidence.
        await Assert.That(server.LogEntries.Single().RequestMessage.Url).EndsWith("/api/pairings/a%2Fb/status");
    }

    // The server compares the bearer against the approver, so both headers have to arrive.
    [Test]
    public async Task CompleteAsync_sends_the_secret_and_the_bearer_together() {
        using var server = WireMockServer.Start();
        server.Given(Request.Create().WithPath("/api/pairings/p1/complete").UsingPost())
            .RespondWith(Response.Create().WithStatusCode(204));

        using var http = new HttpClient();

        await Assert.That(await new PairingClient(http).CompleteAsync(server.Urls[0], "p1", Secret, "tok", CancellationToken.None))
            .IsEqualTo(204);

        var sent = server.FindLogEntries(Request.Create().WithPath("/api/pairings/p1/complete").UsingPost())[0].RequestMessage;

        await Assert.That(sent.Headers![HttpClientExtensions.PairingSecretHeader][0]).IsEqualTo(Secret);
        await Assert.That(sent.Headers["Authorization"][0]).IsEqualTo("Bearer tok");
    }

    // 403 is the server disagreeing about who approved, and it has to reach the caller intact.
    [Test]
    public async Task CompleteAsync_returns_the_status_the_server_gave() {
        using var server = WireMockServer.Start();
        server.Given(Request.Create().WithPath("/api/pairings/p1/complete").UsingPost())
            .RespondWith(Response.Create().WithStatusCode(403));

        using var http = new HttpClient();

        await Assert.That(await new PairingClient(http).CompleteAsync(server.Urls[0], "p1", Secret, "tok", CancellationToken.None))
            .IsEqualTo(403);
    }
}
