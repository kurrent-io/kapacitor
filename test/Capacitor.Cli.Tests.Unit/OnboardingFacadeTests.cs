using System.Net;
using System.Text;
using Capacitor.Cli.Core;
using Capacitor.Cli.Core.Auth;
using Capacitor.Cli.Core.Config;
using Capacitor.Cli.Core.Telemetry;
using Duende.IdentityModel.OidcClient.Browser;
using NSubstitute;
using WireMock.RequestBuilders;
using WireMock.ResponseBuilders;
using WireMock.Server;

namespace Capacitor.Cli.Tests.Unit;

/// <summary>Every HTTP endpoint the façade can touch, served from one scripted handler.</summary>
sealed class AuthHttpScript(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler {
    public List<string> Seen { get; } = [];

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) {
        ct.ThrowIfCancellationRequested();
        Seen.Add($"{request.Method} {request.RequestUri}");

        return Task.FromResult(respond(request));
    }
}

static class AuthHttp {
    public static HttpResponseMessage Json(string body) =>
        new(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    public static HttpResponseMessage Status(HttpStatusCode code, string body = "") =>
        new(code) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    public const string DeviceCode = """{"device_code":"dc","user_code":"UC","verification_uri":"","interval":0}""";

    /// <param name="tokenExchange">POST {tenant}/auth/token — defaults to a JWT for "alice".</param>
    public static AuthHttpScript Script(
            string?                                        authConfig    = null,
            string?                                        proxyConfig   = null,
            string?                                        tenants       = null,
            string?                                        workosTenants = null,
            string?                                        orgSwitch     = null,
            Func<HttpRequestMessage, HttpResponseMessage>? tokenExchange = null,
            Func<HttpResponseMessage>?                     devicePoll    = null) =>
        new(request => {
            var path = request.RequestUri!.AbsolutePath;

            if (path.EndsWith("/auth/config", StringComparison.Ordinal)) {
                return authConfig is null ? Status(HttpStatusCode.NotFound) : Json(authConfig);
            }

            if (path == "/config") {
                return proxyConfig is null ? Status(HttpStatusCode.NotFound) : Json(proxyConfig);
            }

            if (path == "/discover-tenants") {
                return tenants is null ? Status(HttpStatusCode.InternalServerError) : Json(tenants);
            }

            if (path == "/discover-tenants-workos") {
                return workosTenants is null ? Status(HttpStatusCode.InternalServerError) : Json(workosTenants);
            }

            if (path == "/user_management/authenticate") {
                return orgSwitch is null ? Status(HttpStatusCode.Unauthorized) : Json(orgSwitch);
            }

            if (path == "/login/device/code") return Json(DeviceCode);

            if (path == "/login/oauth/access_token") {
                return devicePoll?.Invoke() ?? Json("""{"access_token":"gh-token"}""");
            }

            if (path.EndsWith("/auth/token", StringComparison.Ordinal)) {
                return tokenExchange?.Invoke(request)
                    ?? Json("""{"access_token":"capacitor-jwt","expires_in":3600,"username":"alice"}""");
            }

            return Status(HttpStatusCode.NotFound);
        });
}

/// <summary>
/// Operation-level contract of <see cref="OnboardingFacade"/>: provider dispatch, discovery over
/// every tenant, cancellation before the boundary, and the WorkOS retarget/provisioner arms.
/// Shares the TokenStoreProfileTests key so the one KCAP_CONFIG_DIR isn't raced, and the funnel-sink
/// keys because WorkOS discovery emits SetupFunnel events into CliTelemetry's process-global sink.
/// </summary>
[NotInParallel([
    nameof(TokenStoreProfileTests),
    nameof(TelemetryState) + "." + nameof(TelemetryState.PathOverride),
    nameof(TelemetryDeviceId) + "." + nameof(TelemetryDeviceId.PathOverride),
])]
public class OnboardingFacadeTests {
    static string TokensDir  => PathHelpers.ConfigPath("tokens");
    static string LegacyPath => PathHelpers.ConfigPath("tokens.json");
    static string ConfigPath => AppConfig.GetConfigPath();

    [Before(Test)]
    public void Cleanup() => SharedConfigDirCleanup.ClearTokenAndProfileState(LegacyPath, TokensDir);

    internal static OnboardingFacade NewFacade(
            IAuthProgress                                                     progress,
            HttpMessageHandler                                                handler,
            ITenantPicker?                                                    picker         = null,
            ITenantProvisioner?                                               provisioner    = null,
            Func<IReadOnlyList<AuthIdentity>, CancellationToken, Task>?       beforeCommit   = null,
            Func<CancellationToken, Task<WorkOSAuthResponse?>>?               workosLogin    = null,
            IBrowser?                                                         workosBrowser  = null,
            string?                                                           workosApiBase  = null) {
        var http = new HttpClient(handler, disposeHandler: false);

        return new OnboardingFacade(progress, picker ?? Substitute.For<ITenantPicker>(), provisioner, beforeCommit, () => http) {
            WorkOSOrglessLogin    = workosLogin,
            WorkOSBrowser         = workosBrowser,
            WorkOSApiBaseOverride = workosApiBase
        };
    }

    internal static ITenantPicker PickerReturningFirst() {
        var picker = Substitute.For<ITenantPicker>();
        picker.PickAsync(Arg.Any<DiscoveredTenant[]>(), Arg.Any<CancellationToken>())
              .Returns(ci => Task.FromResult<DiscoveredTenant?>(ci.Arg<DiscoveredTenant[]>()[0]));

        return picker;
    }

    internal static ProfileConfig ReadConfig() => ConfigMutator.LoadPure(ConfigPath);

    internal static bool TokenFileExists(string profile) => File.Exists(Path.Combine(TokensDir, $"{profile}.json"));

    // ── login to a known server ──────────────────────────────────────────────

    [Test]
    public async Task LoginAsync_none_provider_publishes_profile_and_stamp_with_no_token() {
        using var handler  = AuthHttp.Script(authConfig: """{"provider":"None"}""");
        var       progress = new RecordingAuthProgress();
        var       facade   = NewFacade(progress, handler);

        var result = await facade.LoginAsync(
            "https://none.example", forceDevice: false, profile: "solo", CancellationToken.None, adoptServer: true);

        await Assert.That(result).IsTypeOf<AuthResult.Committed>();
        var committed = (AuthResult.Committed)result;
        await Assert.That(committed.Provider).IsEqualTo(AuthProvider.None);
        await Assert.That(committed.ActiveProfile).IsEqualTo("solo");
        await Assert.That(committed.Published).HasCount(1);

        var profile = ReadConfig().Profiles["solo"];
        await Assert.That(profile.ServerUrl).IsEqualTo("https://none.example");
        await Assert.That(profile.AuthProvider!.Provider).IsEqualTo("None");
        await Assert.That(ServerIdentity.SameServer(profile.AuthProvider.ServerUrl, profile.ServerUrl)).IsTrue();
        await Assert.That(TokenFileExists("solo")).IsFalse();
        await Assert.That(progress.Notices).Contains("Server has no authentication configured — login not required.");
    }

    [Test]
    public async Task LoginAsync_none_provider_leaves_a_profile_that_already_points_at_the_server_alone() {
        await ConfigMutator.MutateAsync(c => c with {
            Profiles = new Dictionary<string, Profile> { ["solo"] = new() { ServerUrl = "https://none.example/" } }
        });

        using var handler = AuthHttp.Script(authConfig: """{"provider":"None"}""");
        var       facade  = NewFacade(new RecordingAuthProgress(), handler);

        await facade.LoginAsync("https://none.example", forceDevice: false, profile: "solo", CancellationToken.None);

        var profile = ReadConfig().Profiles["solo"];
        await Assert.That(profile.ServerUrl).IsEqualTo("https://none.example/"); // same server — not rewritten
        await Assert.That(profile.AuthProvider!.Provider).IsEqualTo(AuthProvider.None);
    }

    [Test]
    public async Task LoginAsync_github_publishes_the_exchanged_token_and_the_stamp() {
        using var handler  = AuthHttp.Script(authConfig: """{"provider":"GitHubApp","github_client_id":"cid"}""");
        var       progress = new RecordingAuthProgress();
        var       facade   = NewFacade(progress, handler);

        var result = await facade.LoginAsync(
            "https://acme.kcap.ai", forceDevice: true, profile: "acme", CancellationToken.None, adoptServer: true);

        await Assert.That(result).IsTypeOf<AuthResult.Committed>();
        await Assert.That(((AuthResult.Committed)result).Username).IsEqualTo("alice");

        var stored = await TokenStore.LoadAsync("acme");
        await Assert.That(stored!.AccessToken).IsEqualTo("capacitor-jwt");
        await Assert.That(stored.Provider).IsEqualTo(AuthProvider.GitHubApp);

        var profile = ReadConfig().Profiles["acme"];
        await Assert.That(profile.AuthProvider!.Provider).IsEqualTo(AuthProvider.GitHubApp);
        await Assert.That(ServerIdentity.SameServer(profile.AuthProvider.ServerUrl, "https://acme.kcap.ai")).IsTrue();
        await Assert.That(profile.ServerUrl).IsEqualTo("https://acme.kcap.ai"); // adopted
        await Assert.That(progress.Notices).Contains("Logged in as alice");
    }

    [Test]
    public async Task LoginAsync_without_adopt_keeps_todays_behaviour_on_a_foreign_profile() {
        await ConfigMutator.MutateAsync(c => c with {
            Profiles = new Dictionary<string, Profile> { ["acme"] = new() { ServerUrl = "https://other.example" } }
        });

        using var handler = AuthHttp.Script(authConfig: """{"provider":"GitHubApp","github_client_id":"cid"}""");
        var       facade  = NewFacade(new RecordingAuthProgress(), handler);

        var result = await facade.LoginAsync("https://acme.kcap.ai", forceDevice: true, profile: "acme", CancellationToken.None);

        await Assert.That(result).IsTypeOf<AuthResult.Committed>();
        await Assert.That((await TokenStore.LoadAsync("acme"))!.AccessToken).IsEqualTo("capacitor-jwt");

        // No repoint and no claim on a server the profile doesn't name.
        var profile = ReadConfig().Profiles["acme"];
        await Assert.That(profile.ServerUrl).IsEqualTo("https://other.example");
        await Assert.That(profile.AuthProvider).IsNull();
    }

    [Test]
    public async Task LoginAsync_none_without_adopt_refuses_a_foreign_profile() {
        using var handler  = AuthHttp.Script(authConfig: """{"provider":"None"}""");
        var       progress = new RecordingAuthProgress();
        var       facade   = NewFacade(progress, handler);

        var result = await facade.LoginAsync("https://none.example", forceDevice: false, profile: "solo", CancellationToken.None);

        await Assert.That(result).IsTypeOf<AuthResult.Failed>();
        await Assert.That(File.Exists(ConfigPath)).IsFalse();
        await Assert.That(progress.Errors.Any(e => e.Contains("is not configured for https://none.example"))).IsTrue();
    }

    [Test]
    public async Task LoginAsync_workos_known_server_publishes_the_org_scoped_token_and_stamp() {
        using var workos = WireMockServer.Start();
        workos.Given(Request.Create().WithPath("/user_management/authenticate").UsingPost())
              .RespondWith(Response.Create().WithStatusCode(200).WithBody(
                  """{"user":{"id":"user_x","first_name":"Ada"},"organization_id":"org_a","access_token":"acc","refresh_token":"rt"}"""));

        using var handler = AuthHttp.Script(
            authConfig: """{"provider":"workos","client_id":"client_d","organization_id":"org_a"}""");

        var progress = new RecordingAuthProgress();
        var facade   = NewFacade(progress, handler,
            workosBrowser: FakeBrowser.WithCode("the_code"), workosApiBase: workos.Urls[0]);

        var result = await facade.LoginAsync(
            "https://acme.kcap.ai", forceDevice: false, profile: "acme", CancellationToken.None, adoptServer: true);

        await Assert.That(result).IsTypeOf<AuthResult.Committed>();

        var stored = await TokenStore.LoadAsync("acme");
        await Assert.That(stored!.AccessToken).IsEqualTo("acc");
        await Assert.That(stored.RefreshToken).IsEqualTo("rt");
        await Assert.That(stored.ClientId).IsEqualTo("client_d");
        await Assert.That(stored.Provider).IsEqualTo(AuthProvider.WorkOS);

        var profile = ReadConfig().Profiles["acme"];
        await Assert.That(profile.ServerUrl).IsEqualTo("https://acme.kcap.ai");
        await Assert.That(profile.AuthProvider!.Provider).IsEqualTo(AuthProvider.WorkOS);
        await Assert.That(progress.Notices).Contains("Logged in as Ada");
    }

    [Test]
    public async Task LoginAsync_workos_wrong_organization_publishes_nothing() {
        using var workos = WireMockServer.Start();
        workos.Given(Request.Create().WithPath("/user_management/authenticate").UsingPost())
              .RespondWith(Response.Create().WithStatusCode(200).WithBody(
                  """{"user":{"id":"user_x"},"organization_id":"org_other","access_token":"acc","refresh_token":"rt"}"""));

        using var handler = AuthHttp.Script(
            authConfig: """{"provider":"workos","client_id":"client_d","organization_id":"org_a"}""");

        var facade = NewFacade(new RecordingAuthProgress(), handler,
            workosBrowser: FakeBrowser.WithCode("the_code"), workosApiBase: workos.Urls[0]);

        var result = await facade.LoginAsync(
            "https://acme.kcap.ai", forceDevice: false, profile: "acme", CancellationToken.None, adoptServer: true);

        await Assert.That(result).IsTypeOf<AuthResult.Failed>();
        await Assert.That(File.Exists(ConfigPath)).IsFalse();
        await Assert.That(TokenFileExists("acme")).IsFalse();
    }

    [Test]
    public async Task LoginAsync_cancelled_during_the_token_exchange_publishes_nothing() {
        using var cts = new CancellationTokenSource();

        using var handler = AuthHttp.Script(
            authConfig: """{"provider":"GitHubApp","github_client_id":"cid"}""",
            tokenExchange: _ => {
                cts.Cancel();

                throw new OperationCanceledException(cts.Token);
            });

        var facade = NewFacade(new RecordingAuthProgress(), handler);

        var result = await facade.LoginAsync(
            "https://acme.kcap.ai", forceDevice: true, profile: "acme", cts.Token, adoptServer: true);

        await Assert.That(result).IsTypeOf<AuthResult.Cancelled>();
        await Assert.That(File.Exists(ConfigPath)).IsFalse();
        await Assert.That(TokenFileExists("acme")).IsFalse();
    }

    [Test]
    public async Task LoginAsync_unknown_provider_fails_with_todays_message() {
        using var handler  = AuthHttp.Script(authConfig: """{"provider":"martian"}""");
        var       progress = new RecordingAuthProgress();
        var       facade   = NewFacade(progress, handler);

        var result = await facade.LoginAsync("https://acme.kcap.ai", forceDevice: true, profile: "acme", CancellationToken.None);

        await Assert.That(result).IsTypeOf<AuthResult.Failed>();
        await Assert.That(progress.Errors).Contains("Error: Unknown auth provider 'martian'. Update your kcap CLI.");
        await Assert.That(File.Exists(ConfigPath)).IsFalse();
    }

    [Test]
    public async Task LoginAsync_cancelled_during_the_device_poll_publishes_nothing() {
        using var cts   = new CancellationTokenSource();
        var       polls = 0;

        using var handler = AuthHttp.Script(
            authConfig: """{"provider":"GitHubApp","github_client_id":"cid"}""",
            devicePoll: () => {
                if (++polls == 2) cts.Cancel();

                return AuthHttp.Json("""{"error":"authorization_pending"}""");
            });

        var facade = NewFacade(new RecordingAuthProgress(), handler);

        var result = await facade.LoginAsync("https://acme.kcap.ai", forceDevice: true, profile: "acme", cts.Token);

        await Assert.That(result).IsTypeOf<AuthResult.Cancelled>();
        await Assert.That(File.Exists(ConfigPath)).IsFalse();
        await Assert.That(TokenFileExists("acme")).IsFalse();
    }

    // ── discovery ────────────────────────────────────────────────────────────

    internal const string TwoGitHubTenants = """
        [{"org_id":1,"org_login":"acme","origin":"https://acme.kcap.ai"},
         {"org_id":2,"org_login":"contoso","origin":"https://contoso.kcap.ai"}]
        """;

    [Test]
    public async Task DiscoverAsync_github_publishes_a_token_for_every_discovered_tenant() {
        using var handler = AuthHttp.Script(
            proxyConfig: """{"github_client_id":"cid"}""",
            tenants: TwoGitHubTenants);

        var identities = new List<AuthIdentity>();
        var facade = NewFacade(new RecordingAuthProgress(), handler, PickerReturningFirst(),
            beforeCommit: (ids, _) => { identities.AddRange(ids); return Task.CompletedTask; });

        var result = await facade.DiscoverAsync(AuthProvider.GitHubApp, forceDevice: true, CancellationToken.None);

        await Assert.That(result).IsTypeOf<AuthResult.Committed>();
        await Assert.That(((AuthResult.Committed)result).ActiveProfile).IsEqualTo("acme");
        await Assert.That(identities.Select(i => i.Profile)).IsEquivalentTo(new[] { "acme", "contoso" });

        await Assert.That(TokenFileExists("acme")).IsTrue();
        await Assert.That(TokenFileExists("contoso")).IsTrue();

        var cfg = ReadConfig();
        await Assert.That(cfg.ActiveProfile).IsEqualTo("acme");
        await Assert.That(cfg.Profiles["contoso"].ServerUrl).IsEqualTo("https://contoso.kcap.ai");
    }

    [Test]
    public async Task DiscoverAsync_github_commits_the_rest_when_one_tenant_exchange_fails() {
        using var handler = AuthHttp.Script(
            proxyConfig: """{"github_client_id":"cid"}""",
            tenants: TwoGitHubTenants,
            tokenExchange: request => request.RequestUri!.Host.StartsWith("contoso", StringComparison.Ordinal)
                ? AuthHttp.Status(HttpStatusCode.InternalServerError, """{"error":"nope"}""")
                : AuthHttp.Json("""{"access_token":"capacitor-jwt","expires_in":3600,"username":"alice"}"""));

        var progress = new RecordingAuthProgress();
        var facade   = NewFacade(progress, handler, PickerReturningFirst());

        var result = await facade.DiscoverAsync(AuthProvider.GitHubApp, forceDevice: true, CancellationToken.None);

        await Assert.That(result).IsTypeOf<AuthResult.Committed>();
        await Assert.That(TokenFileExists("acme")).IsTrue();
        await Assert.That(TokenFileExists("contoso")).IsFalse();
        await Assert.That(progress.Errors).Contains(
            "Warning: token exchange failed for contoso. Run 'kcap login' after switching to that profile.");
    }

    [Test]
    public async Task DiscoverAsync_github_commits_the_rest_when_one_tenant_exchange_throws() {
        using var handler = AuthHttp.Script(
            proxyConfig: """{"github_client_id":"cid"}""",
            tenants: TwoGitHubTenants,
            tokenExchange: request => request.RequestUri!.Host.StartsWith("acme", StringComparison.Ordinal)
                ? throw new HttpRequestException("connection reset")
                : AuthHttp.Json("""{"access_token":"capacitor-jwt","expires_in":3600,"username":"alice"}"""));

        var progress = new RecordingAuthProgress();
        var facade   = NewFacade(progress, handler, PickerReturningFirst());

        var result = await facade.DiscoverAsync(AuthProvider.GitHubApp, forceDevice: true, CancellationToken.None);

        // The throwing tenant loses only its own token; the boundary still finishes the rest.
        await Assert.That(result).IsTypeOf<AuthResult.Committed>();
        await Assert.That(TokenFileExists("acme")).IsFalse();
        await Assert.That(TokenFileExists("contoso")).IsTrue();
        await Assert.That(progress.Errors).Contains(
            "Warning: token exchange failed for acme. Run 'kcap login' after switching to that profile.");
        await Assert.That(ReadConfig().Profiles["contoso"].AuthProvider!.Provider).IsEqualTo(AuthProvider.GitHubApp);
    }

    [Test]
    public async Task DiscoverAsync_github_zero_tenants_reports_the_no_tenants_reason() {
        using var handler = AuthHttp.Script(proxyConfig: """{"github_client_id":"cid"}""", tenants: "[]");

        var facade = NewFacade(new RecordingAuthProgress(), handler);
        var result = await facade.DiscoverAsync(AuthProvider.GitHubApp, forceDevice: true, CancellationToken.None);

        await Assert.That(result).IsTypeOf<AuthResult.Failed>();
        await Assert.That(((AuthResult.Failed)result).Reason).IsEqualTo(AuthFailureReason.NoTenantsFound);
    }

    [Test]
    public async Task DiscoverAsync_github_token_denial_reports_the_signin_denied_reason() {
        using var handler = AuthHttp.Script(
            proxyConfig: """{"github_client_id":"cid"}""",
            devicePoll: () => AuthHttp.Json("""{"error":"access_denied"}"""));

        var facade = NewFacade(new RecordingAuthProgress(), handler);
        var result = await facade.DiscoverAsync(AuthProvider.GitHubApp, forceDevice: true, CancellationToken.None);

        await Assert.That(result).IsTypeOf<AuthResult.Failed>();
        await Assert.That(((AuthResult.Failed)result).Reason).IsEqualTo(AuthFailureReason.SigninDenied);
    }

    [Test]
    public async Task DiscoverAsync_unreachable_proxy_reports_the_unreachable_reason() {
        using var handler = AuthHttp.Script(); // no /config route

        var facade = NewFacade(new RecordingAuthProgress(), handler);
        var result = await facade.DiscoverAsync(AuthProvider.GitHubApp, forceDevice: true, CancellationToken.None);

        await Assert.That(result).IsTypeOf<AuthResult.Failed>();
        await Assert.That(((AuthResult.Failed)result).Reason).IsEqualTo(AuthFailureReason.Unreachable);
    }

    [Test]
    public async Task DiscoverAsync_github_cancelled_at_the_picker_publishes_nothing() {
        using var cts = new CancellationTokenSource();

        using var handler = AuthHttp.Script(
            proxyConfig: """{"github_client_id":"cid"}""",
            tenants: TwoGitHubTenants);

        var picker = Substitute.For<ITenantPicker>();
        picker.PickAsync(Arg.Any<DiscoveredTenant[]>(), Arg.Any<CancellationToken>())
              .Returns<Task<DiscoveredTenant?>>(_ => { cts.Cancel(); throw new OperationCanceledException(cts.Token); });

        var facade = NewFacade(new RecordingAuthProgress(), handler, picker);

        var result = await facade.DiscoverAsync(AuthProvider.GitHubApp, forceDevice: true, cts.Token);

        await Assert.That(result).IsTypeOf<AuthResult.Cancelled>();
        await Assert.That(File.Exists(ConfigPath)).IsFalse();
        await Assert.That(TokenFileExists("acme")).IsFalse();
    }

    [Test]
    public async Task DiscoverAsync_reports_a_cancelled_proxy_call_as_cancelled_not_failed() {
        using var cts = new CancellationTokenSource();

        // AuthProxyClient maps OperationCanceledException onto its own "unreachable" result, so a
        // live cancel would otherwise be rendered as a transport failure.
        using var handler = new AuthHttpScript(_ => throw new OperationCanceledException(cts.Token));
        await cts.CancelAsync();

        var progress = new RecordingAuthProgress();
        var facade   = NewFacade(progress, handler);

        var result = await facade.DiscoverAsync(AuthProvider.GitHubApp, forceDevice: true, cts.Token);

        await Assert.That(result).IsTypeOf<AuthResult.Cancelled>();
        await Assert.That(progress.Errors).IsEmpty(); // a cancel must not paint a transport failure
    }

    [Test]
    public async Task DiscoverAsync_github_cancelled_during_tenant_discovery_renders_no_failure() {
        using var cts = new CancellationTokenSource();

        var progress = new RecordingAuthProgress();

        // /discover-tenants cancels instead of answering; AuthProxyClient maps that to "unreachable".
        using var cancelling = new AuthHttpScript(request => {
            if (request.RequestUri!.AbsolutePath == "/discover-tenants") {
                cts.Cancel();

                throw new OperationCanceledException(cts.Token);
            }

            if (request.RequestUri.AbsolutePath == "/config") return AuthHttp.Json("""{"github_client_id":"cid"}""");
            if (request.RequestUri.AbsolutePath == "/login/device/code") return AuthHttp.Json(AuthHttp.DeviceCode);

            return AuthHttp.Json("""{"access_token":"gh-token"}""");
        });

        var facade = NewFacade(progress, cancelling);
        var result = await facade.DiscoverAsync(AuthProvider.GitHubApp, forceDevice: true, cts.Token);

        await Assert.That(result).IsTypeOf<AuthResult.Cancelled>();
        await Assert.That(progress.Errors).IsEmpty();
        await Assert.That(File.Exists(ConfigPath)).IsFalse();
    }

    [Test]
    public async Task DiscoverAsync_workos_cancelled_during_tenant_discovery_renders_no_failure() {
        using var cts = new CancellationTokenSource();

        using var handler = new AuthHttpScript(request => {
            if (request.RequestUri!.AbsolutePath == "/config") return AuthHttp.Json("""{"workos_client_id":"client_d"}""");

            cts.Cancel();

            throw new OperationCanceledException(cts.Token);
        });

        var progress = new RecordingAuthProgress();
        var facade   = NewFacade(progress, handler, workosLogin: OrglessAda);

        var result = await facade.DiscoverAsync(AuthProvider.WorkOS, forceDevice: false, cts.Token);

        await Assert.That(result).IsTypeOf<AuthResult.Cancelled>();
        await Assert.That(progress.Errors).IsEmpty();
        await Assert.That(File.Exists(ConfigPath)).IsFalse();
    }

    [Test]
    public async Task DiscoverAsync_unknown_provider_fails() {
        using var handler = AuthHttp.Script(proxyConfig: """{"github_client_id":"cid"}""");
        var       facade  = NewFacade(new RecordingAuthProgress(), handler);

        var result = await facade.DiscoverAsync("martian", forceDevice: true, CancellationToken.None);

        await Assert.That(result).IsTypeOf<AuthResult.Failed>();
        await Assert.That(File.Exists(ConfigPath)).IsFalse();
    }

    // ── WorkOS discovery ─────────────────────────────────────────────────────

    internal const string WorkOSTenants = """
        [{"provider":"WorkOS","organization_id":"org_a","slug":"eventuous","display_name":"Eventuous","origin":"https://eventuous.kcap.ai"},
         {"provider":"WorkOS","organization_id":"org_b","slug":"contoso","display_name":"Contoso","origin":"https://contoso.kcap.ai"}]
        """;

    internal static Task<WorkOSAuthResponse?> OrglessAda(CancellationToken ct) =>
        Task.FromResult<WorkOSAuthResponse?>(new WorkOSAuthResponse {
            User = new() { Id = "user_x", FirstName = "Ada" }, AccessToken = "acc", RefreshToken = "rt"
        });

    [Test]
    public async Task DiscoverAsync_workos_publishes_the_picked_profile_and_its_org_scoped_token() {
        using var handler = AuthHttp.Script(
            proxyConfig: """{"workos_client_id":"client_d"}""",
            workosTenants: WorkOSTenants,
            orgSwitch: """{"organization_id":"org_a","access_token":"acc2","refresh_token":"rt2"}""");

        var progress = new RecordingAuthProgress();
        var facade   = NewFacade(progress, handler, PickerReturningFirst(), workosLogin: OrglessAda);

        var result = await facade.DiscoverAsync(AuthProvider.WorkOS, forceDevice: false, CancellationToken.None);

        await Assert.That(result).IsTypeOf<AuthResult.Committed>();
        var committed = (AuthResult.Committed)result;
        await Assert.That(committed.ActiveProfile).IsEqualTo("eventuous");
        await Assert.That(committed.Provider).IsEqualTo(AuthProvider.WorkOS);

        var stored = await TokenStore.LoadAsync("eventuous");
        await Assert.That(stored!.AccessToken).IsEqualTo("acc2");
        await Assert.That(stored.RefreshToken).IsEqualTo("rt2");
        await Assert.That(stored.ClientId).IsEqualTo("client_d");
        await Assert.That(stored.GitHubUsername).IsEqualTo("Ada");
        await Assert.That(ServerIdentity.SameServer(stored.ServerUrl, "https://eventuous.kcap.ai")).IsTrue();

        var cfg = ReadConfig();
        await Assert.That(cfg.ActiveProfile).IsEqualTo("eventuous");
        await Assert.That(cfg.Profiles["eventuous"].AuthProvider!.Provider).IsEqualTo(AuthProvider.WorkOS);
        await Assert.That(progress.Notices).Contains("Logged in as Ada → Eventuous");
    }

    [Test]
    public async Task DiscoverAsync_workos_retarget_hands_back_the_input_with_nothing_durable() {
        using var handler = AuthHttp.Script(
            proxyConfig: """{"workos_client_id":"client_d"}""",
            workosTenants: "[]");

        var provisioner = Substitute.For<ITenantProvisioner>();
        provisioner.OfferCreateAsync(Arg.Any<WorkOSTokenSource>(), Arg.Any<CancellationToken>())
                   .Returns(Task.FromResult(ProvisionOffer.ExistingWorkspace("kurrent")));

        var facade = NewFacade(new RecordingAuthProgress(), handler, provisioner: provisioner, workosLogin: OrglessAda);

        var result = await facade.DiscoverAsync(AuthProvider.WorkOS, forceDevice: false, CancellationToken.None);

        await Assert.That(result).IsTypeOf<AuthResult.Retarget>();
        await Assert.That(((AuthResult.Retarget)result).ServerInput).IsEqualTo("kurrent");
        await Assert.That(File.Exists(ConfigPath)).IsFalse();
    }

    [Test]
    public async Task DiscoverAsync_workos_provisioned_tenant_is_switched_into_and_published() {
        using var handler = AuthHttp.Script(
            proxyConfig: """{"workos_client_id":"client_d"}""",
            workosTenants: "[]",
            orgSwitch: """{"organization_id":"org_new","access_token":"acc2","refresh_token":"rt2"}""");

        var provisioner = Substitute.For<ITenantProvisioner>();
        provisioner.OfferCreateAsync(Arg.Any<WorkOSTokenSource>(), Arg.Any<CancellationToken>())
                   .Returns(Task.FromResult(ProvisionOffer.Created(
                       new ProvisionedTenant("org_new", "acme", "Acme Inc", "https://acme.kcap.ai"))));

        var facade = NewFacade(new RecordingAuthProgress(), handler, provisioner: provisioner, workosLogin: OrglessAda);

        var result = await facade.DiscoverAsync(AuthProvider.WorkOS, forceDevice: false, CancellationToken.None);

        await Assert.That(result).IsTypeOf<AuthResult.Committed>();
        await Assert.That((await TokenStore.LoadAsync("acme"))!.AccessToken).IsEqualTo("acc2");
        await Assert.That(ReadConfig().Profiles["acme"].AuthProvider!.Provider).IsEqualTo(AuthProvider.WorkOS);
    }

    [Test]
    public async Task DiscoverAsync_workos_declined_provisioning_fails_with_nothing_durable() =>
        await AssertProvisionOfferFails(ProvisionOffer.Declined);

    [Test]
    public async Task DiscoverAsync_workos_in_progress_provisioning_fails_with_nothing_durable() =>
        await AssertProvisionOfferFails(ProvisionOffer.InProgress);

    [Test]
    public async Task DiscoverAsync_workos_failed_provisioning_fails_with_nothing_durable() =>
        await AssertProvisionOfferFails(ProvisionOffer.Failed);

    static async Task AssertProvisionOfferFails(ProvisionOffer offer) {
        using var handler = AuthHttp.Script(
            proxyConfig: """{"workos_client_id":"client_d"}""",
            workosTenants: "[]");

        var provisioner = Substitute.For<ITenantProvisioner>();
        provisioner.OfferCreateAsync(Arg.Any<WorkOSTokenSource>(), Arg.Any<CancellationToken>())
                   .Returns(Task.FromResult(offer));

        var facade = NewFacade(new RecordingAuthProgress(), handler, provisioner: provisioner, workosLogin: OrglessAda);

        var result = await facade.DiscoverAsync(AuthProvider.WorkOS, forceDevice: false, CancellationToken.None);

        await Assert.That(result).IsTypeOf<AuthResult.Failed>();
        await Assert.That(File.Exists(ConfigPath)).IsFalse();
        await Assert.That(TokenFileExists("acme")).IsFalse();
    }
}
