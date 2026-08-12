using Capacitor.Cli.Core.Auth;
using Duende.IdentityModel.OidcClient;
using Duende.IdentityModel.OidcClient.Browser;
using WireMock.RequestBuilders;
using WireMock.ResponseBuilders;
using WireMock.Server;

namespace Capacitor.Cli.Tests.Unit;

public class OAuthFlowTests {
    [Test]
    public async Task WorkOS_authorize_url_targets_api_domain_with_authkit_and_org() {
        var options = OAuthLoginFlow.BuildWorkOSOptions("client_d", "https://api.workos.com", "http://127.0.0.1:5555/callback");
        var oidc    = new OidcClient(options);

        var state = await oidc.PrepareLoginAsync(OAuthLoginFlow.WorkOSFrontChannel("org_a"));

        await Assert.That(state.StartUrl).StartsWith("https://api.workos.com/user_management/authorize");
        await Assert.That(state.StartUrl).Contains("provider=authkit");
        await Assert.That(state.StartUrl).Contains("organization_id=org_a");
        await Assert.That(state.StartUrl).Contains("code_challenge_method=S256");
        await Assert.That(options.LoadProfile).IsFalse();
    }

    [Test]
    public async Task WorkOS_authorize_url_omits_org_when_null() {
        var options = OAuthLoginFlow.BuildWorkOSOptions("client_d", "https://api.workos.com", "http://127.0.0.1:5555/callback");
        var oidc    = new OidcClient(options);

        var state = await oidc.PrepareLoginAsync(OAuthLoginFlow.WorkOSFrontChannel(null));

        await Assert.That(state.StartUrl).DoesNotContain("organization_id");
    }

    [Test]
    public async Task AuthenticateWorkOS_maps_token_response_including_org_and_user() {
        using var server = WireMockServer.Start();
        server.Given(Request.Create().WithPath("/user_management/authenticate").UsingPost())
            .RespondWith(Response.Create().WithStatusCode(200).WithBody(
                """{"user":{"id":"user_x","first_name":"Ada"},"organization_id":"org_a","access_token":"acc","refresh_token":"rt"}"""));

        var result = await OAuthLoginFlow.AuthenticateWorkOSAsync(
            "client_d", "org_a", FakeBrowser.WithCode("the_code"), apiBase: server.Urls[0]);

        await Assert.That(result).IsNotNull();
        await Assert.That(result!.AccessToken).IsEqualTo("acc");
        await Assert.That(result.RefreshToken).IsEqualTo("rt");
        await Assert.That(result.OrganizationId).IsEqualTo("org_a");
        await Assert.That(result.User!.FirstName).IsEqualTo("Ada");
    }

    [Test]
    public async Task AuthenticateWorkOS_handles_orgless_response_without_throwing() {
        using var server = WireMockServer.Start();
        server.Given(Request.Create().WithPath("/user_management/authenticate").UsingPost())
            .RespondWith(Response.Create().WithStatusCode(200).WithBody(
                """{"access_token":"acc","refresh_token":"rt"}"""));   // no organization_id, no user

        var result = await OAuthLoginFlow.AuthenticateWorkOSAsync(
            "client_d", organizationId: null, FakeBrowser.WithCode("the_code"), apiBase: server.Urls[0]);

        await Assert.That(result).IsNotNull();
        await Assert.That(result!.OrganizationId).IsNull();
        await Assert.That(result.User).IsNull();
        await Assert.That(result.RefreshToken).IsEqualTo("rt");
    }

    [Test]
    public async Task AuthenticateWorkOS_returns_null_on_token_endpoint_error() {
        using var server = WireMockServer.Start();
        server.Given(Request.Create().WithPath("/user_management/authenticate").UsingPost())
            .RespondWith(Response.Create().WithStatusCode(400).WithBody("""{"error":"invalid_grant"}"""));

        var result = await OAuthLoginFlow.AuthenticateWorkOSAsync(
            "client_d", "org_a", FakeBrowser.WithCode("the_code"), apiBase: server.Urls[0]);

        await Assert.That(result).IsNull();
    }

    [Test]
    public async Task WorkOSSignInError_preserves_actionable_detail() {
        await Assert.That(OAuthLoginFlow.WorkOSSignInError("Timeout", null)).Contains("Timed out");
        await Assert.That(OAuthLoginFlow.WorkOSSignInError("Invalid state.", null))
            .IsEqualTo("WorkOS sign-in failed: Invalid state.");
        await Assert.That(OAuthLoginFlow.WorkOSSignInError("invalid_grant", "bad code"))
            .IsEqualTo("WorkOS sign-in failed: invalid_grant — bad code");
    }

    [Test]
    public async Task SwitchWorkOSOrg_posts_refresh_grant_with_org_and_returns_token() {
        using var server = WireMockServer.Start();
        server.Given(Request.Create().WithPath("/user_management/authenticate").UsingPost())
            .RespondWith(Response.Create().WithStatusCode(200).WithBody(
                """{"user":{"id":"user_x"},"organization_id":"org_a","access_token":"acc","refresh_token":"rt2"}"""));
        using var http = new HttpClient();

        var auth = await OAuthLoginFlow.SwitchWorkOSOrgAsync(http, server.Urls[0], "client_d", "rt1", "org_a");

        await Assert.That(auth!.OrganizationId).IsEqualTo("org_a");
        await Assert.That(auth.RefreshToken).IsEqualTo("rt2");
        await Assert.That(auth.AccessToken).IsEqualTo("acc");
    }

    [Test]
    public async Task SwitchWorkOSOrg_returns_null_on_error() {
        using var server = WireMockServer.Start();
        server.Given(Request.Create().WithPath("/user_management/authenticate").UsingPost())
            .RespondWith(Response.Create().WithStatusCode(401));
        using var http = new HttpClient();

        var auth = await OAuthLoginFlow.SwitchWorkOSOrgAsync(http, server.Urls[0], "client_d", "rt1", "org_a");

        await Assert.That(auth).IsNull();
    }

    [Test]
    public async Task RefreshWorkOSToken_posts_org_less_refresh_grant_and_returns_token() {
        using var server = WireMockServer.Start();
        server.Given(Request.Create().WithPath("/user_management/authenticate").UsingPost())
            .RespondWith(Response.Create().WithStatusCode(200).WithBody(
                """{"user":{"id":"user_x"},"access_token":"acc","refresh_token":"rt2"}"""));
        using var http = new HttpClient();

        var auth = await OAuthLoginFlow.RefreshWorkOSTokenAsync(http, server.Urls[0], "client_d", "rt1");

        await Assert.That(auth!.AccessToken).IsEqualTo("acc");
        await Assert.That(auth.RefreshToken).IsEqualTo("rt2");

        var body = server.FindLogEntries(Request.Create().WithPath("/user_management/authenticate").UsingPost())[0].RequestMessage.Body!;
        await Assert.That(body).Contains("refresh_token=rt1");
        await Assert.That(body).DoesNotContain("organization_id");
    }

    [Test]
    public async Task RefreshWorkOSToken_returns_null_on_transport_failure() {
        // A network/timeout blip during a mid-poll refresh must degrade to null, not throw and
        // abort provisioning. Start then stop the server so the connection is refused.
        var server = WireMockServer.Start();
        var url = server.Urls[0];
        server.Stop();

        using var http = new HttpClient();

        var auth = await OAuthLoginFlow.RefreshWorkOSTokenAsync(http, url, "client_d", "rt1");

        await Assert.That(auth).IsNull();
    }

    [Test]
    public async Task GitHubBrowser_exchanges_code_and_returns_access_token() {
        using var server = WireMockServer.Start();
        server.Given(Request.Create().WithPath("/code-exchange").UsingPost())
            .RespondWith(Response.Create().WithStatusCode(200).WithBody("""{"access_token":"gho_abc"}"""));

        var token = await OAuthLoginFlow.RunGitHubBrowserFlowAsync(
            "Iv1.abc", $"{server.Urls[0]}/code-exchange", FakeBrowser.WithCode("the_code"));

        await Assert.That(token).IsEqualTo("gho_abc");
    }

    [Test]
    public async Task GitHubBrowser_returns_null_on_state_mismatch_without_calling_proxy() {
        using var server = WireMockServer.Start();
        server.Given(Request.Create().WithPath("/code-exchange").UsingPost())
            .RespondWith(Response.Create().WithStatusCode(200).WithBody("""{"access_token":"nope"}"""));

        var token = await OAuthLoginFlow.RunGitHubBrowserFlowAsync(
            "Iv1.abc", $"{server.Urls[0]}/code-exchange",
            FakeBrowser.WithRawQuery("?code=the_code&state=attacker"));

        await Assert.That(token).IsNull();
        // The proxy must never be hit when the CSRF state doesn't match.
        await Assert.That(server.LogEntries.Any(e => e.RequestMessage.Path == "/code-exchange")).IsFalse();
    }

    [Test]
    public async Task GitHubBrowser_returns_null_on_non_success_browser_result() {
        var token = await OAuthLoginFlow.RunGitHubBrowserFlowAsync(
            "Iv1.abc", "http://unused.test/code-exchange", FakeBrowser.NonSuccess(BrowserResultType.Timeout));

        await Assert.That(token).IsNull();
    }

    [Test]
    public async Task ChooseDiscoveryProvider_honors_flags_and_default() {
        await Assert.That(OAuthLoginFlow.ChooseDiscoveryProvider(["--github"], isInteractive: true)).IsEqualTo(AuthProvider.GitHubApp);
        await Assert.That(OAuthLoginFlow.ChooseDiscoveryProvider([], isInteractive: true)).IsEqualTo(AuthProvider.WorkOS);   // default = org SSO, no prompt
    }

    // A non-interactive session used to fall back to GitHub App auth, because GitHub has a device
    // flow and WorkOS's loopback callback can't reach a CLI on a remote box. That fallback was the
    // ONLY remaining source of new GitHub App sign-ins, and it dead-ended: the GitHub App branch has
    // no provisioning path, so a headless user with no workspace authenticated and was then told to
    // ask an admin to install a GitHub App. No implicit routing onto the legacy provider — the
    // caller fails fast instead.
    [Test]
    public async Task Headless_discovery_does_not_fall_back_to_legacy_github_app() =>
        await Assert.That(OAuthLoginFlow.ChooseDiscoveryProvider([], isInteractive: false)).IsNull();

    // The explicit opt-in is a deliberate statement, not a silent default, so it still works for
    // someone who genuinely needs the legacy provider on a headless box.
    [Test]
    public async Task Explicit_github_flag_still_selects_legacy_provider_when_headless() =>
        await Assert.That(OAuthLoginFlow.ChooseDiscoveryProvider(["--github"], isInteractive: false))
            .IsEqualTo(AuthProvider.GitHubApp);

    // `--device` is the documented way to authenticate from SSH / a container (help-login.txt,
    // help-setup.txt, README's command tour), and only GitHub has a device flow. Dropping headless
    // discovery must not take that contract with it: this is an explicit request, same class as
    // --github, not the implicit fallback this change removed.
    [Test]
    public async Task Explicit_device_flag_still_selects_legacy_provider_when_headless() =>
        await Assert.That(OAuthLoginFlow.ChooseDiscoveryProvider(["--device"], isInteractive: false))
            .IsEqualTo(AuthProvider.GitHubApp);

    // ...but ONLY when headless. On a machine with a browser, `--device` has always meant "use the
    // device flow if the login step needs GitHub", never "switch discovery off org SSO" — routing it
    // to the legacy provider here would be a regression in the opposite direction.
    [Test]
    public async Task Device_flag_does_not_divert_an_interactive_session_away_from_sso() =>
        await Assert.That(OAuthLoginFlow.ChooseDiscoveryProvider(["--device"], isInteractive: true))
            .IsEqualTo(AuthProvider.WorkOS);

    // The message a headless user gets instead. It must not send anyone to the legacy GitHub App,
    // and it must name both real routes: create a workspace, or point at an existing one.
    [Test]
    public async Task Headless_discovery_message_offers_signup_and_server_url_but_never_the_github_app() {
        var message = OAuthLoginFlow.HeadlessDiscoveryUnsupportedMessage();

        await Assert.That(message).DoesNotContain("GitHub App");
        await Assert.That(message).Contains("/signup");
        await Assert.That(message).Contains("--server-url");
    }

    [Test]
    public async Task ShouldDiscoverLogin_true_when_no_server_or_discover_flag() {
        await Assert.That(OAuthLoginFlow.ShouldDiscoverLogin(null, [])).IsTrue();
        await Assert.That(OAuthLoginFlow.ShouldDiscoverLogin(null, ["--device"])).IsTrue();
        await Assert.That(OAuthLoginFlow.ShouldDiscoverLogin("https://x.example", ["--discover"])).IsTrue();
        await Assert.That(OAuthLoginFlow.ShouldDiscoverLogin("https://x.example", [])).IsFalse();
    }

    [Test]
    public async Task ChooseGitHubFlow_returns_device_when_forced() {
        var choice = OAuthLoginFlow.ChooseGitHubFlow(forceDevice: true, isHeadless: false, hasExchangeUrl: true);
        await Assert.That(choice).IsEqualTo(GitHubFlow.Device);
    }

    [Test]
    public async Task ChooseGitHubFlow_returns_device_when_headless() {
        var choice = OAuthLoginFlow.ChooseGitHubFlow(forceDevice: false, isHeadless: true, hasExchangeUrl: true);
        await Assert.That(choice).IsEqualTo(GitHubFlow.Device);
    }

    [Test]
    public async Task ChooseGitHubFlow_returns_device_when_no_exchange_url() {
        var choice = OAuthLoginFlow.ChooseGitHubFlow(forceDevice: false, isHeadless: false, hasExchangeUrl: false);
        await Assert.That(choice).IsEqualTo(GitHubFlow.Device);
    }

    [Test]
    public async Task IsValidExchangeUrl_accepts_https_url() {
        await Assert.That(OAuthLoginFlow.IsValidExchangeUrl("https://auth.example/auth/github/code-exchange")).IsTrue();
    }

    [Test]
    public async Task IsValidExchangeUrl_accepts_http_url() {
        await Assert.That(OAuthLoginFlow.IsValidExchangeUrl("http://localhost:8080/exchange")).IsTrue();
    }

    [Test]
    public async Task IsValidExchangeUrl_rejects_null() {
        await Assert.That(OAuthLoginFlow.IsValidExchangeUrl(null)).IsFalse();
    }

    [Test]
    public async Task IsValidExchangeUrl_rejects_empty_and_whitespace() {
        await Assert.That(OAuthLoginFlow.IsValidExchangeUrl("")).IsFalse();
        await Assert.That(OAuthLoginFlow.IsValidExchangeUrl("   ")).IsFalse();
    }

    [Test]
    public async Task IsValidExchangeUrl_rejects_relative_path() {
        await Assert.That(OAuthLoginFlow.IsValidExchangeUrl("/auth/exchange")).IsFalse();
    }

    [Test]
    public async Task IsValidExchangeUrl_rejects_non_http_scheme() {
        await Assert.That(OAuthLoginFlow.IsValidExchangeUrl("javascript:alert(1)")).IsFalse();
        await Assert.That(OAuthLoginFlow.IsValidExchangeUrl("file:///etc/passwd")).IsFalse();
    }

    [Test]
    public async Task ChooseGitHubFlow_returns_browser_when_interactive_and_server_supports_it() {
        var choice = OAuthLoginFlow.ChooseGitHubFlow(forceDevice: false, isHeadless: false, hasExchangeUrl: true);
        await Assert.That(choice).IsEqualTo(GitHubFlow.Browser);
    }
}
