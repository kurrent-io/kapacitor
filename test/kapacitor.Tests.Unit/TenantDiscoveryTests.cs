using kapacitor.Auth;
using kapacitor.Config;
using NSubstitute;
using Profile = kapacitor.Config.Profile;
using DiscoveryResult = kapacitor.Auth.DiscoveryResult;

namespace kapacitor.Tests.Unit;

public class TenantDiscoveryTests {
    [Test]
    public async Task RunAsync_auto_picks_single_tenant() {
        var proxy = Substitute.For<IAuthProxyClient>();
        proxy.DiscoverTenantsAsync(Arg.Any<string>(), Arg.Any<string>())
             .Returns(Task.FromResult(new DiscoveryResult(
                 [new DiscoveredTenant { OrgId = 1, OrgLogin = "solo", Origin = "https://solo.example" }],
                 DiscoveryError.None)));

        var picker    = Substitute.For<ITenantPicker>();
        var discovery = new TenantDiscovery(proxy, picker);

        var outcome = await discovery.RunAsync("https://proxy", "gh");

        await Assert.That(outcome.Picked).IsNotNull();
        await Assert.That(outcome.Picked!.OrgLogin).IsEqualTo("solo");
        picker.DidNotReceive().Pick(Arg.Any<DiscoveredTenant[]>());
    }

    [Test]
    public async Task RunAsync_delegates_to_picker_when_multiple_tenants() {
        DiscoveredTenant[] list = [
            new() { OrgId = 1, OrgLogin = "acme",    Origin = "https://a.example" },
            new() { OrgId = 2, OrgLogin = "contoso", Origin = "https://b.example" }
        ];
        var proxy = Substitute.For<IAuthProxyClient>();
        proxy.DiscoverTenantsAsync(Arg.Any<string>(), Arg.Any<string>())
             .Returns(Task.FromResult(new DiscoveryResult(list, DiscoveryError.None)));

        var picker = Substitute.For<ITenantPicker>();
        picker.Pick(list).Returns(list[1]);

        var discovery = new TenantDiscovery(proxy, picker);
        var outcome = await discovery.RunAsync("https://proxy", "gh");

        await Assert.That(outcome.Picked!.OrgLogin).IsEqualTo("contoso");
    }

    [Test]
    public async Task RunAsync_returns_empty_tenant_error_message() {
        var proxy = Substitute.For<IAuthProxyClient>();
        proxy.DiscoverTenantsAsync(Arg.Any<string>(), Arg.Any<string>())
             .Returns(Task.FromResult(new DiscoveryResult([], DiscoveryError.None)));

        var discovery = new TenantDiscovery(proxy, Substitute.For<ITenantPicker>());

        var outcome = await discovery.RunAsync("https://proxy", "gh");

        await Assert.That(outcome.Picked).IsNull();
        await Assert.That(outcome.ErrorMessage!).Contains("No Capacitor tenants");
    }

    [Test]
    public async Task RunAsync_returns_proxy_unreachable_error() {
        var proxy = Substitute.For<IAuthProxyClient>();
        proxy.DiscoverTenantsAsync(Arg.Any<string>(), Arg.Any<string>())
             .Returns(Task.FromResult(new DiscoveryResult([], DiscoveryError.ProxyUnreachable)));

        var discovery = new TenantDiscovery(proxy, Substitute.For<ITenantPicker>());
        var outcome = await discovery.RunAsync("https://proxy", "gh");

        await Assert.That(outcome.ErrorMessage!).Contains("unreachable");
    }

    [Test]
    public async Task RunAsync_returns_token_rejected_error() {
        var proxy = Substitute.For<IAuthProxyClient>();
        proxy.DiscoverTenantsAsync(Arg.Any<string>(), Arg.Any<string>())
             .Returns(Task.FromResult(new DiscoveryResult([], DiscoveryError.TokenRejected)));

        var discovery = new TenantDiscovery(proxy, Substitute.For<ITenantPicker>());
        var outcome = await discovery.RunAsync("https://proxy", "gh");

        await Assert.That(outcome.ErrorMessage!).Contains("GitHub rejected");
    }

    [Test]
    public async Task RunAsync_returns_upstream_error_message() {
        var proxy = Substitute.For<IAuthProxyClient>();
        proxy.DiscoverTenantsAsync(Arg.Any<string>(), Arg.Any<string>())
             .Returns(Task.FromResult(new DiscoveryResult([], DiscoveryError.UpstreamError)));

        var discovery = new TenantDiscovery(proxy, Substitute.For<ITenantPicker>());
        var outcome = await discovery.RunAsync("https://proxy", "gh");

        await Assert.That(outcome.ErrorMessage!).Contains("returned an error");
    }

    [Test]
    public async Task RunAsync_returns_no_tenant_selected_when_picker_returns_null() {
        DiscoveredTenant[] list = [
            new() { OrgId = 1, OrgLogin = "acme",    Origin = "https://a.example" },
            new() { OrgId = 2, OrgLogin = "contoso", Origin = "https://b.example" }
        ];
        var proxy = Substitute.For<IAuthProxyClient>();
        proxy.DiscoverTenantsAsync(Arg.Any<string>(), Arg.Any<string>())
             .Returns(Task.FromResult(new DiscoveryResult(list, DiscoveryError.None)));

        var picker = Substitute.For<ITenantPicker>();
        picker.Pick(list).Returns((DiscoveredTenant?)null);

        var discovery = new TenantDiscovery(proxy, picker);
        var outcome = await discovery.RunAsync("https://proxy", "gh");

        await Assert.That(outcome.Picked).IsNull();
        await Assert.That(outcome.ErrorMessage).IsEqualTo("No tenant selected.");
        await Assert.That(outcome.Tenants.Length).IsEqualTo(2);
    }

    [Test]
    public async Task MergeProfiles_creates_profile_per_tenant_and_sets_active() {
        var existing = new ProfileConfig {
            ActiveProfile = "default",
            Profiles = new Dictionary<string, Profile> {
                ["default"] = new() { DefaultVisibility = "org_public" }
            }
        };
        DiscoveredTenant[] discovered = [
            new() { OrgId = 1, OrgLogin = "acme",    Origin = "https://a.example" },
            new() { OrgId = 2, OrgLogin = "contoso", Origin = "https://b.example" }
        ];

        var merged = TenantDiscovery.MergeProfiles(existing, discovered, discovered[0]);

        await Assert.That(merged.ActiveProfile).IsEqualTo("acme");
        await Assert.That(merged.Profiles["acme"].ServerUrl).IsEqualTo("https://a.example");
        await Assert.That(merged.Profiles["contoso"].ServerUrl).IsEqualTo("https://b.example");
        await Assert.That(merged.Profiles["acme"].DefaultVisibility).IsEqualTo("org_public");
    }

    [Test]
    public async Task MergeProfiles_inherits_from_active_profile_when_not_default() {
        var existing = new ProfileConfig {
            ActiveProfile = "acme",
            Profiles = new Dictionary<string, Profile> {
                ["acme"]    = new() { ServerUrl = "https://a.example", DefaultVisibility = "private" },
                ["default"] = new() { DefaultVisibility = "org_public" }
            }
        };
        DiscoveredTenant[] discovered = [
            new() { OrgId = 1, OrgLogin = "acme",   Origin = "https://a.example" },
            new() { OrgId = 2, OrgLogin = "newly",  Origin = "https://n.example" }
        ];

        var merged = TenantDiscovery.MergeProfiles(existing, discovered, discovered[1]);

        // newly-discovered "newly" profile should inherit settings from active "acme", NOT from "default"
        await Assert.That(merged.Profiles["newly"].DefaultVisibility).IsEqualTo("private");
        await Assert.That(merged.Profiles["newly"].ServerUrl).IsEqualTo("https://n.example");
        await Assert.That(merged.ActiveProfile).IsEqualTo("newly");
    }

    [Test]
    public async Task MergeProfiles_preserves_existing_profile_settings() {
        var existing = new ProfileConfig {
            Profiles = new Dictionary<string, Profile> {
                ["acme"] = new() { ServerUrl = "https://old", DefaultVisibility = "private" }
            }
        };
        DiscoveredTenant[] discovered = [
            new() { OrgId = 1, OrgLogin = "acme", Origin = "https://new.example" }
        ];

        var merged = TenantDiscovery.MergeProfiles(existing, discovered, discovered[0]);

        await Assert.That(merged.Profiles["acme"].ServerUrl).IsEqualTo("https://new.example");
        await Assert.That(merged.Profiles["acme"].DefaultVisibility).IsEqualTo("private");
    }
}
