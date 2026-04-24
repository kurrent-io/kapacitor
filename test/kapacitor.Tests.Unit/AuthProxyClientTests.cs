using System.Net;
using kapacitor.Auth;
using WireMock.RequestBuilders;
using WireMock.ResponseBuilders;
using WireMock.Server;

namespace kapacitor.Tests.Unit;

public class AuthProxyClientTests {
    [Test]
    public async Task GetGitHubClientIdAsync_returns_id_on_200() {
        using var server = WireMockServer.Start();
        server.Given(Request.Create().WithPath("/config").UsingGet())
              .RespondWith(Response.Create().WithStatusCode(200)
                  .WithBody("""{"github_client_id":"Iv1.abc"}""")
                  .WithHeader("Content-Type", "application/json"));

        using var http = new HttpClient();
        var client = new AuthProxyClient(http);

        var id = await client.GetGitHubClientIdAsync(server.Urls[0]);

        await Assert.That(id).IsEqualTo("Iv1.abc");
    }

    [Test]
    public async Task GetGitHubClientIdAsync_returns_null_on_proxy_unreachable() {
        using var http = new HttpClient { Timeout = TimeSpan.FromMilliseconds(200) };
        var client = new AuthProxyClient(http);

        var id = await client.GetGitHubClientIdAsync("http://127.0.0.1:1");

        await Assert.That(id).IsNull();
    }

    [Test]
    public async Task DiscoverTenantsAsync_returns_tenants_on_200() {
        using var server = WireMockServer.Start();
        server.Given(Request.Create().WithPath("/discover-tenants").UsingPost())
              .RespondWith(Response.Create().WithStatusCode(200)
                  .WithBody("""[{"org_id":100,"org_login":"acme","origin":"https://a.example"}]""")
                  .WithHeader("Content-Type", "application/json"));

        using var http = new HttpClient();
        var client = new AuthProxyClient(http);

        var result = await client.DiscoverTenantsAsync(server.Urls[0], "gh-token");

        await Assert.That(result.Error).IsEqualTo(DiscoveryError.None);
        await Assert.That(result.Tenants.Length).IsEqualTo(1);
        await Assert.That(result.Tenants[0].OrgLogin).IsEqualTo("acme");
        await Assert.That(result.Tenants[0].OrgId).IsEqualTo(100L);
        await Assert.That(result.Tenants[0].Origin).IsEqualTo("https://a.example");
    }

    [Test]
    public async Task DiscoverTenantsAsync_returns_TokenRejected_on_401() {
        using var server = WireMockServer.Start();
        server.Given(Request.Create().WithPath("/discover-tenants").UsingPost())
              .RespondWith(Response.Create().WithStatusCode(401));

        using var http = new HttpClient();
        var client = new AuthProxyClient(http);

        var result = await client.DiscoverTenantsAsync(server.Urls[0], "gh-token");

        await Assert.That(result.Error).IsEqualTo(DiscoveryError.TokenRejected);
    }

    [Test]
    public async Task DiscoverTenantsAsync_returns_TokenRejected_on_403() {
        using var server = WireMockServer.Start();
        server.Given(Request.Create().WithPath("/discover-tenants").UsingPost())
              .RespondWith(Response.Create().WithStatusCode(403));

        using var http = new HttpClient();
        var client = new AuthProxyClient(http);

        var result = await client.DiscoverTenantsAsync(server.Urls[0], "gh-token");

        await Assert.That(result.Error).IsEqualTo(DiscoveryError.TokenRejected);
    }

    [Test]
    public async Task DiscoverTenantsAsync_returns_UpstreamError_on_502() {
        using var server = WireMockServer.Start();
        server.Given(Request.Create().WithPath("/discover-tenants").UsingPost())
              .RespondWith(Response.Create().WithStatusCode(502));

        using var http = new HttpClient();
        var client = new AuthProxyClient(http);

        var result = await client.DiscoverTenantsAsync(server.Urls[0], "gh-token");

        await Assert.That(result.Error).IsEqualTo(DiscoveryError.UpstreamError);
    }

    [Test]
    public async Task DiscoverTenantsAsync_returns_ProxyUnreachable_on_connection_refused() {
        using var http = new HttpClient { Timeout = TimeSpan.FromMilliseconds(200) };
        var client = new AuthProxyClient(http);

        var result = await client.DiscoverTenantsAsync("http://127.0.0.1:1", "gh-token");

        await Assert.That(result.Error).IsEqualTo(DiscoveryError.ProxyUnreachable);
    }
}
