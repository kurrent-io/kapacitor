using Capacitor.Cli.Core.Auth;

namespace Capacitor.Cli.Core.Tests.Unit.Auth;

/// <summary>
/// No-op behaviour of <see cref="TokenStore.RefreshIfExpiringAsync"/> — the paths that
/// must NOT touch the network. These are the daemon acceptance criteria: proactive refresh
/// is a no-op when no tokens are stored, for the None provider, and while the token is still
/// comfortably valid (refresh only inside the expiry window). The provider paths that DO hit
/// the WorkOS / server refresh endpoints are exercised by DecideProactiveRefresh's unit tests
/// and integration coverage, never here, so this suite stays offline and fast.
///
/// Shares the KCAP_CONFIG_DIR token store with the other path-based tests, so it cleans up
/// and runs non-parallel exactly like <see cref="TokenStoreProfileTests"/>.
/// </summary>
[NotInParallel(nameof(TokenStoreProfileTests))]
public class RefreshIfExpiringTests {
    static readonly TimeSpan Window = TimeSpan.FromMinutes(5);

    static string TokensDir  => PathHelpers.ConfigPath("tokens");
    static string LegacyPath => PathHelpers.ConfigPath("tokens.json");

    [Before(Test)]
    public void Cleanup() {
        if (File.Exists(LegacyPath)) File.Delete(LegacyPath);
        if (Directory.Exists(TokensDir)) Directory.Delete(TokensDir, recursive: true);

        var cfg = Capacitor.Cli.Core.Config.AppConfig.GetConfigPath();
        if (File.Exists(cfg)) File.Delete(cfg);
    }

    [Test]
    public async Task NotDue_when_no_tokens_stored() {
        await Assert.That(await TokenStore.RefreshIfExpiringAsync(Window)).IsEqualTo(ProactiveRefreshOutcome.NotDue);
    }

    [Test]
    public async Task NotDue_and_leaves_token_untouched_when_comfortably_valid() {
        var original = new StoredTokens {
            AccessToken    = "at",
            RefreshToken   = "rt",
            ClientId       = "cid",
            ExpiresAt      = DateTimeOffset.UtcNow.AddHours(1), // well outside the 5-minute window
            GitHubUsername = "alice",
            Provider       = AuthProvider.WorkOS
        };
        await TokenStore.SaveAsync("default", original);

        await Assert.That(await TokenStore.RefreshIfExpiringAsync(Window)).IsEqualTo(ProactiveRefreshOutcome.NotDue);

        // Untouched — no refresh attempted, so the persisted access token is unchanged.
        var after = await TokenStore.LoadAsync("default");
        await Assert.That(after!.AccessToken).IsEqualTo("at");
    }

    [Test]
    public async Task NotDue_for_none_provider_even_inside_window() {
        // A None-auth server stores no tokens; a stray Provider=None file must still be a no-op.
        await TokenStore.SaveAsync("default", new StoredTokens {
            AccessToken    = "at",
            ExpiresAt      = DateTimeOffset.UtcNow.AddMinutes(1), // inside the window
            GitHubUsername = "alice",
            Provider       = AuthProvider.None
        });

        await Assert.That(await TokenStore.RefreshIfExpiringAsync(Window)).IsEqualTo(ProactiveRefreshOutcome.NotDue);
    }
}
