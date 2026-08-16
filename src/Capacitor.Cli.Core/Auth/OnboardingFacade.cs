using Capacitor.Cli.Core.Config;
using Config_Profile = Capacitor.Cli.Core.Config.Profile;

namespace Capacitor.Cli.Core.Auth;

/// <summary>
/// One durable publication set: the config mutation (profiles + the provider stamp for every
/// identity) followed by the token writes. <see cref="PublishTokens"/> returns the username to
/// report, because on the discovery path it is only known once the tokens are exchanged.
/// </summary>
sealed record CommitRequest(
    IReadOnlyList<AuthIdentity>         Identities,
    string                              Provider,
    string                              ActiveProfile,
    string                              CanonicalServer,
    Func<ProfileConfig, ProfileConfig>? ConfigMutation,
    Func<Task<string?>>?                PublishTokens);

/// <summary>
/// The ordered commit boundary. The before-commit hook is the LAST cancellable await: it either
/// completes — after which every publication runs under <see cref="CancellationToken.None"/> to
/// completion and the answer is <see cref="AuthResult.Committed"/> even if a cancel arrives — or
/// the operation ends with nothing durable written. Crash residue is safe by this ordering:
/// claim-without-profile and profile-without-token both leave the start gate failing.
/// </summary>
static class CommitBoundary {
    internal static async Task<AuthResult> CommitAsync(
            CommitRequest                                              request,
            Func<IReadOnlyList<AuthIdentity>, CancellationToken, Task>? beforeCommit,
            IAuthProgress                                               progress,
            CancellationToken                                           ct) {
        if (ct.IsCancellationRequested) return new AuthResult.Cancelled();

        if (beforeCommit is not null) {
            try {
                await beforeCommit(request.Identities, ct);
            } catch (OperationCanceledException) {
                return new AuthResult.Cancelled();
            } catch (Exception ex) {
                progress.Error($"Error: sign-in could not be prepared: {ex.Message}");

                return new AuthResult.Failed(ex.Message);
            }
        }

        await ConfigMutator.MutateAsync(config => Stamp(request.ConfigMutation?.Invoke(config) ?? config, request),
            CancellationToken.None);

        var username = request.PublishTokens is null ? null : await request.PublishTokens();

        return new AuthResult.Committed(
            request.ActiveProfile, request.CanonicalServer, request.Provider, username, request.Identities);
    }

    static ProfileConfig Stamp(ProfileConfig config, CommitRequest request) {
        var profiles = new Dictionary<string, Config_Profile>(config.Profiles);

        foreach (var identity in request.Identities) {
            var profile = profiles.GetValueOrDefault(identity.Profile) ?? new Config_Profile();
            profiles[identity.Profile] = profile with {
                AuthProvider = new AuthProviderStamp(request.Provider, identity.CanonicalServer)
            };
        }

        return config with { Profiles = profiles };
    }

    /// <summary>Points a profile at the server only when it doesn't already name the same one.</summary>
    internal static ProfileConfig PointProfileAtServer(ProfileConfig config, string profileName, string serverUrl) {
        var existing = config.Profiles.GetValueOrDefault(profileName);

        if (existing is not null && ServerIdentity.SameServer(existing.ServerUrl, serverUrl)) return config;

        return config with {
            Profiles = new Dictionary<string, Config_Profile>(config.Profiles) {
                [profileName] = (existing ?? new Config_Profile()) with { ServerUrl = AppConfig.NormalizeUrl(serverUrl) }
            }
        };
    }
}

/// <summary>
/// GUI-neutral onboarding operations over the existing auth flows: sign in to a known server, or
/// discover and join a tenant. Every step up to the commit boundary is cancellable and renders
/// through <paramref name="progress"/> — nothing here touches the console.
/// </summary>
/// <param name="beforeCommit">
/// Runs with every identity the boundary is about to publish, before anything durable exists;
/// throwing aborts the operation with nothing written (the caller may retry).
/// </param>
/// <param name="httpFactory">
/// Supplies the client for every HTTP leg. A supplied factory owns its clients' lifetime; the
/// default creates and disposes one per operation.
/// </param>
public sealed class OnboardingFacade(
        IAuthProgress                                               progress,
        ITenantPicker                                               picker,
        ITenantProvisioner?                                         provisioner,
        Func<IReadOnlyList<AuthIdentity>, CancellationToken, Task>? beforeCommit,
        Func<HttpClient>?                                           httpFactory = null) {
    /// <summary>Test seam for the one WorkOS effect with no HTTP surface (loopback browser + OidcClient).</summary>
    internal Func<CancellationToken, Task<WorkOSAuthResponse?>>? WorkOSOrglessLogin { get; init; }

    public async Task<AuthResult> LoginAsync(string serverUrl, bool forceDevice, string? profile, CancellationToken ct) {
        var http = httpFactory?.Invoke() ?? new HttpClient();

        try {
            return await GuardAsync(() => LoginCoreAsync(http, serverUrl, forceDevice, profile, ct), ct);
        } finally {
            if (httpFactory is null) http.Dispose();
        }
    }

    /// <param name="provider"><see cref="AuthProvider.GitHubApp"/> or <see cref="AuthProvider.WorkOS"/>.</param>
    public async Task<AuthResult> DiscoverAsync(string provider, bool forceDevice, CancellationToken ct) {
        var http = httpFactory?.Invoke() ?? new HttpClient();

        try {
            return await GuardAsync(() => DiscoverCoreAsync(http, provider, forceDevice, ct), ct);
        } finally {
            if (httpFactory is null) http.Dispose();
        }
    }

    async Task<AuthResult> LoginCoreAsync(
            HttpClient http, string serverUrl, bool forceDevice, string? profile, CancellationToken ct) {
        var config = await OAuthLoginFlow.FetchAuthConfigAsync(http, serverUrl, ct, progress);

        if (config is null) return Stop($"Failed to fetch auth config from {serverUrl}/auth/config", ct);

        var targetProfile = profile ?? await TokenStore.ResolveActiveProfileAsync(ct);

        if (!ServerIdentity.TryCanonicalizeForStamping(serverUrl, out var canonical, out var identityError)) {
            progress.Error($"Error: {identityError}");

            return Stop(identityError, ct);
        }

        return config.Provider switch {
            AuthProvider.None      => await LoginNoneAsync(serverUrl, targetProfile, canonical, ct),
            AuthProvider.GitHubApp => await LoginGitHubAsync(http, serverUrl, config, forceDevice, targetProfile, canonical, ct),
            AuthProvider.WorkOS    => await LoginWorkOSAsync(serverUrl, config, targetProfile, canonical, ct),
            _                      => UnknownProvider(config.Provider)
        };
    }

    async Task<AuthResult> LoginNoneAsync(string serverUrl, string targetProfile, string canonical, CancellationToken ct) {
        var request = new CommitRequest(
            [new AuthIdentity(targetProfile, canonical)], AuthProvider.None, targetProfile, canonical,
            ConfigMutation: config => CommitBoundary.PointProfileAtServer(config, targetProfile, serverUrl),
            PublishTokens: null);

        var result = await CommitBoundary.CommitAsync(request, beforeCommit, progress, ct);

        if (result is AuthResult.Committed) {
            progress.Notice("Server has no authentication configured — login not required.");
        }

        return result;
    }

    async Task<AuthResult> LoginGitHubAsync(
            HttpClient http, string serverUrl, AuthDiscoveryResponse config, bool forceDevice,
            string targetProfile, string canonical, CancellationToken ct) {
        var accessToken = await OAuthLoginFlow.AcquireGitHubTokenAsync(
            http, config.GithubClientId!, config.GithubCodeExchangeUrl, forceDevice, ct, progress);

        if (accessToken is null) return Stop("GitHub sign-in did not complete.", ct);

        var exchanged = await OAuthLoginFlow.ExchangeAsync(
            http, serverUrl, accessToken, config.Provider, targetProfile, progress, ct);

        if (exchanged is null) return Stop("Token exchange failed.", ct);

        return await CommitTokensAsync(exchanged.Value.Tokens, exchanged.Value.Username, config.Provider, targetProfile, canonical, ct);
    }

    async Task<AuthResult> LoginWorkOSAsync(
            string serverUrl, AuthDiscoveryResponse config, string targetProfile, string canonical, CancellationToken ct) {
        var authenticated = await OAuthLoginFlow.WorkOSTokensForServerAsync(
            serverUrl, config.ClientId!, config.OrganizationId, new LoopbackBrowser(progress: progress), ct, progress);

        if (authenticated is null) return Stop("WorkOS sign-in did not complete.", ct);

        return await CommitTokensAsync(
            authenticated.Value.Tokens, authenticated.Value.Username, AuthProvider.WorkOS, targetProfile, canonical, ct);
    }

    async Task<AuthResult> CommitTokensAsync(
            StoredTokens tokens, string? username, string provider, string targetProfile, string canonical, CancellationToken ct) {
        var request = new CommitRequest(
            [new AuthIdentity(targetProfile, canonical)], provider, targetProfile, canonical,
            ConfigMutation: null,
            PublishTokens: async () => {
                await TokenStore.SaveAsync(targetProfile, tokens, CancellationToken.None);

                return username;
            });

        var result = await CommitBoundary.CommitAsync(request, beforeCommit, progress, ct);

        if (result is AuthResult.Committed) progress.Notice($"Logged in as {username}");

        return result;
    }

    async Task<AuthResult> DiscoverCoreAsync(HttpClient http, string provider, bool forceDevice, CancellationToken ct) {
        var proxy       = new AuthProxyClient(http);
        var proxyConfig = await proxy.GetConfigAsync(AuthProxyEndpoint.Url, ct);

        if (proxyConfig is null) {
            progress.Error("Cannot reach the Kurrent auth service.");

            return Stop("Cannot reach the Kurrent auth service.", ct);
        }

        return provider switch {
            AuthProvider.WorkOS    => await DiscoverWorkOSAsync(http, proxy, proxyConfig, ct),
            AuthProvider.GitHubApp => await DiscoverGitHubAsync(http, proxy, proxyConfig, forceDevice, ct),
            _                      => UnknownProvider(provider)
        };
    }

    async Task<AuthResult> DiscoverWorkOSAsync(
            HttpClient http, IAuthProxyClient proxy, ProxyConfigResponse proxyConfig, CancellationToken ct) {
        var clientId = proxyConfig.WorkOSClientId ?? "";

        var flow = await WorkOSDiscovery.DiscoverAsync(
            AuthProxyEndpoint.Url, proxyConfig, proxy, picker,
            orglessLogin: () => WorkOSOrglessLogin is not null
                ? WorkOSOrglessLogin(ct)
                : OAuthLoginFlow.AuthenticateWorkOSAsync(
                    clientId, organizationId: null, new LoopbackBrowser(progress: progress), ct: ct, progress: progress),
            orgSwitch: (refreshToken, organizationId) => OAuthLoginFlow.SwitchWorkOSOrgAsync(
                http, OAuthLoginFlow.WorkOSApiBase, clientId, refreshToken, organizationId, ct),
            orglessRefresh: (refreshToken, refreshCt) => OAuthLoginFlow.RefreshWorkOSTokenAsync(
                http, OAuthLoginFlow.WorkOSApiBase, clientId, refreshToken, refreshCt),
            provisioner: provisioner,
            ct: ct,
            progress: progress);

        return flow switch {
            WorkOSDiscoveryFlow.Ready ready       => await WorkOSDiscovery.PublishAsync(ready, progress, beforeCommit, ct),
            WorkOSDiscoveryFlow.Retarget retarget => new AuthResult.Retarget(retarget.ServerInput),
            WorkOSDiscoveryFlow.Failed failed     => Stop(failed.Message, ct),
            _                                     => Stop("No Capacitor tenants are linked to your account.", ct)
        };
    }

    async Task<AuthResult> DiscoverGitHubAsync(
            HttpClient http, IAuthProxyClient proxy, ProxyConfigResponse proxyConfig, bool forceDevice, CancellationToken ct) {
        if (string.IsNullOrEmpty(proxyConfig.GitHubClientId)) {
            progress.Error("Cannot reach the Kurrent auth service.");

            return Stop("Cannot reach the Kurrent auth service.", ct);
        }

        var accessToken = await OAuthLoginFlow.AcquireGitHubTokenAsync(
            http, proxyConfig.GitHubClientId, proxyConfig.GitHubCodeExchangeUrl, forceDevice, ct, progress);

        if (accessToken is null) return Stop("GitHub sign-in did not complete.", ct);

        var outcome = await new TenantDiscovery(proxy, picker).RunAsync(AuthProxyEndpoint.Url, accessToken, ct);

        if (outcome.ErrorMessage is not null) {
            progress.Error(outcome.ErrorMessage);

            return Stop(outcome.ErrorMessage, ct);
        }

        var identities = new List<AuthIdentity>();

        foreach (var tenant in outcome.Tenants) {
            if (!ServerIdentity.TryCanonicalizeForStamping(tenant.Origin, out var canonical, out var identityError)) {
                progress.Error($"Error: {identityError}");

                return Stop(identityError, ct);
            }

            identities.Add(new(tenant.ProfileName, canonical));
        }

        var picked = outcome.Picked!;

        var request = new CommitRequest(
            identities, AuthProvider.GitHubApp, picked.ProfileName,
            identities.First(i => i.Profile == picked.ProfileName).CanonicalServer,
            ConfigMutation: config => TenantDiscovery.MergeProfiles(config, outcome.Tenants, picked),
            PublishTokens: () => ExchangeEveryTenantAsync(http, outcome.Tenants, picked, accessToken));

        return await CommitBoundary.CommitAsync(request, beforeCommit, progress, ct);
    }

    // Inside the boundary: each tenant's exchange is network-then-save, and a failure costs that
    // tenant its token (today's per-tenant warning) rather than the whole commit.
    async Task<string?> ExchangeEveryTenantAsync(
            HttpClient http, DiscoveredTenant[] tenants, DiscoveredTenant picked, string githubAccessToken) {
        string? pickedUsername = null;

        foreach (var tenant in tenants) {
            var exchanged = await OAuthLoginFlow.ExchangeAsync(
                http, AppConfig.NormalizeUrl(tenant.Origin), githubAccessToken, AuthProvider.GitHubApp,
                tenant.ProfileName, progress, CancellationToken.None);

            if (exchanged is null) {
                progress.Error(
                    $"Warning: token exchange failed for {tenant.ProfileName}. Run 'kcap login' after switching to that profile.");

                continue;
            }

            await TokenStore.SaveAsync(tenant.ProfileName, exchanged.Value.Tokens, CancellationToken.None);

            if (tenant.ProfileName == picked.ProfileName) pickedUsername = exchanged.Value.Username;
        }

        return pickedUsername;
    }

    AuthResult.Failed UnknownProvider(string provider) {
        progress.Error($"Error: Unknown auth provider '{provider}'. Update your kcap CLI.");

        return new AuthResult.Failed($"Unknown auth provider '{provider}'");
    }

    static async Task<AuthResult> GuardAsync(Func<Task<AuthResult>> operation, CancellationToken ct) {
        try {
            return await operation();
        } catch (OperationCanceledException) when (ct.IsCancellationRequested) {
            return new AuthResult.Cancelled();
        }
    }

    // A pre-boundary failure under a live cancel IS a cancel: the proxy client and the WorkOS
    // refresh map OperationCanceledException onto their own failure results.
    static AuthResult Stop(string message, CancellationToken ct) =>
        ct.IsCancellationRequested ? new AuthResult.Cancelled() : new AuthResult.Failed(message);
}
