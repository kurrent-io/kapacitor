using Capacitor.Cli.Core.Config;

namespace Capacitor.Cli.Core.Auth;

/// <summary>
/// WorkOS tenant discovery: authenticate org-less against the proxy's shared AuthKit app,
/// list the user's tenants via the proxy, let them pick, then org-switch into the chosen org
/// and save an org-scoped profile. The two browser/HTTP effects (org-less login, org-switch)
/// are injected so the orchestration (discover → pick → switch → save) is unit-testable;
/// production wiring passes <see cref="OAuthLoginFlow"/>'s loopback + switch helpers.
/// </summary>
public static class WorkOSDiscovery {
    const string WorkOSApiBase = "https://api.workos.com";

    /// <summary>
    /// <see cref="RunAsync"/> wired to the real WorkOS effects: an org-less loopback login on the
    /// shared AuthKit app + a refresh-token org-switch (both public-client, no secret). The two call
    /// sites (`kcap login --discover` and `kcap setup`) use this; tests call <see cref="RunAsync"/>
    /// directly with fakes.
    /// </summary>
    public static Task<int> RunWithLiveAuthAsync(
            string proxyUrl, ProxyConfigResponse proxyConfig, IAuthProxyClient proxy, ITenantPicker picker,
            ITenantProvisioner? provisioner = null) {
        var clientId = proxyConfig.WorkOSClientId ?? "";

        return RunAsync(proxyUrl, proxyConfig, proxy, picker,
            orglessLogin: () => OAuthLoginFlow.AuthenticateWorkOSAsync(clientId, organizationId: null, new LoopbackBrowser()),
            orgSwitch: async (refreshToken, organizationId) => {
                using var http = new HttpClient();
                return await OAuthLoginFlow.SwitchWorkOSOrgAsync(http, WorkOSApiBase, clientId, refreshToken, organizationId);
            },
            orglessRefresh: async (refreshToken, _) => {
                using var http = new HttpClient();
                return await OAuthLoginFlow.RefreshWorkOSTokenAsync(http, WorkOSApiBase, clientId, refreshToken);
            },
            provisioner: provisioner);
    }

    public static async Task<int> RunAsync(
            string                                          proxyUrl,
            ProxyConfigResponse                             proxyConfig,
            IAuthProxyClient                                proxy,
            ITenantPicker                                   picker,
            Func<Task<WorkOSAuthResponse?>>                 orglessLogin,
            Func<string, string, Task<WorkOSAuthResponse?>> orgSwitch,     // args: refreshToken, organizationId
            Func<string, CancellationToken, Task<WorkOSAuthResponse?>>? orglessRefresh = null, // args: refreshToken, ct
            ITenantProvisioner?                             provisioner = null) {
        if (string.IsNullOrEmpty(proxyConfig.WorkOSClientId)) {
            await Console.Error.WriteLineAsync("This server isn't configured for WorkOS sign-in.");

            return 1;
        }

        var auth = await orglessLogin();
        if (auth is null || string.IsNullOrEmpty(auth.RefreshToken)) {
            await Console.Error.WriteLineAsync("WorkOS sign-in failed.");

            return 1;
        }

        var result = await proxy.DiscoverWorkOSTenantsAsync(proxyUrl, auth.AccessToken);
        if (result.Error != DiscoveryError.None) {
            await Console.Error.WriteLineAsync(result.Error switch {
                DiscoveryError.ProxyUnreachable => "The Kurrent auth service is unreachable.",
                DiscoveryError.TokenRejected    => "WorkOS rejected the authentication token. Please sign in again.",
                DiscoveryError.UpstreamError    => "Kurrent auth service returned an error. Try again later.",
                _                               => "Tenant discovery failed."
            });

            return 1;
        }

        if (result.Tenants.Length == 0) {
            if (provisioner is null) {
                await Console.Error.WriteLineAsync("No Capacitor tenants are linked to your account. Ask your admin to invite you.");

                return 1;
            }

            // Provisioning + polling can run for minutes, outliving WorkOS's ~5-minute access-token
            // TTL, so hand the provisioner a refreshing token source rather than the login-time token.
            var tokens = new WorkOSTokenSource(
                auth.AccessToken, auth.RefreshToken,
                orglessRefresh ?? ((_, _) => Task.FromResult<WorkOSAuthResponse?>(null)));
            var offer = await provisioner.OfferCreateAsync(tokens);
            if (offer.Status != ProvisionOfferStatus.Created || offer.Tenant is null) {
                // Declined / InProgress / Failed — the provisioner already printed the
                // outcome-appropriate message; don't stack the legacy dead-end on top.
                return 1;
            }

            var created = new DiscoveredTenant {
                Provider       = AuthProvider.WorkOS,
                OrganizationId = offer.Tenant.OrganizationId,
                Slug           = offer.Tenant.Slug,
                DisplayName    = offer.Tenant.DisplayName,
                Origin         = offer.Tenant.Origin
            };
            // Polling may have rotated the org-less refresh token; the org-switch must use the
            // current one (WorkOS invalidates the old on refresh) or the final switch would 401.
            var authForSwitch = auth with { RefreshToken = tokens.CurrentRefreshToken ?? auth.RefreshToken };
            return await SwitchAndSaveAsync(created, [created], authForSwitch, proxyConfig.WorkOSClientId!, orgSwitch);
        }

        var picked = result.Tenants.Length == 1 ? result.Tenants[0] : picker.Pick(result.Tenants);
        if (picked is null) {
            await Console.Error.WriteLineAsync("No tenant selected.");

            return 1;
        }

        return await SwitchAndSaveAsync(picked, result.Tenants, auth, proxyConfig.WorkOSClientId!, orgSwitch);
    }

    // Org-switch into the chosen tenant, persist its profile + org-bound tokens.
    // Shared by the picked-tenant path and the freshly-provisioned-tenant path.
    static async Task<int> SwitchAndSaveAsync(
            DiscoveredTenant                                picked,
            DiscoveredTenant[]                              tenants,
            WorkOSAuthResponse                              auth,
            string                                          clientId,
            Func<string, string, Task<WorkOSAuthResponse?>> orgSwitch) {
        if (string.IsNullOrEmpty(picked.OrganizationId)) {
            await Console.Error.WriteLineAsync($"Tenant {picked.Label} is missing an organization id; cannot complete sign-in.");

            return 1;
        }

        // Org-switch once into the chosen org. The resulting refresh token stays org-bound
        // (spike-confirmed), so later refreshes need no organization_id.
        var switched = await orgSwitch(auth.RefreshToken!, picked.OrganizationId);
        if (switched is null) {
            await Console.Error.WriteLineAsync($"Could not switch to organization {picked.Label}.");

            return 1;
        }

        var username = OAuthLoginFlow.WorkOSDisplayName(auth.User);

        var cfg = await AppConfig.LoadProfileConfig();
        cfg = TenantDiscovery.MergeProfiles(cfg, tenants, picked);
        await AppConfig.SaveProfileConfig(cfg);

        await TokenStore.SaveAsync(
            picked.ProfileName,
            new StoredTokens {
                AccessToken    = switched.AccessToken,
                RefreshToken   = switched.RefreshToken,
                ExpiresAt      = TokenStore.JwtExpiry(switched.AccessToken),
                GitHubUsername = username,
                Provider       = AuthProvider.WorkOS,
                ClientId       = clientId,
                // The tenant's own origin: this token is org-scoped to the tenant we just switched
                // into, and only that tenant's server will accept it.
                ServerUrl      = ServerIdentity.Canonicalize(picked.Origin)
            });

        await Console.Out.WriteLineAsync($"Logged in as {username} → {picked.Label}");

        return 0;
    }
}
