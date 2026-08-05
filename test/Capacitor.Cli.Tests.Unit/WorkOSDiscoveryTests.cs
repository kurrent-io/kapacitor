using System.Text;
using Capacitor.Cli.Core;
using Capacitor.Cli.Core.Auth;
using Capacitor.Cli.Core.Config;
using NSubstitute;
using DiscoveryResult = Capacitor.Cli.Core.Auth.DiscoveryResult;

namespace Capacitor.Cli.Tests.Unit;

// Shares the TokenStoreProfileTests NotInParallel key so the shared KCAP_CONFIG_DIR
// tokens directory isn't raced by other profile-writing tests.
[NotInParallel(nameof(TokenStoreProfileTests))]
public class WorkOSDiscoveryTests {
    static string TokensDir => PathHelpers.ConfigPath("tokens");

    static string JwtWithExp(DateTimeOffset exp) {
        var json = $"{{\"exp\":{exp.ToUnixTimeSeconds()}}}";
        var b64  = Convert.ToBase64String(Encoding.UTF8.GetBytes(json)).TrimEnd('=').Replace('+', '-').Replace('/', '_');

        return $"header.{b64}.signature";
    }

    [Before(Test)]
    public void Cleanup() {
        try { if (Directory.Exists(TokensDir)) Directory.Delete(TokensDir, recursive: true); } catch { }
    }

    [Test]
    public async Task RunAsync_org_switches_picked_tenant_and_saves_workos_profile() {
        var proxyConfig = new ProxyConfigResponse { WorkOSClientId = "client_d", WorkOSAuthKitDomain = "" };

        var proxy = Substitute.For<IAuthProxyClient>();
        DiscoveredTenant[] tenants = [
            new() { Provider = "WorkOS", OrganizationId = "org_a", Slug = "eventuous", DisplayName = "Eventuous", Origin = "https://eventuous.kcap.ai" },
            new() { Provider = "WorkOS", OrganizationId = "org_b", Slug = "contoso",   DisplayName = "Contoso",   Origin = "https://contoso.kcap.ai" }
        ];
        proxy.DiscoverWorkOSTenantsAsync(Arg.Any<string>(), Arg.Any<string>())
             .Returns(Task.FromResult(new DiscoveryResult(tenants, DiscoveryError.None)));

        var picker = Substitute.For<ITenantPicker>();
        picker.Pick(tenants).Returns(tenants[0]); // eventuous

        var orgless  = new WorkOSAuthResponse { User = new() { Id = "user_x", FirstName = "Ada" }, AccessToken = "acc",  RefreshToken = "rt" };
        var switched = new WorkOSAuthResponse { User = new() { Id = "user_x" }, OrganizationId = "org_a", AccessToken = "acc2", RefreshToken = "rt2" };

        var outcome = await WorkOSDiscovery.RunAsync(
            "https://auth.kcap.ai", proxyConfig, proxy, picker,
            orglessLogin: ()     => Task.FromResult<WorkOSAuthResponse?>(orgless),
            orgSwitch:    (_, _) => Task.FromResult<WorkOSAuthResponse?>(switched));

        await Assert.That(outcome.ExitCode).IsEqualTo(0);

        var stored = await TokenStore.LoadAsync("eventuous");
        await Assert.That(stored).IsNotNull();
        await Assert.That(stored!.AccessToken).IsEqualTo("acc2");
        await Assert.That(stored.Provider).IsEqualTo(AuthProvider.WorkOS);

        var cfg = await AppConfig.LoadProfileConfig();
        await Assert.That(cfg.ActiveProfile).IsEqualTo("eventuous");
        await Assert.That(cfg.Profiles["eventuous"].ServerUrl).IsEqualTo("https://eventuous.kcap.ai");
    }

    [Test]
    public async Task RunAsync_errors_when_workos_not_configured() {
        var outcome = await WorkOSDiscovery.RunAsync(
            "https://auth.kcap.ai", new ProxyConfigResponse { WorkOSClientId = "" },
            Substitute.For<IAuthProxyClient>(), Substitute.For<ITenantPicker>(),
            ()     => Task.FromResult<WorkOSAuthResponse?>(null),
            (_, _) => Task.FromResult<WorkOSAuthResponse?>(null));

        await Assert.That(outcome.ExitCode).IsEqualTo(1);
    }

    [Test]
    public async Task RunAsync_errors_when_picked_tenant_has_no_org_id() {
        var proxy = Substitute.For<IAuthProxyClient>();
        DiscoveredTenant[] tenants = [
            new() { Provider = "WorkOS", Slug = "eventuous", DisplayName = "Eventuous", Origin = "https://eventuous.kcap.ai" } // no OrganizationId
        ];
        proxy.DiscoverWorkOSTenantsAsync(Arg.Any<string>(), Arg.Any<string>())
             .Returns(Task.FromResult(new DiscoveryResult(tenants, DiscoveryError.None)));

        var switchCalled = false;
        var outcome = await WorkOSDiscovery.RunAsync(
            "https://auth.kcap.ai", new ProxyConfigResponse { WorkOSClientId = "client_d" },
            proxy, Substitute.For<ITenantPicker>(),
            ()     => Task.FromResult<WorkOSAuthResponse?>(new WorkOSAuthResponse { AccessToken = "acc", RefreshToken = "rt" }),
            (_, _) => { switchCalled = true; return Task.FromResult<WorkOSAuthResponse?>(null); });

        await Assert.That(outcome.ExitCode).IsEqualTo(1);
        await Assert.That(switchCalled).IsFalse(); // fail before the org-switch, not during it
    }

    [Test]
    public async Task RunAsync_errors_when_no_tenants() {
        var proxy = Substitute.For<IAuthProxyClient>();
        proxy.DiscoverWorkOSTenantsAsync(Arg.Any<string>(), Arg.Any<string>())
             .Returns(Task.FromResult(new DiscoveryResult([], DiscoveryError.None)));

        var outcome = await WorkOSDiscovery.RunAsync(
            "https://auth.kcap.ai", new ProxyConfigResponse { WorkOSClientId = "client_d" },
            proxy, Substitute.For<ITenantPicker>(),
            ()     => Task.FromResult<WorkOSAuthResponse?>(new WorkOSAuthResponse { AccessToken = "acc", RefreshToken = "rt" }),
            (_, _) => Task.FromResult<WorkOSAuthResponse?>(null));

        await Assert.That(outcome.ExitCode).IsEqualTo(1);
    }

    [Test]
    public async Task RunAsync_provisions_when_no_tenants_and_provisioner_creates() {
        var proxyConfig = new ProxyConfigResponse { WorkOSClientId = "client_d" };
        var proxy = Substitute.For<IAuthProxyClient>();
        proxy.DiscoverWorkOSTenantsAsync(Arg.Any<string>(), Arg.Any<string>())
             .Returns(Task.FromResult(new DiscoveryResult([], DiscoveryError.None)));

        WorkOSTokenSource? handedTokens = null;
        var provisioner = Substitute.For<ITenantProvisioner>();
        provisioner.OfferCreateAsync(Arg.Any<WorkOSTokenSource>(), Arg.Any<CancellationToken>())
                   .Returns(ci => { handedTokens = ci.Arg<WorkOSTokenSource>(); return Task.FromResult(ProvisionOffer.Created(
                       new ProvisionedTenant("org_new", "acme", "Acme Inc", "https://acme.kcap.ai"))); });

        var orgless  = new WorkOSAuthResponse { User = new() { Id = "user_x", FirstName = "Ada" }, AccessToken = "acc", RefreshToken = "rt" };
        var switched = new WorkOSAuthResponse { User = new() { Id = "user_x" }, OrganizationId = "org_new", AccessToken = "acc2", RefreshToken = "rt2" };

        var outcome = await WorkOSDiscovery.RunAsync(
            "https://auth.kcap.ai", proxyConfig, proxy, Substitute.For<ITenantPicker>(),
            orglessLogin: ()     => Task.FromResult<WorkOSAuthResponse?>(orgless),
            orgSwitch:    (_, _) => Task.FromResult<WorkOSAuthResponse?>(switched),
            provisioner:  provisioner);

        await Assert.That(outcome.ExitCode).IsEqualTo(0);

        var stored = await TokenStore.LoadAsync("acme");
        await Assert.That(stored).IsNotNull();
        await Assert.That(stored!.AccessToken).IsEqualTo("acc2");

        var cfg = await AppConfig.LoadProfileConfig();
        await Assert.That(cfg.ActiveProfile).IsEqualTo("acme");
        await Assert.That(cfg.Profiles["acme"].ServerUrl).IsEqualTo("https://acme.kcap.ai");

        // The provisioner is handed a refreshing token source seeded with the org-less login token.
        await Assert.That(handedTokens).IsNotNull();
        await Assert.That(await handedTokens!.GetAsync(CancellationToken.None)).IsEqualTo("acc");
    }

    [Test]
    public async Task RunAsync_uses_rotated_refresh_token_for_org_switch_when_poll_refreshed() {
        var proxyConfig = new ProxyConfigResponse { WorkOSClientId = "client_d" };
        var proxy = Substitute.For<IAuthProxyClient>();
        proxy.DiscoverWorkOSTenantsAsync(Arg.Any<string>(), Arg.Any<string>())
             .Returns(Task.FromResult(new DiscoveryResult([], DiscoveryError.None)));

        // Login-time access token is already near expiry, so the provisioner's first token pull
        // forces a refresh — exactly the long-provisioning case that consumes the org-less token.
        var nearExpiry = JwtWithExp(DateTimeOffset.UtcNow.AddSeconds(5));
        var orgless    = new WorkOSAuthResponse { User = new() { Id = "u" }, AccessToken = nearExpiry, RefreshToken = "R0" };
        var switched   = new WorkOSAuthResponse { User = new() { Id = "u" }, OrganizationId = "org_new", AccessToken = "acc2", RefreshToken = "R2" };

        var provisioner = Substitute.For<ITenantProvisioner>();
        provisioner.OfferCreateAsync(Arg.Any<WorkOSTokenSource>(), Arg.Any<CancellationToken>())
                   .Returns(async ci => {
                       await ci.Arg<WorkOSTokenSource>().GetAsync(CancellationToken.None); // rotates R0 -> R1
                       return ProvisionOffer.Created(new ProvisionedTenant("org_new", "acme", "Acme Inc", "https://acme.kcap.ai"));
                   });

        string? switchRefreshToken = null;
        var outcome = await WorkOSDiscovery.RunAsync(
            "https://auth.kcap.ai", proxyConfig, proxy, Substitute.For<ITenantPicker>(),
            orglessLogin:   ()      => Task.FromResult<WorkOSAuthResponse?>(orgless),
            orgSwitch:      (rt, _) => { switchRefreshToken = rt; return Task.FromResult<WorkOSAuthResponse?>(switched); },
            orglessRefresh: (_, _)  => Task.FromResult<WorkOSAuthResponse?>(
                new WorkOSAuthResponse { AccessToken = JwtWithExp(DateTimeOffset.UtcNow.AddMinutes(5)), RefreshToken = "R1" }),
            provisioner:    provisioner);

        await Assert.That(outcome.ExitCode).IsEqualTo(0);
        await Assert.That(switchRefreshToken).IsEqualTo("R1"); // rotated token, not the consumed login-time R0
    }

    [Test]
    public async Task RunAsync_hands_back_the_workspace_the_user_already_has_instead_of_provisioning() {
        var proxy = Substitute.For<IAuthProxyClient>();
        proxy.DiscoverWorkOSTenantsAsync(Arg.Any<string>(), Arg.Any<string>())
             .Returns(Task.FromResult(new DiscoveryResult([], DiscoveryError.None)));

        var provisioner = Substitute.For<ITenantProvisioner>();
        provisioner.OfferCreateAsync(Arg.Any<WorkOSTokenSource>(), Arg.Any<CancellationToken>())
                   .Returns(Task.FromResult(ProvisionOffer.ExistingWorkspace("kurrent")));

        var switchCalled = false;
        var outcome = await WorkOSDiscovery.RunAsync(
            "https://auth.kcap.ai", new ProxyConfigResponse { WorkOSClientId = "client_d" },
            proxy, Substitute.For<ITenantPicker>(),
            ()     => Task.FromResult<WorkOSAuthResponse?>(new WorkOSAuthResponse { AccessToken = "acc", RefreshToken = "rt" }),
            (_, _) => { switchCalled = true; return Task.FromResult<WorkOSAuthResponse?>(null); },
            provisioner: provisioner);

        // The input comes back unresolved — expanding "kurrent" to a URL, and picking the auth
        // provider from that server, both belong to the caller.
        await Assert.That(outcome.RetargetServerInput).IsEqualTo("kurrent");

        // Non-zero, so a caller that ignores RetargetServerInput fails loudly rather than
        // claiming success after configuring nothing.
        await Assert.That(outcome.ExitCode).IsNotEqualTo(0);

        // Nothing WorkOS-shaped happened: no org-switch, no profile, no token.
        await Assert.That(switchCalled).IsFalse();
        await Assert.That(await TokenStore.LoadAsync("kurrent")).IsNull();
    }

    [Test]
    public async Task RunAsync_treats_a_blank_existing_workspace_input_as_no_retarget() {
        var proxy = Substitute.For<IAuthProxyClient>();
        proxy.DiscoverWorkOSTenantsAsync(Arg.Any<string>(), Arg.Any<string>())
             .Returns(Task.FromResult(new DiscoveryResult([], DiscoveryError.None)));

        var provisioner = Substitute.For<ITenantProvisioner>();
        provisioner.OfferCreateAsync(Arg.Any<WorkOSTokenSource>(), Arg.Any<CancellationToken>())
                   .Returns(Task.FromResult(ProvisionOffer.ExistingWorkspace("   ")));

        var outcome = await WorkOSDiscovery.RunAsync(
            "https://auth.kcap.ai", new ProxyConfigResponse { WorkOSClientId = "client_d" },
            proxy, Substitute.For<ITenantPicker>(),
            ()     => Task.FromResult<WorkOSAuthResponse?>(new WorkOSAuthResponse { AccessToken = "acc", RefreshToken = "rt" }),
            (_, _) => Task.FromResult<WorkOSAuthResponse?>(null),
            provisioner: provisioner);

        // Whitespace would resolve to "https://   .kcap.ai" downstream; refuse it here so the
        // caller never probes a nonsense host.
        await Assert.That(outcome.RetargetServerInput).IsNull();
        await Assert.That(outcome.ExitCode).IsEqualTo(1);
    }

    [Test]
    public async Task RunAsync_returns_1_without_legacy_error_when_provisioner_declines() {
        var proxy = Substitute.For<IAuthProxyClient>();
        proxy.DiscoverWorkOSTenantsAsync(Arg.Any<string>(), Arg.Any<string>())
             .Returns(Task.FromResult(new DiscoveryResult([], DiscoveryError.None)));

        var provisioner = Substitute.For<ITenantProvisioner>();
        provisioner.OfferCreateAsync(Arg.Any<WorkOSTokenSource>(), Arg.Any<CancellationToken>())
                   .Returns(Task.FromResult(ProvisionOffer.Declined));

        var switchCalled = false;
        var outcome = await WorkOSDiscovery.RunAsync(
            "https://auth.kcap.ai", new ProxyConfigResponse { WorkOSClientId = "client_d" },
            proxy, Substitute.For<ITenantPicker>(),
            ()     => Task.FromResult<WorkOSAuthResponse?>(new WorkOSAuthResponse { AccessToken = "acc", RefreshToken = "rt" }),
            (_, _) => { switchCalled = true; return Task.FromResult<WorkOSAuthResponse?>(null); },
            provisioner: provisioner);

        await Assert.That(outcome.ExitCode).IsEqualTo(1);
        await Assert.That(switchCalled).IsFalse();
    }
}
