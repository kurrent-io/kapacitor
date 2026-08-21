using Capacitor.Cli.Core.Auth;
using Duende.IdentityModel.OidcClient.Browser;
using NSubstitute;

namespace Capacitor.Tests.Helpers;

// Shared by the Core façade tests and the CLI login/setup parity tests, which sit in different
// assemblies and so cannot reach a fixture declared in either one.
public static class AuthFixtures {
    public static OnboardingFacade NewFacade(
            IAuthProgress                                               progress,
            HttpMessageHandler                                          handler,
            ITenantPicker?                                              picker        = null,
            ITenantProvisioner?                                         provisioner   = null,
            Func<IReadOnlyList<AuthIdentity>, CancellationToken, Task>? beforeCommit  = null,
            Func<CancellationToken, Task<WorkOSAuthResponse?>>?         workosLogin   = null,
            IBrowser?                                                   workosBrowser = null,
            string?                                                     workosApiBase = null) {
        var http = new HttpClient(handler, disposeHandler: false);

        return new OnboardingFacade(progress, picker ?? Substitute.For<ITenantPicker>(), provisioner, beforeCommit, () => http) {
            WorkOSOrglessLogin    = workosLogin,
            WorkOSBrowser         = workosBrowser,
            WorkOSApiBaseOverride = workosApiBase
        };
    }

    public static ITenantPicker PickerReturningFirst() {
        var picker = Substitute.For<ITenantPicker>();
        picker.PickAsync(Arg.Any<DiscoveredTenant[]>(), Arg.Any<CancellationToken>())
              .Returns(ci => Task.FromResult<DiscoveredTenant?>(ci.Arg<DiscoveredTenant[]>()[0]));

        return picker;
    }

    public const string TwoGitHubTenants = """
        [{"org_id":1,"org_login":"acme","origin":"https://acme.kcap.ai"},
         {"org_id":2,"org_login":"contoso","origin":"https://contoso.kcap.ai"}]
        """;
}
