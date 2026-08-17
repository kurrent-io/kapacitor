using Capacitor.Cli.Core.Config;
using Duende.IdentityModel.OidcClient.Browser;
using Config_Profile = Capacitor.Cli.Core.Config.Profile;

namespace Capacitor.Cli.Core.Auth;

/// <summary>One durable publication set: the config mutation and provider stamp, followed by token writes tracked so a partial failure is known.</summary>
sealed record CommitRequest(
    IReadOnlyList<AuthIdentity>         Identities,
    string                              Provider,
    string                              ActiveProfile,
    string                              CanonicalServer,
    Func<ProfileConfig, ProfileConfig>? ConfigMutation,
    Func<Action, Task<string?>>?        PublishTokens,
    // False for a login that must not claim the profile for this server (see LoginTarget.Foreign).
    bool                                WriteStamp = true);

/// <summary>The ordered commit boundary: the before-commit hook is the last cancellable await, after which every publication is uncancellable.</summary>
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

        // A token-only arm has no config commit to make the boundary durable, so what landed is tracked rather than assumed.
        var configPublished = request.ConfigMutation is not null || request.WriteStamp;

        if (configPublished) {
            try {
                // Profile write and stamp are deliberately ONE mutation: no window where a profile exists unstamped.
                await ConfigMutator.MutateAsync(config => Stamp(request.ConfigMutation?.Invoke(config) ?? config, request),
                    CancellationToken.None);
            } catch (Exception ex) {
                // The config commit is the boundary's first durable step: if it threw, nothing was published.
                progress.Error($"Error: sign-in could not be saved: {ex.Message}");

                return new AuthResult.Failed(ex.Message);
            }
        }

        string? username   = null;
        var     tokenSaved = false;

        try {
            if (request.PublishTokens is not null) username = await request.PublishTokens(() => tokenSaved = true);
        } catch (Exception ex) {
            // Nothing durable exists, so Committed would be a lie — fail honestly instead.
            if (!configPublished && !tokenSaved) {
                progress.Error($"Error: sign-in could not be saved: {ex.Message}");

                return new AuthResult.Failed(ex.Message);
            }

            // Past a landed publication the boundary has begun; report what was lost rather than a torn stop.
            progress.Error($"Error: some credentials could not be saved: {ex.Message}");
        }

        return new AuthResult.Committed(
            request.ActiveProfile, request.CanonicalServer, request.Provider, username, request.Identities);
    }

    static ProfileConfig Stamp(ProfileConfig config, CommitRequest request) {
        if (!request.WriteStamp) return config;

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
/// What a known-server login may write. The stamp claims the profile for this server, so it is
/// written only when the profile already names it or the caller opted to adopt it; adopting also
/// writes <c>server_url</c>. A foreign profile with no adoption gets neither.
/// </summary>
sealed record LoginTarget(string Profile, string CanonicalServer, string ServerUrl, bool PointsAtServer, bool AdoptServer) {
    internal bool Adopting  => !PointsAtServer && AdoptServer;
    internal bool Foreign   => !PointsAtServer && !AdoptServer;
    internal bool WriteStamp => !Foreign;

    internal Func<ProfileConfig, ProfileConfig>? ConfigMutation =>
        Adopting ? config => CommitBoundary.PointProfileAtServer(config, Profile, ServerUrl) : null;
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
/// The client for every HTTP leg (the GitHub browser flow and OidcClient still make their own). A
/// supplied factory owns its clients' lifetime; the default creates and disposes one per operation.
/// </param>
public sealed class OnboardingFacade(
        IAuthProgress                                               progress,
        ITenantPicker                                               picker,
        ITenantProvisioner?                                         provisioner,
        Func<IReadOnlyList<AuthIdentity>, CancellationToken, Task>? beforeCommit,
        Func<HttpClient>?                                           httpFactory = null) {
    /// <summary>Test seam for the one WorkOS effect with no HTTP surface (loopback browser + OidcClient).</summary>
    internal Func<CancellationToken, Task<WorkOSAuthResponse?>>? WorkOSOrglessLogin { get; init; }

    /// <summary>Test seams for the known-server WorkOS login, whose OidcClient leg bypasses the http factory.</summary>
    internal IBrowser? WorkOSBrowser { get; init; }

    internal string? WorkOSApiBaseOverride { get; init; }

    /// <param name="adoptServer">
    /// When the profile doesn't already name this server: true writes its <c>server_url</c> and the
    /// provider stamp, false leaves config untouched (a <c>None</c> server then has nothing to sign in with).
    /// </param>
    public async Task<AuthResult> LoginAsync(
            string serverUrl, bool forceDevice, string? profile, CancellationToken ct, bool adoptServer = false) {
        var http = httpFactory?.Invoke() ?? new HttpClient();

        try {
            return await GuardAsync(() => LoginCoreAsync(http, serverUrl, forceDevice, profile, adoptServer, ct), ct);
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
            HttpClient http, string serverUrl, bool forceDevice, string? profile, bool adoptServer, CancellationToken ct) {
        var config = await OAuthLoginFlow.FetchAuthConfigAsync(http, serverUrl, ct, progress);

        if (config is null) return Stop($"Failed to fetch auth config from {serverUrl}/auth/config", ct);

        var targetProfile = profile ?? await TokenStore.ResolveActiveProfileAsync(ct);

        if (!ServerIdentity.TryCanonicalizeForStamping(serverUrl, out var canonical, out var identityError)) {
            return Fail($"Error: {identityError}", identityError, ct);
        }

        var configured = (await AppConfig.LoadProfileConfig(ct)).Profiles.GetValueOrDefault(targetProfile);
        var target     = new LoginTarget(
            targetProfile, canonical, serverUrl,
            PointsAtServer: ServerIdentity.SameServer(configured?.ServerUrl, serverUrl),
            AdoptServer: adoptServer);

        return config.Provider switch {
            AuthProvider.None      => await LoginNoneAsync(target, ct),
            AuthProvider.GitHubApp => await LoginGitHubAsync(http, config, forceDevice, target, ct),
            AuthProvider.WorkOS    => await LoginWorkOSAsync(config, target, ct),
            _                      => UnknownProvider(config.Provider)
        };
    }

    async Task<AuthResult> LoginNoneAsync(LoginTarget target, CancellationToken ct) {
        // Nothing to sign in with on a None server, so a profile that doesn't name it stays unconfigured.
        if (target.Foreign) {
            return Fail(
                $"Error: profile '{target.Profile}' is not configured for {target.ServerUrl} — pass --profile or run kcap setup.",
                $"Profile '{target.Profile}' is not configured for {target.ServerUrl}.", ct);
        }

        var request = new CommitRequest(
            [new AuthIdentity(target.Profile, target.CanonicalServer)], AuthProvider.None, target.Profile, target.CanonicalServer,
            ConfigMutation: target.ConfigMutation,
            PublishTokens: null);

        var result = await CommitBoundary.CommitAsync(request, beforeCommit, progress, ct);

        if (result is AuthResult.Committed) {
            progress.Notice("Server has no authentication configured — login not required.");
        }

        return result;
    }

    async Task<AuthResult> LoginGitHubAsync(
            HttpClient http, AuthDiscoveryResponse config, bool forceDevice, LoginTarget target, CancellationToken ct) {
        var accessToken = await OAuthLoginFlow.AcquireGitHubTokenAsync(
            http, config.GithubClientId!, config.GithubCodeExchangeUrl, forceDevice, ct, progress);

        if (accessToken is null) return Stop("GitHub sign-in did not complete.", ct, AuthFailureReason.SigninDenied);

        var exchanged = await OAuthLoginFlow.ExchangeAsync(
            http, target.ServerUrl, accessToken, config.Provider, target.Profile, progress, ct);

        if (exchanged is null) return Stop("Token exchange failed.", ct);

        return await CommitTokensAsync(exchanged.Value.Tokens, exchanged.Value.Username, config.Provider, target, ct);
    }

    async Task<AuthResult> LoginWorkOSAsync(AuthDiscoveryResponse config, LoginTarget target, CancellationToken ct) {
        var authenticated = await OAuthLoginFlow.WorkOSTokensForServerAsync(
            target.ServerUrl, config.ClientId!, config.OrganizationId,
            WorkOSBrowser ?? new LoopbackBrowser(progress: progress), ct, progress,
            WorkOSApiBaseOverride ?? OAuthLoginFlow.WorkOSApiBase);

        if (authenticated is null) return Stop("WorkOS sign-in did not complete.", ct, AuthFailureReason.SigninDenied);

        return await CommitTokensAsync(
            authenticated.Value.Tokens, authenticated.Value.Username, AuthProvider.WorkOS, target, ct);
    }

    async Task<AuthResult> CommitTokensAsync(
            StoredTokens tokens, string? username, string provider, LoginTarget target, CancellationToken ct) {
        var request = new CommitRequest(
            [new AuthIdentity(target.Profile, target.CanonicalServer)], provider, target.Profile, target.CanonicalServer,
            ConfigMutation: target.ConfigMutation,
            PublishTokens: async saved => {
                await TokenStore.SaveAsync(target.Profile, tokens, CancellationToken.None);
                saved();

                return username;
            },
            WriteStamp: target.WriteStamp);

        var result = await CommitBoundary.CommitAsync(request, beforeCommit, progress, ct);

        if (result is AuthResult.Committed) progress.Notice($"Logged in as {username}");

        return result;
    }

    async Task<AuthResult> DiscoverCoreAsync(HttpClient http, string provider, bool forceDevice, CancellationToken ct) {
        var proxy       = new AuthProxyClient(http);
        var proxyConfig = await proxy.GetConfigAsync(AuthProxyEndpoint.Url, ct);

        if (proxyConfig is null) {
            return Fail("Cannot reach the Kurrent auth service.", ct, AuthFailureReason.Unreachable);
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
            _                                     => Stop("No Capacitor tenants are linked to your account.", ct,
                                                          AuthFailureReason.NoTenantsFound)
        };
    }

    async Task<AuthResult> DiscoverGitHubAsync(
            HttpClient http, IAuthProxyClient proxy, ProxyConfigResponse proxyConfig, bool forceDevice, CancellationToken ct) {
        if (string.IsNullOrEmpty(proxyConfig.GitHubClientId)) {
            return Fail("Cannot reach the Kurrent auth service.", ct, AuthFailureReason.Unreachable);
        }

        var accessToken = await OAuthLoginFlow.AcquireGitHubTokenAsync(
            http, proxyConfig.GitHubClientId, proxyConfig.GitHubCodeExchangeUrl, forceDevice, ct, progress);

        if (accessToken is null) return Stop("GitHub sign-in did not complete.", ct, AuthFailureReason.SigninDenied);

        var outcome = await new TenantDiscovery(proxy, picker).RunAsync(AuthProxyEndpoint.Url, accessToken, ct);

        if (outcome.ErrorMessage is not null) {
            return Fail(outcome.ErrorMessage, ct,
                outcome.NoTenantsFound ? AuthFailureReason.NoTenantsFound : AuthFailureReason.Other);
        }

        var identities = new List<AuthIdentity>();

        foreach (var tenant in outcome.Tenants) {
            if (!ServerIdentity.TryCanonicalizeForStamping(tenant.Origin, out var canonical, out var identityError)) {
                return Fail($"Error: {identityError}", identityError, ct);
            }

            identities.Add(new(tenant.ProfileName, canonical));
        }

        var picked = outcome.Picked!;

        var request = new CommitRequest(
            identities, AuthProvider.GitHubApp, picked.ProfileName,
            identities.First(i => i.Profile == picked.ProfileName).CanonicalServer,
            ConfigMutation: config => TenantDiscovery.MergeProfiles(config, outcome.Tenants, picked),
            PublishTokens: saved => ExchangeEveryTenantAsync(http, outcome.Tenants, picked, accessToken, saved));

        return await CommitBoundary.CommitAsync(request, beforeCommit, progress, ct);
    }

    // Inside the boundary: each tenant's exchange is network-then-save, and ANY failure — mapped or
    // thrown — costs that tenant its token (today's per-tenant warning) rather than the whole commit.
    async Task<string?> ExchangeEveryTenantAsync(
            HttpClient http, DiscoveredTenant[] tenants, DiscoveredTenant picked, string githubAccessToken, Action saved) {
        string? pickedUsername = null;

        foreach (var tenant in tenants) {
            try {
                var exchanged = await OAuthLoginFlow.ExchangeAsync(
                    http, AppConfig.NormalizeUrl(tenant.Origin), githubAccessToken, AuthProvider.GitHubApp,
                    tenant.ProfileName, progress, CancellationToken.None);

                if (exchanged is null) {
                    WarnExchangeFailed(tenant.ProfileName);

                    continue;
                }

                await TokenStore.SaveAsync(tenant.ProfileName, exchanged.Value.Tokens, CancellationToken.None);
                saved();

                if (tenant.ProfileName == picked.ProfileName) pickedUsername = exchanged.Value.Username;
            } catch (Exception) {
                WarnExchangeFailed(tenant.ProfileName);
            }
        }

        return pickedUsername;
    }

    void WarnExchangeFailed(string profile) =>
        progress.Error($"Warning: token exchange failed for {profile}. Run 'kcap login' after switching to that profile.");

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
    static AuthResult Stop(string message, CancellationToken ct, AuthFailureReason reason = AuthFailureReason.Other) =>
        ct.IsCancellationRequested ? new AuthResult.Cancelled() : new AuthResult.Failed(message, reason);

    // Same rule for a façade-rendered failure, with the line suppressed under a live cancel: a user
    // who cancelled must not be shown a transport error the cancel itself caused.
    AuthResult Fail(string message, CancellationToken ct, AuthFailureReason reason = AuthFailureReason.Other) =>
        Fail(message, message, ct, reason);

    AuthResult Fail(string rendered, string message, CancellationToken ct, AuthFailureReason reason = AuthFailureReason.Other) {
        if (ct.IsCancellationRequested) return new AuthResult.Cancelled();

        progress.Error(rendered);

        return new AuthResult.Failed(message, reason);
    }
}
