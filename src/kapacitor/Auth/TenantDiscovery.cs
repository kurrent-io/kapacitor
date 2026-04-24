using kapacitor.Config;
using Profile = kapacitor.Config.Profile;

namespace kapacitor.Auth;

public sealed record DiscoveryOutcome(
    DiscoveredTenant[] Tenants,
    DiscoveredTenant?  Picked,
    string?            ErrorMessage
);

public interface ITenantPicker {
    DiscoveredTenant? Pick(DiscoveredTenant[] tenants);
}

public class TenantDiscovery(IAuthProxyClient proxy, ITenantPicker picker) {
    public async Task<DiscoveryOutcome> RunAsync(string proxyUrl, string githubAccessToken) {
        var result = await proxy.DiscoverTenantsAsync(proxyUrl, githubAccessToken);

        if (result.Error != DiscoveryError.None) {
            return new([], null, result.Error switch {
                DiscoveryError.ProxyUnreachable => "The Kurrent auth service is unreachable.",
                DiscoveryError.TokenRejected    => "GitHub rejected the authentication token. Please sign in again.",
                DiscoveryError.UpstreamError    => "Kurrent auth service could not reach GitHub. Try again later.",
                _                               => "Tenant discovery failed."
            });
        }

        if (result.Tenants.Length == 0) {
            return new([], null,
                "No Capacitor tenants are linked to your GitHub orgs. Ask your admin to install the Kurrent GitHub App on your org, or re-run with --server-url <url> for a self-hosted server.");
        }

        var picked = result.Tenants.Length == 1
            ? result.Tenants[0]
            : picker.Pick(result.Tenants);

        if (picked is null) {
            return new(result.Tenants, null, "No tenant selected.");
        }

        return new(result.Tenants, picked, null);
    }

    public static ProfileConfig MergeProfiles(
            ProfileConfig      existing,
            DiscoveredTenant[] discovered,
            DiscoveredTenant   active) {
        var profiles = new Dictionary<string, Profile>(existing.Profiles);
        var template = existing.Profiles.GetValueOrDefault(existing.ActiveProfile)
                    ?? existing.Profiles.GetValueOrDefault("default")
                    ?? new Profile();

        foreach (var t in discovered) {
            var name = t.OrgLogin;
            profiles[name] = (profiles.GetValueOrDefault(name) ?? template) with {
                ServerUrl = AppConfig.NormalizeUrl(t.Origin)
            };
        }

        return existing with {
            Profiles      = profiles,
            ActiveProfile = active.OrgLogin
        };
    }
}
