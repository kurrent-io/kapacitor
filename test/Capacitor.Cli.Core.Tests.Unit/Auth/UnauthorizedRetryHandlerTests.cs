using System.Net;
using Capacitor.Cli.Core.Auth;
using Capacitor.Cli.Core.Config;

namespace Capacitor.Cli.Core.Tests.Unit.Auth;

/// <summary>
/// The 401-retry handler's contract. These exercise the real handler over a fake transport and a
/// real on-disk token store, because the failure modes that matter — attributing a 401 to the
/// wrong token, resending with a stale header, leaking the first response — all live in the
/// interaction, not in any single method.
/// </summary>
public class UnauthorizedRetryHandlerTests {
    [TempConfigRoot] public required TempConfigRoot Config { get; init; }

    [Test]
    public async Task Non_401_responses_pass_through_untouched() {
        var transport = new RecordingHandler(HttpStatusCode.OK);
        using var client = Client(transport, Token("original"));

        var response = await client.GetAsync("https://kcap.example.com/api/thing");

        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.OK);
        await Assert.That(transport.SentTokens).IsEquivalentTo(["original"]);
    }

    [Test]
    public async Task Every_request_carries_the_handlers_own_token_not_the_client_default() {
        // The client default header still holds whatever was attached at construction; after a
        // refresh only the handler knows the current token.
        var transport = new RecordingHandler(HttpStatusCode.OK);
        using var client = Client(transport, Token("handler-token"));
        client.DefaultRequestHeaders.Authorization = new("Bearer", "stale-default");

        await client.GetAsync("https://kcap.example.com/api/thing");

        await Assert.That(transport.SentTokens).IsEquivalentTo(["handler-token"]);
    }

    [Test]
    public async Task A_401_that_cannot_be_refreshed_surfaces_as_a_single_attempt() {
        // No stored token, so the refresh has nothing to work with: the caller must see the 401
        // rather than a silent extra round trip.
        var transport = new RecordingHandler(HttpStatusCode.Unauthorized);
        using var client = Client(transport, Token("original"));

        var response = await client.GetAsync("https://kcap.example.com/api/thing");

        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.Unauthorized);
        await Assert.That(transport.SentTokens.Count).IsEqualTo(1);
    }

    [Test]
    public async Task A_peer_refresh_is_adopted_and_the_request_is_retried_with_it() {
        // The store already holds a NEWER token than the one this request sent — exactly what a
        // peer process leaves behind. The retry must adopt it (and the dedup rule means no
        // rotation is attempted, so no refresh endpoint is contacted).
        await new TokenStore(Config.Root).SaveAsync("default", Token("peer-refreshed"));

        var transport = new RecordingHandler(HttpStatusCode.Unauthorized, HttpStatusCode.OK);
        using var client = Client(transport, Token("original"));

        var response = await client.GetAsync("https://kcap.example.com/api/thing");

        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.OK);
        await Assert.That(transport.SentTokens).IsEquivalentTo(["original", "peer-refreshed"]);
    }

    [Test]
    public async Task Exactly_one_extra_attempt_is_made_when_the_retry_also_fails() {
        await new TokenStore(Config.Root).SaveAsync("default", Token("peer-refreshed"));

        var transport = new RecordingHandler(HttpStatusCode.Unauthorized, HttpStatusCode.Unauthorized);
        using var client = Client(transport, Token("original"));

        var response = await client.GetAsync("https://kcap.example.com/api/thing");

        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.Unauthorized);
        await Assert.That(transport.SentTokens.Count).IsEqualTo(2);
    }

    [Test]
    public async Task The_first_401_response_is_disposed_before_the_retry() {
        // Every recovered 401 would otherwise leak its response content/connection.
        await new TokenStore(Config.Root).SaveAsync("default", Token("peer-refreshed"));

        var transport = new RecordingHandler(HttpStatusCode.Unauthorized, HttpStatusCode.OK);
        using var client = Client(transport, Token("original"));

        await client.GetAsync("https://kcap.example.com/api/thing");

        await Assert.That(transport.FirstResponseContentDisposed).IsTrue();
    }

    [Test]
    public async Task A_buffered_body_is_replayed_on_the_retry() {
        await new TokenStore(Config.Root).SaveAsync("default", Token("peer-refreshed"));

        var transport = new RecordingHandler(HttpStatusCode.Unauthorized, HttpStatusCode.OK);
        using var client = Client(transport, Token("original"));

        var response = await client.PostAsync("https://kcap.example.com/api/thing",
            new StringContent("""{"hello":"world"}"""));

        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.OK);
        await Assert.That(transport.SentBodies).IsEquivalentTo(["""{"hello":"world"}""", """{"hello":"world"}"""]);
    }

    [Test]
    public async Task A_json_body_is_replayed_on_the_retry() {
        // JsonContent re-serializes its value on every send, so it IS replayable. Excluding it
        // would silently leave every JSON-posting call site without 401 recovery.
        await new TokenStore(Config.Root).SaveAsync("default", Token("peer-refreshed"));

        var transport = new RecordingHandler(HttpStatusCode.Unauthorized, HttpStatusCode.OK);
        using var client = Client(transport, Token("original"));

        var response = await client.PostAsync("https://kcap.example.com/api/thing",
            System.Net.Http.Json.JsonContent.Create(new { hello = "world" }));

        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.OK);
        await Assert.That(transport.SentTokens).IsEquivalentTo(["original", "peer-refreshed"]);
    }

    [Test]
    public async Task A_peer_token_for_a_different_server_is_never_adopted() {
        // The handler is pinned to one server; a peer login elsewhere must not be picked up and
        // sent to it.
        await new TokenStore(Config.Root).SaveAsync("default", Token("peer-elsewhere") with { ServerUrl = "http://127.0.0.1:9" });

        var transport = new RecordingHandler(HttpStatusCode.Unauthorized, HttpStatusCode.OK);
        using var client = Client(transport, Token("original"));

        var response = await client.GetAsync("https://kcap.example.com/api/thing");

        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.Unauthorized);
        await Assert.That(transport.SentTokens).IsEquivalentTo(["original"]);
    }

    [Test]
    public async Task A_non_replayable_body_is_not_retried() {
        // Resending a consumed stream would send an empty or corrupt body; surfacing the 401 is
        // the honest outcome.
        await new TokenStore(Config.Root).SaveAsync("default", Token("peer-refreshed"));

        var transport = new RecordingHandler(HttpStatusCode.Unauthorized, HttpStatusCode.OK);
        using var client = Client(transport, Token("original"));

        var response = await client.PostAsync("https://kcap.example.com/api/thing",
            new StreamContent(new MemoryStream("body"u8.ToArray())));

        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.Unauthorized);
        await Assert.That(transport.SentTokens.Count).IsEqualTo(1);
    }

    [Test]
    public async Task Concurrent_requests_share_one_refreshed_token_without_sending_a_bogus_one() {
        // Sanity check on the shared mutable token under parallel use. The precise
        // rejected-token attribution this depends on is asserted directly against the dedup rule
        // in TokenServerBindingTests — here we only establish that concurrency doesn't produce a
        // token that was never in play, or a torn read.
        await new TokenStore(Config.Root).SaveAsync("default", Token("peer-refreshed"));

        var transport = new RecordingHandler(HttpStatusCode.Unauthorized, HttpStatusCode.OK);
        using var client = Client(transport, Token("original"));

        var responses = await Task.WhenAll(
            Enumerable.Range(0, 4).Select(_ => client.GetAsync("https://kcap.example.com/api/thing")));

        await Assert.That(transport.SentTokens.All(t => t is "original" or "peer-refreshed")).IsTrue();
        await Assert.That(responses.Count(r => r.StatusCode == HttpStatusCode.OK)).IsGreaterThan(0);
    }

    // ── Helpers ──────────────────────────────────────────────────────────────

    HttpClient Client(RecordingHandler transport, StoredTokens initial) {
        var retry = new UnauthorizedRetryHandler(Config.Root, ProfileConfig.DefaultName, initial, "https://kcap.example.com") { InnerHandler = transport };

        return new(retry);
    }

    static StoredTokens Token(string accessToken) => new() {
        AccessToken    = accessToken,
        ExpiresAt      = DateTimeOffset.UtcNow.AddHours(1),
        GitHubUsername = "alice",
        Provider       = AuthProvider.GitHubApp,
        ServerUrl      = "https://kcap.example.com"
    };

    sealed class RecordingHandler(params HttpStatusCode[] statuses) : HttpMessageHandler {
        readonly Lock _gate = new();
        int           _call;

        public List<string> SentTokens { get; } = [];
        public List<string> SentBodies { get; } = [];
        public bool         FirstResponseContentDisposed { get; private set; }

        TrackedContent? _firstContent;

        protected override async Task<HttpResponseMessage> SendAsync(
                HttpRequestMessage request, CancellationToken cancellationToken) {
            var body = request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken);

            int index;
            lock (_gate) {
                index = _call++;
                SentTokens.Add(request.Headers.Authorization?.Parameter ?? "<none>");
                if (body is not null) SentBodies.Add(body);
            }

            var status  = statuses[Math.Min(index, statuses.Length - 1)];
            var content = new TrackedContent(() => FirstResponseContentDisposed = true);

            if (index == 0) _firstContent = content;

            return new(status) { Content = index == 0 ? content : new StringContent("") };
        }

        sealed class TrackedContent(Action onDispose) : StringContent("") {
            protected override void Dispose(bool disposing) {
                if (disposing) onDispose();
                base.Dispose(disposing);
            }
        }
    }
}
