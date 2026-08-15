using Capacitor.Cli.Core.Auth;
using Capacitor.Cli.Core.Config;

namespace Capacitor.App.Services.Onboarding;

public abstract record GateResult {
    public sealed record Complete : GateResult;
    public sealed record Incomplete(GateReason Reason) : GateResult;
}

// EvaluationFailed: never returned by EvaluateAsync itself — App.EvaluateGateSafelyAsync's fail-safe
// degrade for an unexpected exception (round-1 review, adjudicated).
public enum GateReason { NoProfile, InvalidServerUrl, NoToken, TokenUnusableBinding, TokenUnusableExpired, EvaluationFailed }

/// <summary>
/// Decision-1 first-run trigger: local, side-effect-free except the shared resolution path's own
/// v1→v2 migration write on legacy configs (kept intentionally shared with the normal daemon
/// graph — decision 2 — rather than a purity-motivated divergent read that could resolve a
/// different profile than the graph builds), no refresh. Whether the wizard opens is the exact
/// inverse of "does TokenStore already consider this profile authenticated" — so every branch
/// here mirrors a specific TokenStore rule rather than inventing its own.
/// </summary>
public static class OnboardingGate {
    /// <summary>
    /// The ONE shared validator for "is this usable as a server identity" — also used by
    /// <c>App.ValidProfileName</c> so the gate and the lifecycle-controller precondition can
    /// never disagree on what counts as a valid <c>server_url</c> (e.g. both reject
    /// <c>file://</c>). Delegates to <see cref="ServerIdentity.Canonicalize"/>, which restricts
    /// to absolute http/https origins with no userinfo/query/fragment.
    /// </summary>
    public static bool ValidServerUrl(string? url) => ServerIdentity.Canonicalize(url) is not null;

    public static async Task<GateResult> EvaluateAsync(CancellationToken ct) {
        // Daemon-style resolution — no repo/git discovery, matching decision 1's "local" scope.
        await AppConfig.ResolveActiveProfile([]);
        var resolved = AppConfig.ResolvedProfile;

        if (resolved is not { Profile: { } profile, ProfileName: { Length: > 0 } profileName }) {
            return new GateResult.Incomplete(GateReason.NoProfile);
        }

        if (!ValidServerUrl(profile.ServerUrl)) {
            return new GateResult.Incomplete(GateReason.InvalidServerUrl);
        }

        var stamp = profile.AuthProvider;

        if (stamp is { Provider: "none" } && ServerIdentity.SameServer(stamp.ServerUrl, profile.ServerUrl)) {
            return new GateResult.Complete();
        }

        // Raw, refresh-free read — a stale/expiring token must not be rotated just to answer
        // "is the wizard needed", which would spend a rotating WorkOS refresh token for nothing.
        var tokens = await TokenStore.LoadForProfileAsync(profileName, ct);

        if (tokens is null) {
            return new GateResult.Incomplete(GateReason.NoToken);
        }

        if (!BoundToProfile(tokens, profile.ServerUrl)) {
            return new GateResult.Incomplete(GateReason.TokenUnusableBinding);
        }

        if (!tokens.IsExpired) {
            return new GateResult.Complete();
        }

        return RefreshCapable(tokens)
            ? new GateResult.Complete()
            : new GateResult.Incomplete(GateReason.TokenUnusableExpired);
    }

    // Mirrors TokenStore.BoundToTarget exactly: a legacy (pre-upgrade) token carries no
    // ServerUrl stamp, so there is nothing to contradict — treated as usable for any server.
    static bool BoundToProfile(StoredTokens tokens, string? serverUrl) =>
        tokens.ServerUrl is null || ServerIdentity.SameServer(tokens.ServerUrl, serverUrl);

    // Mirrors GetValidTokensForProfileAsync's refresh gating: GitHubApp always refreshes via the
    // server's /auth/refresh; WorkOS needs its own rotating RefreshToken plus ClientId.
    static bool RefreshCapable(StoredTokens tokens) =>
        tokens.Provider is AuthProvider.GitHubApp
     || tokens is { Provider: AuthProvider.WorkOS, RefreshToken: not null, ClientId: not null };
}
