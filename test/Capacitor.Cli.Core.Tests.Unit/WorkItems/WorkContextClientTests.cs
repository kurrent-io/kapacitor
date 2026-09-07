using Capacitor.Cli.Core.WorkItems;
using WireMock.RequestBuilders;
using WireMock.ResponseBuilders;
using WireMock.Server;

namespace Capacitor.Cli.Core.Tests.Unit.WorkItems;

/// The routes and the id containment: a session id is canonicalized, validated and escaped before it
/// enters a path, a work item id validated and escaped, and neither can turn into a dot segment.
public class WorkContextClientTests {
    const string Dashed   = "01234567-89ab-cdef-0123-456789abcdef";
    const string Dashless = "0123456789abcdef0123456789abcdef";

    sealed class ThrowingHandler(Exception exception) : HttpMessageHandler {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) =>
            Task.FromException<HttpResponseMessage>(exception);
    }

    sealed class CancellingHandler : HttpMessageHandler {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) {
            await Task.Delay(Timeout.InfiniteTimeSpan, ct);
            throw new InvalidOperationException("unreachable");
        }
    }

    static WireMockServer Serve(string path, int status, string body) {
        var server = WireMockServer.Start();
        server.Given(Request.Create().WithPath(path).UsingGet())
            .RespondWith(Response.Create().WithStatusCode(status).WithBody(body).WithHeader("Content-Type", "application/json"));
        return server;
    }

    [Test]
    public async Task Assignments_route_strips_dashes_and_parses_the_rows() {
        using var server = Serve($"/api/work-items/session/{Dashless}", 200, """[{"work_item_id":"w1","label":"WK-1 — t","source":"mcp","confidence":1,"is_primary":true}]""");
        using var http = new HttpClient();

        var outcome = await new WorkContextClient(http, server.Urls[0] + "/").GetSessionAssignmentsAsync(Dashed, CancellationToken.None);

        await Assert.That(outcome.StatusCode).IsEqualTo(200);
        await Assert.That(outcome.Succeeded).IsTrue();
        await Assert.That(outcome.Body![0].WorkItemId).IsEqualTo("w1");
        await Assert.That(server.LogEntries.Single().RequestMessage.Path).IsEqualTo($"/api/work-items/session/{Dashless}");
    }

    [Test]
    public async Task Topology_and_summary_routes_hit_their_paths() {
        using var server = WireMockServer.Start();
        server.Given(Request.Create().WithPath("/api/work-items/w1/topology").UsingGet())
            .RespondWith(Response.Create().WithStatusCode(200).WithBody("""{"parts":[],"part_of":null,"blocks":[],"blocked_by":[],"cycle":"none","item":{"work_item_id":"w1","title":"T"}}""").WithHeader("Content-Type", "application/json"));
        server.Given(Request.Create().WithPath($"/api/sessions/{Dashless}/summary").UsingGet())
            .RespondWith(Response.Create().WithStatusCode(200).WithBody("""{"session_id":"s1","repositories":[],"pull_requests":[]}""").WithHeader("Content-Type", "application/json"));
        using var http = new HttpClient();
        var client = new WorkContextClient(http, server.Urls[0]);

        var topology = await client.GetTopologyAsync(" w1 ", CancellationToken.None);
        var summary = await client.GetSessionSummaryAsync(Dashed, CancellationToken.None);

        await Assert.That(topology.Body!.Item!.Title).IsEqualTo("T");
        await Assert.That(summary.Body!.SessionId).IsEqualTo("s1");
    }

    [Test]
    public async Task The_work_item_route_hits_its_path_and_parses_the_body() {
        using var server = Serve("/api/work-items/w1", 200, """{"work_item_id":"w1","title":"T","enriched_title":null,"overview":null,"is_overview_mechanical":false,"key":null,"state":{"kind":"in_flight","settled_at":null},"links":[],"parts":[],"contributors":[],"session_count":1}""");
        using var http = new HttpClient();

        var item = await new WorkContextClient(http, server.Urls[0]).GetWorkItemAsync(" w1 ", CancellationToken.None);

        await Assert.That(item.Succeeded).IsTrue();
        await Assert.That(item.Body!.Title).IsEqualTo("T");
        await Assert.That(item.Body.State!.Kind).IsEqualTo("in_flight");
        await Assert.That(server.LogEntries.Single().RequestMessage.Path).IsEqualTo("/api/work-items/w1");
    }

    [Test]
    [Arguments(".")]
    [Arguments("..")]
    [Arguments("")]
    [Arguments("   ")]
    [Arguments("---")]
    public async Task An_id_that_would_escape_or_empty_the_route_is_refused_before_any_request(string id) {
        using var http = new HttpClient(new ThrowingHandler(new InvalidOperationException("a request was sent")));
        var client = new WorkContextClient(http, "http://localhost:1");

        var assignments = await client.GetSessionAssignmentsAsync(id, CancellationToken.None);
        var summary = await client.GetSessionSummaryAsync(id, CancellationToken.None);
        var topology = await client.GetTopologyAsync(id == "---" ? "." : id, CancellationToken.None);
        var item = await client.GetWorkItemAsync(id == "---" ? ".." : id, CancellationToken.None);

        await Assert.That(assignments.StatusCode).IsEqualTo(0);
        await Assert.That(summary.StatusCode).IsEqualTo(0);
        await Assert.That(topology.StatusCode).IsEqualTo(0);
        await Assert.That(item.StatusCode).IsEqualTo(0);
    }

    [Test]
    public async Task Ids_with_a_slash_or_percent_are_escaped_into_one_segment() {
        using var server = WireMockServer.Start();
        server.Given(Request.Create().UsingGet())
            .RespondWith(Response.Create().WithStatusCode(200).WithBody("[]").WithHeader("Content-Type", "application/json"));
        using var http = new HttpClient();
        var client = new WorkContextClient(http, server.Urls[0]);

        await client.GetSessionAssignmentsAsync("a/b%25c", CancellationToken.None);
        await client.GetTopologyAsync("x/y", CancellationToken.None);
        await client.GetWorkItemAsync("x/y", CancellationToken.None);

        var urls = server.LogEntries.Select(e => e.RequestMessage.AbsoluteUrl).ToList();
        // AbsoluteUrl is a System.Uri round-tripped through ToString(), which keeps %2F (the escaped
        // slash) but unescapes %25 back to a literal '%' — so a raw '%' in the id shows up once-escaped
        // here even though EscapeDataString produced %25 on the wire.
        await Assert.That(urls[0]).EndsWith("/api/work-items/session/a%2Fb%25c");
        await Assert.That(urls[1]).EndsWith("/api/work-items/x%2Fy/topology");
        await Assert.That(urls[2]).EndsWith("/api/work-items/x%2Fy");
    }

    [Test]
    public async Task A_4xx_body_in_the_error_shape_becomes_the_outcome_error() {
        using var server = Serve($"/api/work-items/session/{Dashless}", 403, """{"error":"work_items_not_in_plan","message":"Upgrade."}""");
        using var http = new HttpClient();

        var outcome = await new WorkContextClient(http, server.Urls[0]).GetSessionAssignmentsAsync(Dashless, CancellationToken.None);

        await Assert.That(outcome.StatusCode).IsEqualTo(403);
        await Assert.That(outcome.Body).IsNull();
        await Assert.That(outcome.Error!.Error).IsEqualTo("work_items_not_in_plan");
    }

    [Test]
    public async Task An_unparseable_2xx_body_keeps_the_status_with_a_null_body() {
        using var server = Serve($"/api/work-items/session/{Dashless}", 200, "not json");
        using var http = new HttpClient();

        var outcome = await new WorkContextClient(http, server.Urls[0]).GetSessionAssignmentsAsync(Dashless, CancellationToken.None);

        await Assert.That(outcome.StatusCode).IsEqualTo(200);
        await Assert.That(outcome.Body).IsNull();
        await Assert.That(outcome.Succeeded).IsFalse();
    }

    [Test]
    public async Task A_transport_failure_is_status_zero() {
        using var http = new HttpClient(new ThrowingHandler(new HttpRequestException("refused")));

        var outcome = await new WorkContextClient(http, "http://localhost:1").GetSessionAssignmentsAsync(Dashless, CancellationToken.None);

        await Assert.That(outcome.StatusCode).IsEqualTo(0);
    }

    [Test]
    public async Task The_callers_own_cancellation_propagates() {
        using var http = new HttpClient(new CancellingHandler());
        using var cts = new CancellationTokenSource();
        var pending = new WorkContextClient(http, "http://localhost:1").GetSessionAssignmentsAsync(Dashless, cts.Token);

        cts.Cancel();

        await Assert.ThrowsAsync<OperationCanceledException>(async () => await pending);
    }
}
