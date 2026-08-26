using Capacitor.Cli.Core.Config;
using Config_Profile = Capacitor.Cli.Core.Config.Profile;

namespace Capacitor.Cli.Core.Auth;

/// <param name="NoTenantsFound">
/// True only for the GENUINE "authenticated, zero tenants" case — never for a discovery-service
/// failure (proxy unreachable, token rejected, upstream error), which also returns an empty
/// <paramref name="Tenants"/> array and a non-null <paramref name="ErrorMessage"/>. Both shapes
/// look identical from <see cref="Tenants"/>.Length alone, so a caller emitting
/// telemetry off that length was inflating the funnel's key "reached signup" denominator with
/// discovery outages that have nothing to do with signup.
/// </param>
public sealed record DiscoveryOutcome(
    DiscoveredTenant[] Tenants,
    DiscoveredTenant?  Picked,
    string?            ErrorMessage,
    bool               NoTenantsFound = false
);

public interface ITenantPicker {
    DiscoveredTenant?       Pick(DiscoveredTenant[] tenants);
    Task<DiscoveredTenant?> PickAsync(DiscoveredTenant[] tenants, CancellationToken ct);
}

public class TenantDiscovery(IAuthProxyClient proxy, ITenantPicker picker) {
    public async Task<DiscoveryOutcome> RunAsync(string proxyUrl, string githubAccessToken, CancellationToken ct = default) {
        var result = await proxy.DiscoverTenantsAsync(proxyUrl, githubAccessToken, ct);

        if (result.Error != DiscoveryError.None) {
            return new([], null, result.Error switch {
                DiscoveryError.ProxyUnreachable => "The Kurrent auth service is unreachable.",
                DiscoveryError.TokenRejected    => "GitHub rejected the authentication token. Please sign in again.",
                DiscoveryError.UpstreamError    => "Kurrent auth service returned an error. Try again later.",
                _                               => "Tenant discovery failed."
            });
        }

        if (result.Tenants.Length == 0) {
            return new([], null,
                "No Capacitor tenants are linked to your GitHub orgs. Ask your admin to install the Kurrent GitHub App on your org, or re-run with --server-url <url> for a self-hosted server.",
                NoTenantsFound: true);
        }

        var picked = result.Tenants.Length == 1
            ? result.Tenants[0]
            : await picker.PickAsync(result.Tenants, ct);

        if (picked is null) {
            return new(result.Tenants, null, "No tenant selected.");
        }

        return new(result.Tenants, picked, null);
    }

    public static ProfileConfig MergeProfiles(
            ProfileConfig      existing,
            DiscoveredTenant[] discovered,
            DiscoveredTenant   active) {
        var profiles      = new Dictionary<string, Config_Profile>(existing.Profiles);
        var activeProfile = existing.ActiveName;
        var template = existing.Profiles.GetValueOrDefault(activeProfile)
                    ?? existing.Profiles.GetValueOrDefault(ProfileConfig.DefaultName)
                    ?? new Config_Profile();

        foreach (var t in discovered) {
            var name = t.ProfileName;
            // Seed a brand-new tenant profile from the template, but never inherit the
            // template's remembered import org — that GitHub owner belongs to a different
            // tenant and would silently scope this tenant's `kcap import --org`. Existing
            // profiles keep their own ImportOrg.
            var basis = profiles.GetValueOrDefault(name) ?? template with { ImportOrg = null };
            profiles[name] = basis with {
                ServerUrl = AppConfig.NormalizeUrl(t.Origin)
            };
        }

        return existing with {
            Profiles      = profiles,
            ActiveProfile = active.ProfileName
        };
    }
}
