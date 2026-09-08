using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Capacitor.Cli.Core.PullRequests;
using Microsoft.Extensions.Time.Testing;

namespace Capacitor.Cli.Core.Tests.Unit.PullRequests;

public class PullRequestClientTests {
    [Test]
    public async Task Literal_contract_bundle_has_the_same_pinned_digest_as_the_server() {
        var bytes = await File.ReadAllBytesAsync(FixturePath);
        await Assert.That(Convert.ToHexStringLower(SHA256.HashData(bytes))).IsEqualTo(PullRequestContract.FixtureSha256);
    }

    [Test]
    public async Task Literal_responses_are_validated_through_each_typed_read_route() {
        using var fixture = JsonDocument.Parse(await File.ReadAllTextAsync(FixturePath));
        var subject = fixture.RootElement.GetProperty("subject").Deserialize(PullRequestJsonContext.Default.PullRequestSubjectDto)!;
        foreach (var example in fixture.RootElement.GetProperty("responses").EnumerateArray()) {
            using var handler = new Handler { Body = example.GetProperty("body").GetRawText() };
            using var http = new HttpClient(handler);
            using var client = new PullRequestClient(http, "https://tenant.test");
            var kind = example.GetProperty("route").GetString() switch {
                "links" => (await client.ListAsync("session", default)).Kind,
                "overview" => (await client.OverviewAsync("session", subject, default)).Kind,
                "checks" => (await client.PageAsync<PullRequestCheckDto>("session", subject, "checks", null, null, null, default)).Kind,
                "reviewers" => (await client.PageAsync<PullRequestReviewerDto>("session", subject, "reviewers", null, null, null, default)).Kind,
                "reviews" => (await client.PageAsync<PullRequestReviewDto>("session", subject, "reviews", null, null, null, default)).Kind,
                "threads" => (await client.PageAsync<PullRequestThreadDto>("session", subject, "threads", null, null, null, default)).Kind,
                "thread_comments" => (await client.PageAsync<PullRequestCommentDto>("session", subject, "thread_comments", null, null, "PRRT_thread", default)).Kind,
                _ => (await client.PageAsync<PullRequestCommentDto>("session", subject, "conversation", null, null, null, default)).Kind
            };
            await Assert.That(kind.ToString()).IsEqualTo(example.GetProperty("expected").GetString());
            await Assert.That(handler.Paths.Skip(1).All(path => path.Contains("version=1", StringComparison.Ordinal))).IsTrue();
        }
    }

    [Test]
    public async Task Discovery_requires_a_well_formed_shared_version_before_any_new_route() {
        using var fixture = JsonDocument.Parse(await File.ReadAllTextAsync(FixturePath));
        foreach (var example in fixture.RootElement.GetProperty("discovery").EnumerateArray()) {
            using var handler = new Handler { Discovery = example.GetProperty("body").GetRawText() };
            using var http = new HttpClient(handler);
            using var client = new PullRequestClient(http, "https://tenant.test");
            var discovery = await client.DiscoverAsync(false, default);
            await Assert.That(discovery.Kind.ToString()).IsEqualTo(example.GetProperty("kind").GetString());
            if (discovery.Kind != PullRequestCapabilityKind.Supported) {
                await client.ListAsync("session", default);
                await Assert.That(handler.Paths.Count).IsEqualTo(1);
            }
        }
    }

    [Test]
    public async Task Network_time_cannot_extend_a_response_access_lease() {
        using var fixture = JsonDocument.Parse(await File.ReadAllTextAsync(FixturePath));
        var subject = fixture.RootElement.GetProperty("subject").Deserialize(PullRequestJsonContext.Default.PullRequestSubjectDto)!;
        var overview = fixture.RootElement.GetProperty("responses")[1].GetProperty("body").GetRawText();
        var clock = new FakeTimeProvider();
        using var handler = new Handler { Body = overview, BeforeBody = () => clock.Advance(TimeSpan.FromSeconds(16)) };
        using var http = new HttpClient(handler);
        using var client = new PullRequestClient(http, "https://tenant.test", clock);
        var read = await client.OverviewAsync("session", subject, default);
        await Assert.That(read.Kind).IsEqualTo(PullRequestReadKind.Ready);
        await Assert.That(read.RemainingSeconds(clock)).IsEqualTo(4);
        await Assert.That(read.CanReveal(clock)).IsFalse();
    }

    [Test]
    public async Task Discovery_failures_back_off_without_turning_into_legacy_support() {
        var clock = new FakeTimeProvider();
        using var handler = new Handler { Discovery = "not-json" };
        using var http = new HttpClient(handler);
        using var client = new PullRequestClient(http, "https://tenant.test", clock);
        await client.DiscoverAsync(false, default);
        await client.DiscoverAsync(true, default);
        await Assert.That(handler.Paths.Count).IsEqualTo(1);
        clock.Advance(TimeSpan.FromSeconds(30));
        await client.DiscoverAsync(false, default);
        clock.Advance(TimeSpan.FromSeconds(59));
        await client.DiscoverAsync(true, default);
        await Assert.That(handler.Paths.Count).IsEqualTo(2);
        clock.Advance(TimeSpan.FromSeconds(1));
        await client.DiscoverAsync(false, default);
        await Assert.That(handler.Paths.Count).IsEqualTo(3);
    }

    [Test]
    public async Task A_non_JSON_gateway_failure_is_transient_and_an_off_origin_response_is_invalid() {
        using var handler = new Handler { Status = HttpStatusCode.ServiceUnavailable, Body = "gateway unavailable" };
        using var http = new HttpClient(handler);
        using var client = new PullRequestClient(http, "https://tenant.test");
        var outage = await client.ListAsync("session", default);
        await Assert.That(outage.AccessFailure).IsEqualTo("transient");
        handler.Redirect = true;
        var redirected = await client.ListAsync("session", default);
        await Assert.That(redirected.Kind).IsEqualTo(PullRequestReadKind.InvalidProtocol);
    }

    [Test]
    public async Task Legacy_links_require_an_independently_admitted_session_summary() {
        using var handler = new Handler {
            Discovery = """{"provider":"workos"}""",
            Body = """{"session_id":"session","pull_requests":[{"repo_hash":"hash","owner":"Example","repo_name":"Repo","number":7,"url":"https://github.com/Example/Repo/pull/7"}]}"""
        };
        using var http = new HttpClient(handler);
        using var client = new PullRequestClient(http, "https://tenant.test");
        var read = await client.LegacyLinksAsync("session", default);
        await Assert.That(read.Kind).IsEqualTo(PullRequestReadKind.Ready);
        await Assert.That(read.Data!.Items[0].Owner).IsEqualTo("example");
        await Assert.That(read.AccessValidForSeconds).IsEqualTo(0);
        await Assert.That(handler.Paths.Last()).IsEqualTo("/api/sessions/session/summary");
        handler.Status = HttpStatusCode.Forbidden;
        var denied = await client.LegacyLinksAsync("session", default);
        await Assert.That(denied.Data).IsNull();
        await Assert.That(denied.AccessFailure).IsEqualTo("invalid");
    }

    [Test]
    [Arguments("null")]
    [Arguments("[null]")]
    public async Task Malformed_legacy_rows_are_rejected(string rows) {
        using var handler = new Handler { Discovery = """{"provider":"workos"}""", Body = "{\"session_id\":\"session\",\"pull_requests\":" + rows + "}" };
        using var http = new HttpClient(handler);
        using var client = new PullRequestClient(http, "https://tenant.test");
        await Assert.That((await client.LegacyLinksAsync("session", default)).Kind).IsEqualTo(PullRequestReadKind.InvalidProtocol);
    }

    static string FixturePath => Path.Combine(AppContext.BaseDirectory, "fixtures", "pull-request-reads-v1.json");
    sealed class Handler : HttpMessageHandler {
        internal string Discovery = """{"provider":"workos","pull_request_reads_versions":[1]}""";
        internal string Body = "{}";
        internal HttpStatusCode Status = HttpStatusCode.OK;
        internal Action? BeforeBody;
        internal bool Redirect;
        internal List<string> Paths { get; } = [];
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) {
            Paths.Add(request.RequestUri!.PathAndQuery);
            var discovery = request.RequestUri.AbsolutePath == "/auth/config";
            if (!discovery) BeforeBody?.Invoke();
            return Task.FromResult(new HttpResponseMessage(discovery ? HttpStatusCode.OK : Status) {
                RequestMessage = Redirect ? new HttpRequestMessage(HttpMethod.Get, "https://foreign.test/result") : request,
                Content = new StringContent(discovery ? Discovery : Body, Encoding.UTF8, "application/json")
            });
        }
    }
}
