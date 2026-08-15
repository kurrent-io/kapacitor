using System.Runtime.CompilerServices;
using System.Text.Json;
using Capacitor.App.Services.Onboarding;
using Capacitor.Cli.Core;
using Capacitor.Cli.Core.Auth;
using Capacitor.Cli.Core.Config;

namespace Capacitor.App.Tests.Unit;

/// <summary>
/// Assembly-wide <c>KCAP_CONFIG_DIR</c> isolation for <see cref="OnboardingGateTests"/> — the
/// first tests in this assembly to exercise <c>AppConfig</c>/<c>TokenStore</c>'s real,
/// <c>PathHelpers</c>-based config.json / tokens/ paths. <c>PathHelpers.ConfigDir</c> is
/// <c>static readonly</c>, captured once per process, so this MUST run via a
/// <see cref="ModuleInitializerAttribute"/> (the CLR guarantees it runs before any type in the
/// module is touched) rather than a TUnit <c>[Before(Assembly)]</c> hook, which is not
/// guaranteed to beat every other static touch. Mirrors
/// <c>Capacitor.Cli.Tests.Unit.RepoPathStoreGlobalSetup</c> for this assembly.
/// </summary>
public static class OnboardingGateGlobalSetup {
    internal static readonly string SharedConfigDir = Path.Combine(
        Path.GetTempPath(),
        "kcap-app-onboarding-tests-" + Guid.NewGuid().ToString("N")[..8]
    );

    [ModuleInitializer]
    internal static void SetConfigDir() {
        Directory.CreateDirectory(SharedConfigDir);
        Environment.SetEnvironmentVariable("KCAP_CONFIG_DIR", SharedConfigDir);
    }

    [After(Assembly)]
    public static void CleanupConfigDir() {
        Environment.SetEnvironmentVariable("KCAP_CONFIG_DIR", null);
        try { Directory.Delete(SharedConfigDir, recursive: true); } catch { /* best effort */ }
    }
}

/// <summary>
/// The decision-1 gate matrix (design doc §2 decision 1 / §4's <c>OnboardingGate</c> bullet),
/// pinned against <see cref="TokenStore"/>'s REAL refresh/binding rules rather than a
/// reimplementation of them — each test below cites the TokenStore rule it mirrors.
///
/// [NotInParallel]: every test shares the one real config.json/tokens/ dir under the shared
/// KCAP_CONFIG_DIR (see <see cref="OnboardingGateGlobalSetup"/>), same convention as
/// TokenStoreProfileTests/ConfigMutatorTests in the CLI test assembly.
/// </summary>
[NotInParallel(nameof(OnboardingGateTests))]
public class OnboardingGateTests {
    const string ProfileName = "acme";
    const string ServerUrl = "https://acme.example";

    static string ConfigPath => AppConfig.GetConfigPath();
    static string TokensDir  => PathHelpers.ConfigPath("tokens");

    [Before(Test)]
    public void Cleanup() {
        if (File.Exists(ConfigPath)) File.Delete(ConfigPath);
        if (Directory.Exists(TokensDir)) Directory.Delete(TokensDir, recursive: true);
        AppConfig.ResetResolvedStateForTesting();

        // The unauthenticated-path test-isolation rule (CLI test memory): a stray KCAP_URL/
        // KCAP_PROFILE from the developer's shell must not redirect ResolveActiveProfile.
        Environment.SetEnvironmentVariable("KCAP_URL", null);
        Environment.SetEnvironmentVariable("KCAP_PROFILE", null);
    }

    // ── ValidServerUrl (the shared validator) ───────────────────────────────

    [Test]
    [Arguments("https://acme.example", true)]
    [Arguments("http://acme.example", true)]
    [Arguments("https://acme.example:8443/base", true)]
    [Arguments("file:///tmp/x", false)]
    [Arguments("not-a-url", false)]
    [Arguments("", false)]
    [Arguments(null, false)]
    public async Task ValidServerUrl_accepts_only_absolute_http_or_https(string? url, bool expected) {
        await Assert.That(OnboardingGate.ValidServerUrl(url)).IsEqualTo(expected);
    }

    // ── Gate matrix ──────────────────────────────────────────────────────────

    [Test]
    public async Task No_resolvable_profile_yields_NoProfile() {
        // active_profile names a profile that does not exist in `profiles` — ProfileResolver's
        // ResolveByName returns Profile: null, ProfileName: null (ProfileResolver.cs:85-96).
        WriteConfig(new ProfileConfig { ActiveProfile = "ghost", Profiles = new() });

        var result = await OnboardingGate.EvaluateAsync(CancellationToken.None);

        await AssertIncomplete(result, GateReason.NoProfile);
    }

    [Test]
    public async Task Invalid_server_url_rejects_file_scheme_and_App_ValidProfileName_agrees() {
        const string fileUrl = "file:///tmp/x";
        var profile = new Profile { ServerUrl = fileUrl };
        WriteConfig(SingleProfileConfig(profile));

        var result = await OnboardingGate.EvaluateAsync(CancellationToken.None);

        await AssertIncomplete(result, GateReason.InvalidServerUrl);

        // Decision 2: the gate and App.ValidProfileName must share one validator — before this
        // task, App.ValidProfileName accepted any absolute URI (including file://).
        await Assert.That(OnboardingGate.ValidServerUrl(fileUrl)).IsFalse();
        var resolved = new ResolvedProfile(fileUrl, ProfileName, profile, null);
        await Assert.That(App.ValidProfileName(resolved)).IsNull();
    }

    [Test]
    public async Task No_token_file_yields_NoToken() {
        WriteConfig(SingleProfileConfig(new Profile { ServerUrl = ServerUrl }));

        var result = await OnboardingGate.EvaluateAsync(CancellationToken.None);

        await AssertIncomplete(result, GateReason.NoToken);
    }

    [Test]
    public async Task WorkOS_expired_with_refresh_token_and_client_id_is_Complete() {
        // TokenStore.GetValidTokensForProfileAsync (TokenStore.cs:398): WorkOS refreshes only
        // when BOTH RefreshToken and ClientId are present.
        WriteConfig(SingleProfileConfig(new Profile { ServerUrl = ServerUrl }));
        await TokenStore.SaveAsync(ProfileName, MakeToken(
            AuthProvider.WorkOS, expired: true, serverUrl: ServerUrl, refreshToken: "rt", clientId: "cid"));

        var result = await OnboardingGate.EvaluateAsync(CancellationToken.None);

        await Assert.That(result).IsTypeOf<GateResult.Complete>();
    }

    [Test]
    public async Task WorkOS_expired_missing_client_id_is_TokenUnusableExpired() {
        WriteConfig(SingleProfileConfig(new Profile { ServerUrl = ServerUrl }));
        await TokenStore.SaveAsync(ProfileName, MakeToken(
            AuthProvider.WorkOS, expired: true, serverUrl: ServerUrl, refreshToken: "rt", clientId: null));

        var result = await OnboardingGate.EvaluateAsync(CancellationToken.None);

        await AssertIncomplete(result, GateReason.TokenUnusableExpired);
    }

    [Test]
    public async Task GitHubApp_expired_is_always_refresh_capable_and_Complete() {
        // TokenStore.cs:403-405 / DecideProactiveRefresh: GitHubApp always refreshes via the
        // server's /auth/refresh, independent of RefreshToken (normally null for this provider).
        WriteConfig(SingleProfileConfig(new Profile { ServerUrl = ServerUrl }));
        await TokenStore.SaveAsync(ProfileName, MakeToken(AuthProvider.GitHubApp, expired: true, serverUrl: ServerUrl));

        var result = await OnboardingGate.EvaluateAsync(CancellationToken.None);

        await Assert.That(result).IsTypeOf<GateResult.Complete>();
    }

    [Test]
    public async Task Wrong_server_token_is_TokenUnusableBinding_even_when_unexpired() {
        // TokenStore.BoundToTarget (TokenStore.cs:339-340) is checked BEFORE expiry — an
        // unexpired-but-wrong-server token must still be refused, never silently accepted.
        WriteConfig(SingleProfileConfig(new Profile { ServerUrl = ServerUrl }));
        await TokenStore.SaveAsync(ProfileName, MakeToken(
            AuthProvider.GitHubApp, expired: false, serverUrl: "https://other.example"));

        var result = await OnboardingGate.EvaluateAsync(CancellationToken.None);

        await AssertIncomplete(result, GateReason.TokenUnusableBinding);
    }

    [Test]
    public async Task Legacy_unbound_token_null_server_url_is_treated_as_usable() {
        // Pinned per TokenStore's REAL treatment: BoundToTarget (TokenStore.cs:339-340) —
        // "tokens.ServerUrl is null || SameServer(...)" — a pre-upgrade token with no stamp has
        // nothing to contradict and is let through to ANY server. The gate must agree, not
        // invent a stricter rule that would strand every pre-upgrade machine behind the wizard.
        WriteConfig(SingleProfileConfig(new Profile { ServerUrl = ServerUrl }));
        await TokenStore.SaveAsync(ProfileName, MakeToken(AuthProvider.GitHubApp, expired: false, serverUrl: null));

        var result = await OnboardingGate.EvaluateAsync(CancellationToken.None);

        await Assert.That(result).IsTypeOf<GateResult.Complete>();
    }

    [Test]
    public async Task Corrupt_token_file_yields_NoToken_not_a_crash() {
        // TokenStore.ReadTokenFileAsync (TokenStore.cs:94-108): a JsonException degrades to
        // Unusable → null, never throws — the wizard is the recovery path, not a crash.
        WriteConfig(SingleProfileConfig(new Profile { ServerUrl = ServerUrl }));
        Directory.CreateDirectory(TokensDir);
        var valid = JsonSerializer.Serialize(MakeToken(AuthProvider.GitHubApp, expired: false, serverUrl: ServerUrl),
            CapacitorJsonContext.Default.StoredTokens);
        await File.WriteAllTextAsync(Path.Combine(TokensDir, $"{ProfileName}.json"), valid + ",\"x\":1}");

        var result = await OnboardingGate.EvaluateAsync(CancellationToken.None);

        await AssertIncomplete(result, GateReason.NoToken);
    }

    [Test]
    public async Task None_stamp_matching_current_server_is_Complete_without_any_token_file() {
        var profile = new Profile { ServerUrl = ServerUrl, AuthProvider = new AuthProviderStamp("none", ServerUrl) };
        WriteConfig(SingleProfileConfig(profile));
        // Deliberately no tokens/ directory at all — the stamp must short-circuit the token read.

        var result = await OnboardingGate.EvaluateAsync(CancellationToken.None);

        await Assert.That(result).IsTypeOf<GateResult.Complete>();
        await Assert.That(Directory.Exists(TokensDir)).IsFalse();
    }

    [Test]
    public async Task Stale_none_stamp_after_server_url_change_requires_a_token() {
        // The stamp names a DIFFERENT server than the profile's current one (a server_url edit
        // since the stamp was written) — SameServer fails, so the stamp is ignored, not honored.
        var profile = new Profile {
            ServerUrl    = ServerUrl,
            AuthProvider = new AuthProviderStamp("none", "https://old.example")
        };
        WriteConfig(SingleProfileConfig(profile));

        var result = await OnboardingGate.EvaluateAsync(CancellationToken.None);

        await AssertIncomplete(result, GateReason.NoToken);
    }

    [Test]
    public async Task Legacy_profile_without_a_stamp_still_requires_and_accepts_a_valid_token() {
        // No auth_provider stamp at all (the common case for every profile before this task):
        // the gate must fall all the way through to the real token evaluation — proven here by
        // giving it a genuinely valid token and expecting Complete via THAT path, not some
        // stamp-shaped shortcut.
        WriteConfig(SingleProfileConfig(new Profile { ServerUrl = ServerUrl, AuthProvider = null }));
        await TokenStore.SaveAsync(ProfileName, MakeToken(AuthProvider.GitHubApp, expired: false, serverUrl: ServerUrl));

        var result = await OnboardingGate.EvaluateAsync(CancellationToken.None);

        await Assert.That(result).IsTypeOf<GateResult.Complete>();
    }

    static async Task AssertIncomplete(GateResult result, GateReason expected) {
        await Assert.That(result).IsTypeOf<GateResult.Incomplete>();
        await Assert.That(((GateResult.Incomplete)result).Reason).IsEqualTo(expected);
    }

    static ProfileConfig SingleProfileConfig(Profile profile) =>
        new() { ActiveProfile = ProfileName, Profiles = new() { [ProfileName] = profile } };

    static void WriteConfig(ProfileConfig config) =>
        File.WriteAllText(ConfigPath, JsonSerializer.Serialize(config, ProfileConfigJsonContext.Default.ProfileConfig));

    static StoredTokens MakeToken(
            string provider, bool expired, string? serverUrl, string? refreshToken = null, string? clientId = null) =>
        new() {
            AccessToken    = "t",
            ExpiresAt      = expired ? DateTimeOffset.UtcNow.AddHours(-1) : DateTimeOffset.UtcNow.AddHours(1),
            GitHubUsername = "u",
            Provider       = provider,
            ServerUrl      = serverUrl,
            RefreshToken   = refreshToken,
            ClientId       = clientId
        };
}
