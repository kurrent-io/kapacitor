using Capacitor.Cli.Core.Auth;
using WireMock.RequestBuilders;
using WireMock.ResponseBuilders;
using WireMock.Server;

namespace Capacitor.Cli.Core.Tests.Unit.Auth;

public class AuthProxyClientTests {
    [Test]
    public async Task GetConfigAsync_returns_client_id_only_when_exchange_url_absent() {
        using var server = WireMockServer.Start();

        server.Given(Request.Create().WithPath("/config").UsingGet())
            .RespondWith(
                Response.Create()
                    .WithStatusCode(200)
                    .WithBody("""{"github_client_id":"Iv1.abc"}""")
                    .WithHeader("Content-Type", "application/json")
            );

        using var http   = new HttpClient();
        var       client = new AuthProxyClient(http);

        var config = await client.GetConfigAsync(server.Urls[0]);

        await Assert.That(config).IsNotNull();
        await Assert.That(config!.GitHubClientId).IsEqualTo("Iv1.abc");
        await Assert.That(config.GitHubCodeExchangeUrl).IsNull();
    }

    [Test]
    public async Task GetConfigAsync_returns_exchange_url_when_present() {
        using var server = WireMockServer.Start();

        server.Given(Request.Create().WithPath("/config").UsingGet())
            .RespondWith(
                Response.Create()
                    .WithStatusCode(200)
                    .WithBody("""{"github_client_id":"Iv1.abc","github_code_exchange_url":"https://auth.example/auth/github/code-exchange"}""")
                    .WithHeader("Content-Type", "application/json")
            );

        using var http   = new HttpClient();
        var       client = new AuthProxyClient(http);

        var config = await client.GetConfigAsync(server.Urls[0]);

        await Assert.That(config).IsNotNull();
        await Assert.That(config!.GitHubClientId).IsEqualTo("Iv1.abc");
        await Assert.That(config.GitHubCodeExchangeUrl).IsEqualTo("https://auth.example/auth/github/code-exchange");
    }

    [Test]
    public async Task GetConfigAsync_reads_workos_fields() {
        using var server = WireMockServer.Start();

        server.Given(Request.Create().WithPath("/config").UsingGet())
            .RespondWith(
                Response.Create()
                    .WithStatusCode(200)
                    .WithBody("""{"github_client_id":"gh","workos_client_id":"client_d","workos_authkit_domain":""}""")
                    .WithHeader("Content-Type", "application/json")
            );

        using var http   = new HttpClient();
        var       client = new AuthProxyClient(http);

        var config = await client.GetConfigAsync(server.Urls[0]);

        await Assert.That(config!.WorkOSClientId).IsEqualTo("client_d");
        await Assert.That(config.WorkOSAuthKitDomain).IsEqualTo("");
    }

    [Test]
    public async Task GetConfigAsync_returns_null_on_proxy_unreachable() {
        using var http = new HttpClient();
        http.Timeout = TimeSpan.FromMilliseconds(200);
        var client = new AuthProxyClient(http);

        var config = await client.GetConfigAsync("http://127.0.0.1:1");

        await Assert.That(config).IsNull();
    }

    [Test]
    public async Task DiscoverTenantsAsync_returns_tenants_on_200() {
        using var server = WireMockServer.Start();

        server.Given(Request.Create().WithPath("/discover-tenants").UsingPost())
            .RespondWith(
                Response.Create()
                    .WithStatusCode(200)
                    .WithBody("""[{"org_id":100,"org_login":"acme","origin":"https://a.example"}]""")
                    .WithHeader("Content-Type", "application/json")
            );

        using var http   = new HttpClient();
        var       client = new AuthProxyClient(http);

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

        using var http   = new HttpClient();
        var       client = new AuthProxyClient(http);

        var result = await client.DiscoverTenantsAsync(server.Urls[0], "gh-token");

        await Assert.That(result.Error).IsEqualTo(DiscoveryError.TokenRejected);
    }

    [Test]
    public async Task DiscoverTenantsAsync_returns_TokenRejected_on_403() {
        using var server = WireMockServer.Start();

        server.Given(Request.Create().WithPath("/discover-tenants").UsingPost())
            .RespondWith(Response.Create().WithStatusCode(403));

        using var http   = new HttpClient();
        var       client = new AuthProxyClient(http);

        var result = await client.DiscoverTenantsAsync(server.Urls[0], "gh-token");

        await Assert.That(result.Error).IsEqualTo(DiscoveryError.TokenRejected);
    }

    [Test]
    public async Task DiscoverTenantsAsync_returns_UpstreamError_on_502() {
        using var server = WireMockServer.Start();

        server.Given(Request.Create().WithPath("/discover-tenants").UsingPost())
            .RespondWith(Response.Create().WithStatusCode(502));

        using var http   = new HttpClient();
        var       client = new AuthProxyClient(http);

        var result = await client.DiscoverTenantsAsync(server.Urls[0], "gh-token");

        await Assert.That(result.Error).IsEqualTo(DiscoveryError.UpstreamError);
    }

    [Test]
    public async Task DiscoverTenantsAsync_returns_ProxyUnreachable_on_connection_refused() {
        using var http = new HttpClient();
        http.Timeout = TimeSpan.FromMilliseconds(200);
        var client = new AuthProxyClient(http);

        var result = await client.DiscoverTenantsAsync("http://127.0.0.1:1", "gh-token");

        await Assert.That(result.Error).IsEqualTo(DiscoveryError.ProxyUnreachable);
    }

    [Test]
    public async Task DiscoverWorkOSTenantsAsync_parses_provider_aware_rows() {
        using var server = WireMockServer.Start();

        server.Given(Request.Create().WithPath("/discover-tenants-workos").UsingPost())
            .RespondWith(
                Response.Create()
                    .WithStatusCode(200)
                    .WithBody("""[{"provider":"WorkOS","organization_id":"org_a","slug":"eventuous","display_name":"Eventuous","origin":"https://eventuous.kcap.ai"}]""")
                    .WithHeader("Content-Type", "application/json")
            );

        using var http   = new HttpClient();
        var       client = new AuthProxyClient(http);

        var result = await client.DiscoverWorkOSTenantsAsync(server.Urls[0], "wos.tok.en");

        await Assert.That(result.Error).IsEqualTo(DiscoveryError.None);
        await Assert.That(result.Tenants[0].OrganizationId).IsEqualTo("org_a");
        await Assert.That(result.Tenants[0].Slug).IsEqualTo("eventuous");
        await Assert.That(result.Tenants[0].Origin).IsEqualTo("https://eventuous.kcap.ai");
    }

    [Test]
    public async Task DiscoverWorkOSTenantsAsync_maps_401_to_TokenRejected() {
        using var server = WireMockServer.Start();

        server.Given(Request.Create().WithPath("/discover-tenants-workos").UsingPost())
            .RespondWith(Response.Create().WithStatusCode(401));

        using var http   = new HttpClient();
        var       client = new AuthProxyClient(http);

        var result = await client.DiscoverWorkOSTenantsAsync(server.Urls[0], "bad");

        await Assert.That(result.Error).IsEqualTo(DiscoveryError.TokenRejected);
    }
}
