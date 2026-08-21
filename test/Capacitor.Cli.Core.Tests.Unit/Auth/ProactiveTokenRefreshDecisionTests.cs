using Capacitor.Cli.Core.Auth;

namespace Capacitor.Cli.Core.Tests.Unit.Auth;

/// <summary>
/// Tests for <see cref="TokenStore.DecideProactiveRefresh"/> — the pure decision the
/// daemon's proactive-refresh tick makes each time it wakes. It answers "should we
/// refresh the active profile's token ahead of expiry, and via which provider path?"
/// without touching the network or the filesystem, so every branch is unit-testable
/// in isolation.
/// </summary>
public class ProactiveTokenRefreshDecisionTests {
    static readonly TimeSpan Window = TimeSpan.FromMinutes(5);
    static readonly DateTimeOffset Now = new(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);

    static StoredTokens Tokens(string provider, DateTimeOffset expiresAt, string? refreshToken = "rt", string? clientId = "cid") => new() {
        AccessToken    = "at",
        RefreshToken   = refreshToken,
        ClientId       = clientId,
        ExpiresAt      = expiresAt,
        GitHubUsername = "alice",
        Provider       = provider
    };

    [Test]
    public async Task No_stored_tokens_is_a_no_op() {
        var decision = TokenStore.DecideProactiveRefresh(null, Now, Window);

        await Assert.That(decision).IsEqualTo(TokenStore.RefreshDecision.NoTokens);
    }

    [Test]
    public async Task Token_comfortably_valid_is_not_due() {
        // Expires well beyond the window — nothing to do yet.
        var tokens   = Tokens(AuthProvider.WorkOS, Now.AddMinutes(30));
        var decision = TokenStore.DecideProactiveRefresh(tokens, Now, Window);

        await Assert.That(decision).IsEqualTo(TokenStore.RefreshDecision.NotDueYet);
    }

    [Test]
    public async Task WorkOS_token_inside_window_refreshes_via_workos() {
        var tokens   = Tokens(AuthProvider.WorkOS, Now.AddMinutes(4));
        var decision = TokenStore.DecideProactiveRefresh(tokens, Now, Window);

        await Assert.That(decision).IsEqualTo(TokenStore.RefreshDecision.RefreshWorkOS);
    }

    [Test]
    public async Task GitHub_token_inside_window_refreshes_via_github() {
        var tokens   = Tokens(AuthProvider.GitHubApp, Now.AddMinutes(4));
        var decision = TokenStore.DecideProactiveRefresh(tokens, Now, Window);

        await Assert.That(decision).IsEqualTo(TokenStore.RefreshDecision.RefreshGitHub);
    }

    [Test]
    public async Task Token_exactly_at_window_boundary_refreshes() {
        // now == ExpiresAt - window: inclusive, so we refresh rather than wait a tick.
        var tokens   = Tokens(AuthProvider.WorkOS, Now.Add(Window));
        var decision = TokenStore.DecideProactiveRefresh(tokens, Now, Window);

        await Assert.That(decision).IsEqualTo(TokenStore.RefreshDecision.RefreshWorkOS);
    }

    [Test]
    public async Task Already_expired_workos_token_still_refreshes() {
        var tokens   = Tokens(AuthProvider.WorkOS, Now.AddMinutes(-10));
        var decision = TokenStore.DecideProactiveRefresh(tokens, Now, Window);

        await Assert.That(decision).IsEqualTo(TokenStore.RefreshDecision.RefreshWorkOS);
    }

    [Test]
    public async Task WorkOS_token_without_refresh_token_is_unsupported() {
        // WorkOS refresh needs the rotating refresh_token; without it there's nothing
        // to present, so the tick must not attempt a (doomed) refresh.
        var tokens   = Tokens(AuthProvider.WorkOS, Now.AddMinutes(1), refreshToken: null);
        var decision = TokenStore.DecideProactiveRefresh(tokens, Now, Window);

        await Assert.That(decision).IsEqualTo(TokenStore.RefreshDecision.Unsupported);
    }

    [Test]
    public async Task WorkOS_token_without_client_id_is_unsupported() {
        var tokens   = Tokens(AuthProvider.WorkOS, Now.AddMinutes(1), clientId: null);
        var decision = TokenStore.DecideProactiveRefresh(tokens, Now, Window);

        await Assert.That(decision).IsEqualTo(TokenStore.RefreshDecision.Unsupported);
    }

    [Test]
    public async Task None_provider_token_is_unsupported() {
        // A None-auth server stores no tokens, but even if a stray file surfaced with
        // Provider=None the tick must be a no-op (acceptance criterion).
        var tokens   = Tokens(AuthProvider.None, Now.AddMinutes(1));
        var decision = TokenStore.DecideProactiveRefresh(tokens, Now, Window);

        await Assert.That(decision).IsEqualTo(TokenStore.RefreshDecision.Unsupported);
    }
}
