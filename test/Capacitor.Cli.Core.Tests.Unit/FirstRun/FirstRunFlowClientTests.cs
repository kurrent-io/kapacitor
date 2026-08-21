using System.Text.Json.Nodes;
using Capacitor.Cli.Core.FirstRun;
using WireMock.RequestBuilders;
using WireMock.ResponseBuilders;
using WireMock.Server;

namespace Capacitor.Cli.Core.Tests.Unit.FirstRun;

// The wire shape the tenant is written against. Nothing else checks it — the flow's own tests run
// over a fake channel, which is the client this replaces — and every way of getting it wrong is
// silent: a mistyped path reads as "this tenant does not offer browser setup", and a mistyped field
// as a malformed request the CLI reports as the server's fault.
public class FirstRunFlowClientTests {
    const string FlowId = "b7f3a1c2d4e5f607a1b2c3";

    const string CreatePath = "/api/first-run/flows";

    static string PollPath => $"{CreatePath}/{FlowId}";

    static string StateBody(string doneStatus) =>
        $$$"""
          {"flow_id":"{{{FlowId}}}","machine":"nostromo","step":"Done","can_finish":true,
           "steps":{"SignIn":"Completed","Agents":"Completed","Import":"Skipped","Done":"{{{doneStatus}}}"}}
          """;

    [Test]
    public async Task CreateAsync_sends_snake_case_fields_and_parses_the_response() {
        using var server = WireMockServer.Start();
        server.Given(Request.Create().WithPath(CreatePath).UsingPost())
            .RespondWith(Response.Create().WithStatusCode(200)
                .WithBody(StateBody("Pending"))
                .WithHeader("Content-Type", "application/json"));

        using var http = new HttpClient();

        var outcome = await new FirstRunFlowClient(http)
            .CreateAsync(server.Urls[0], FlowId, "nostromo", CancellationToken.None);

        await Assert.That(outcome.StatusCode).IsEqualTo(200);
        await Assert.That(outcome.Body!.FlowId).IsEqualTo(FlowId);
        await Assert.That(outcome.Body.Machine).IsEqualTo("nostromo");
        await Assert.That(outcome.Body.CanFinish).IsTrue();
        await Assert.That(outcome.Body.Steps!["Import"]).IsEqualTo("Skipped");

        var body = JsonNode.Parse(
            server.FindLogEntries(Request.Create().WithPath(CreatePath).UsingPost())[0].RequestMessage.Body!)!;

        // Case-insensitive matching does NOT bridge an underscore, so a camelCase field here would
        // arrive as a null flow_id and be refused as malformed on every single create.
        await Assert.That(body["flow_id"]!.GetValue<string>()).IsEqualTo(FlowId);
        await Assert.That(body["machine"]!.GetValue<string>()).IsEqualTo("nostromo");
    }

    [Test]
    public async Task CreateAsync_reads_the_retry_after_a_429_carries() {
        using var server = WireMockServer.Start();
        server.Given(Request.Create().WithPath(CreatePath).UsingPost())
            .RespondWith(Response.Create().WithStatusCode(429).WithHeader("Retry-After", "600"));

        using var http = new HttpClient();

        var outcome = await new FirstRunFlowClient(http)
            .CreateAsync(server.Urls[0], FlowId, null, CancellationToken.None);

        await Assert.That(outcome.StatusCode).IsEqualTo(429);
        await Assert.That(outcome.RetryAfter).IsEqualTo(TimeSpan.FromMinutes(10));
    }

    // The availability oracle for the whole leg, so it has to survive the client rather than be
    // flattened into a transport failure.
    [Test]
    public async Task CreateAsync_surfaces_a_404_rather_than_degrading() {
        using var server = WireMockServer.Start();
        server.Given(Request.Create().WithPath(CreatePath).UsingPost())
            .RespondWith(Response.Create().WithStatusCode(404));

        using var http = new HttpClient();

        var outcome = await new FirstRunFlowClient(http)
            .CreateAsync(server.Urls[0], FlowId, null, CancellationToken.None);

        await Assert.That(outcome.StatusCode).IsEqualTo(404);
    }

    [Test]
    public async Task CreateAsync_tolerates_a_server_url_with_a_trailing_slash() {
        using var server = WireMockServer.Start();
        server.Given(Request.Create().WithPath(CreatePath).UsingPost())
            .RespondWith(Response.Create().WithStatusCode(200)
                .WithBody(StateBody("Pending")).WithHeader("Content-Type", "application/json"));

        using var http = new HttpClient();

        var outcome = await new FirstRunFlowClient(http)
            .CreateAsync($"{server.Urls[0]}/", FlowId, null, CancellationToken.None);

        await Assert.That(outcome.StatusCode).IsEqualTo(200);
    }

    [Test]
    public async Task PollAsync_reads_the_flow_state() {
        using var server = WireMockServer.Start();
        server.Given(Request.Create().WithPath(PollPath).UsingGet())
            .RespondWith(Response.Create().WithStatusCode(200)
                .WithBody(StateBody("Completed")).WithHeader("Content-Type", "application/json"));

        using var http = new HttpClient();

        var outcome = await new FirstRunFlowClient(http).PollAsync(server.Urls[0], FlowId, CancellationToken.None);

        await Assert.That(outcome.StatusCode).IsEqualTo(200);
        await Assert.That(FirstRunFlowOutcomes.IsFinished(outcome.Body!)).IsTrue();
    }

    [Test]
    [Arguments(404)]
    [Arguments(410)]
    public async Task PollAsync_surfaces_a_refusal_with_no_body(int status) {
        using var server = WireMockServer.Start();
        server.Given(Request.Create().WithPath(PollPath).UsingGet())
            .RespondWith(Response.Create().WithStatusCode(status).WithBody("""{"error":"flow_expired"}"""));

        using var http = new HttpClient();

        var outcome = await new FirstRunFlowClient(http).PollAsync(server.Urls[0], FlowId, CancellationToken.None);

        await Assert.That(outcome.StatusCode).IsEqualTo(status);
        await Assert.That(outcome.Body).IsNull();
    }

    // A server that answered must not be reported as one that could not be reached: status 0 sends
    // the flow down the "could not reach the server" branch, about a server that just replied.
    [Test]
    public async Task PollAsync_reports_an_unreadable_200_as_200() {
        using var server = WireMockServer.Start();
        server.Given(Request.Create().WithPath(PollPath).UsingGet())
            .RespondWith(Response.Create().WithStatusCode(200)
                .WithBody("not json").WithHeader("Content-Type", "application/json"));

        using var http = new HttpClient();

        var outcome = await new FirstRunFlowClient(http).PollAsync(server.Urls[0], FlowId, CancellationToken.None);

        await Assert.That(outcome.StatusCode).IsEqualTo(200);
        await Assert.That(outcome.Body).IsNull();
    }

    [Test]
    public async Task Degrades_to_status_0_when_the_server_is_unreachable() {
        using var http = new HttpClient { Timeout = TimeSpan.FromMilliseconds(250) };

        // Reserved as unroutable by RFC 5737, so this fails to connect rather than reaching anything.
        var outcome = await new FirstRunFlowClient(http)
            .PollAsync("http://192.0.2.1:9", FlowId, CancellationToken.None);

        await Assert.That(outcome.StatusCode).IsEqualTo(0);
    }
}
