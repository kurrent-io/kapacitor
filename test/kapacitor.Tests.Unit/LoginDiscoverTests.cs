using kapacitor.Auth;

namespace kapacitor.Tests.Unit;

/// <summary>
/// Tests for OAuthLoginFlow.ExchangeAndSaveAsync(string, string, string, string) —
/// the named-profile overload used by `kapacitor login --discover`.
///
/// Uses the shared KAPACITOR_CONFIG_DIR temp directory set by RepoPathStoreGlobalSetup
/// so token files land in an isolated directory, not ~/.config/kapacitor.
///
/// Shares the TokenStoreProfileTests NotInParallel key so both classes serialize
/// access to the shared tokens directory and don't race each other.
/// </summary>
[NotInParallel(nameof(TokenStoreProfileTests))]
public class LoginDiscoverTests {
    static string TokensDir => PathHelpers.ConfigPath("tokens");

    [Before(Test)]
    public void Cleanup() {
        try {
            if (Directory.Exists(TokensDir)) Directory.Delete(TokensDir, recursive: true);
        } catch {
            // Best-effort: if deletion fails, individual tests create isolated profile files
        }
    }

    [Test]
    public async Task Exchange_writes_token_to_named_profile() {
        using var tenant = WireMock.Server.WireMockServer.Start();
        tenant.Given(WireMock.RequestBuilders.Request.Create().WithPath("/auth/token").UsingPost())
              .RespondWith(WireMock.ResponseBuilders.Response.Create().WithStatusCode(200)
                  .WithBody("""{"access_token":"capacitor-jwt","expires_in":3600,"username":"alice"}""")
                  .WithHeader("Content-Type", "application/json"));

        var exit = await OAuthLoginFlow.ExchangeAndSaveAsync(tenant.Urls[0], "gh-token", AuthProvider.GitHubApp, "acme");

        await Assert.That(exit).IsEqualTo(0);
        var stored = await TokenStore.LoadAsync("acme");
        await Assert.That(stored).IsNotNull();
        await Assert.That(stored!.AccessToken).IsEqualTo("capacitor-jwt");
    }

    [Test]
    public async Task Exchange_sets_correct_username_and_provider() {
        using var tenant = WireMock.Server.WireMockServer.Start();
        tenant.Given(WireMock.RequestBuilders.Request.Create().WithPath("/auth/token").UsingPost())
              .RespondWith(WireMock.ResponseBuilders.Response.Create().WithStatusCode(200)
                  .WithBody("""{"access_token":"tok","expires_in":7200,"username":"bob"}""")
                  .WithHeader("Content-Type", "application/json"));

        await OAuthLoginFlow.ExchangeAndSaveAsync(tenant.Urls[0], "gh-token", AuthProvider.GitHubApp, "contoso");

        var stored = await TokenStore.LoadAsync("contoso");
        await Assert.That(stored).IsNotNull();
        await Assert.That(stored!.GitHubUsername).IsEqualTo("bob");
        await Assert.That(stored.Provider).IsEqualTo(AuthProvider.GitHubApp);
    }

    [Test]
    public async Task Exchange_rejects_unknown_provider() {
        using var tenant = WireMock.Server.WireMockServer.Start();
        var exit = await OAuthLoginFlow.ExchangeAndSaveAsync(tenant.Urls[0], "gh-token", "NonsenseProvider", "acme");
        await Assert.That(exit).IsEqualTo(1);
    }

    [Test]
    public async Task Exchange_returns_one_on_server_error() {
        using var tenant = WireMock.Server.WireMockServer.Start();
        tenant.Given(WireMock.RequestBuilders.Request.Create().WithPath("/auth/token").UsingPost())
              .RespondWith(WireMock.ResponseBuilders.Response.Create().WithStatusCode(401)
                  .WithBody("Unauthorized"));

        var exit = await OAuthLoginFlow.ExchangeAndSaveAsync(tenant.Urls[0], "bad-token", AuthProvider.GitHubApp, "acme");
        await Assert.That(exit).IsEqualTo(1);
    }

    [Test]
    public async Task Exchange_writes_independent_tokens_for_multiple_profiles() {
        using var acmeTenant    = WireMock.Server.WireMockServer.Start();
        using var contosoTenant = WireMock.Server.WireMockServer.Start();

        acmeTenant.Given(WireMock.RequestBuilders.Request.Create().WithPath("/auth/token").UsingPost())
                  .RespondWith(WireMock.ResponseBuilders.Response.Create().WithStatusCode(200)
                      .WithBody("""{"access_token":"acme-jwt","expires_in":3600,"username":"alice"}""")
                      .WithHeader("Content-Type", "application/json"));

        contosoTenant.Given(WireMock.RequestBuilders.Request.Create().WithPath("/auth/token").UsingPost())
                     .RespondWith(WireMock.ResponseBuilders.Response.Create().WithStatusCode(200)
                         .WithBody("""{"access_token":"contoso-jwt","expires_in":3600,"username":"bob"}""")
                         .WithHeader("Content-Type", "application/json"));

        await OAuthLoginFlow.ExchangeAndSaveAsync(acmeTenant.Urls[0],    "gh-token", AuthProvider.GitHubApp, "acme");
        await OAuthLoginFlow.ExchangeAndSaveAsync(contosoTenant.Urls[0], "gh-token", AuthProvider.GitHubApp, "contoso");

        var acme    = await TokenStore.LoadAsync("acme");
        var contoso = await TokenStore.LoadAsync("contoso");

        await Assert.That(acme!.AccessToken).IsEqualTo("acme-jwt");
        await Assert.That(contoso!.AccessToken).IsEqualTo("contoso-jwt");
    }
}
