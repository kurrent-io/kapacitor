using Capacitor.Cli.Commands;
using Capacitor.Cli.Core;
using Capacitor.Cli.Core.Auth;
using Capacitor.Cli.Core.Config;
using Capacitor.Cli.Core.Telemetry;
using Capacitor.Cli.Tests.Unit.Telemetry;
using Spectre.Console;
using TUnit.Assertions.Enums;
using Profile = Capacitor.Cli.Core.Config.Profile;

namespace Capacitor.Cli.Tests.Unit;

/// <summary>
/// `SetupCommand`'s Step 1 (<see cref="SetupCommand.RunDiscoveryAsync"/>) / Step 2
/// (<see cref="SetupCommand.RunLoginStepAsync"/>) re-plumb onto <see cref="OnboardingFacade"/>:
/// GitHub multi-tenant token publication, the funnel mapping the adapter now owns (WorkOS's own
/// funnel events fire from inside Core regardless of caller — nothing to pin here), and Step 2's
/// three branches. Shares the OnboardingFacadeTests keys — it drives the same shared config dir
/// and telemetry sink. Every discovery test forces `--github`: provider selection with no flag
/// depends on <c>HeadlessEnvironment.IsHeadless()</c>, which reads live env/platform state and
/// differs between the ubuntu-latest and windows-latest CI legs — not something a unit test can
/// pin (mirrors why LoginFacadeParityTests always passes --github too).
/// </summary>
[NotInParallel([
    nameof(TokenStoreProfileTests),
    nameof(TelemetryState) + "." + nameof(TelemetryState.PathOverride),
    nameof(TelemetryDeviceId) + "." + nameof(TelemetryDeviceId.PathOverride),
])]
public class SetupFacadeParityTests {
    static string TokensDir  => PathHelpers.ConfigPath("tokens");
    static string LegacyPath => PathHelpers.ConfigPath("tokens.json");
    static string ConfigPath => AppConfig.GetConfigPath();

    [Before(Test)]
    public void Cleanup() {
        SharedConfigDirCleanup.ClearTokenAndProfileState(LegacyPath, TokensDir);
        CliTelemetry.Reset();
        SetupCommand.FacadeOverride = null;
    }

    [After(Test)]
    public void ResetFacadeOverride() => SetupCommand.FacadeOverride = null;

    static ProfileConfig ReadConfig() => ConfigMutator.LoadPure(ConfigPath);

    static bool TokenFileExists(string profile) => File.Exists(Path.Combine(TokensDir, $"{profile}.json"));

    static List<TelemetryEvent> StartCapturingFunnel() {
        var dir = Path.Combine(Path.GetTempPath(), $"kcap-setup-facade-funnel-{Guid.NewGuid():N}");
        TelemetryState.PathOverride    = Path.Combine(dir, "telemetry.json");
        TelemetryDeviceId.PathOverride = Path.Combine(dir, "telemetry-device.json");
        var sink = new List<TelemetryEvent>();
        CliTelemetry.TestSink = sink;
        CliTelemetry.Initialize("setup", null, loggedIn: false);

        TelemetryTestGuards.AssertEnabled("setup");

        sink.Clear(); // drop cli_first_run

        return sink;
    }

    // ── Step 1: RunDiscoveryAsync (GitHub) ──────────────────────────────────

    [Test]
    public async Task RunDiscoveryAsync_github_two_tenants_publishes_both_and_marks_loginComplete() {
        using var handler = AuthHttp.Script(
            proxyConfig: """{"github_client_id":"cid"}""",
            tenants: OnboardingFacadeTests.TwoGitHubTenants);

        SetupCommand.FacadeOverride = _ =>
            OnboardingFacadeTests.NewFacade(new RecordingAuthProgress(), handler, OnboardingFacadeTests.PickerReturningFirst());

        var discovered = await SetupCommand.RunDiscoveryAsync(["--github"], forceDevice: true);

        await Assert.That(discovered).IsNotNull();
        await Assert.That(discovered!.Value.LoginComplete).IsTrue();
        await Assert.That(discovered.Value.Provider).IsEqualTo(AuthProvider.GitHubApp);
        await Assert.That(discovered.Value.ServerUrl).IsEqualTo("https://acme.kcap.ai");

        await Assert.That(TokenFileExists("acme")).IsTrue();
        await Assert.That(TokenFileExists("contoso")).IsTrue();

        var cfg = ReadConfig();
        await Assert.That(cfg.ActiveProfile).IsEqualTo("acme");
        await Assert.That(cfg.Profiles["contoso"].ServerUrl).IsEqualTo("https://contoso.kcap.ai");
    }

    [Test]
    public async Task RunDiscoveryAsync_github_committed_fires_signin_opened_then_signin_completed() {
        var sink = StartCapturingFunnel();

        using var handler = AuthHttp.Script(
            proxyConfig: """{"github_client_id":"cid"}""",
            tenants: OnboardingFacadeTests.TwoGitHubTenants);

        SetupCommand.FacadeOverride = _ =>
            OnboardingFacadeTests.NewFacade(new RecordingAuthProgress(), handler, OnboardingFacadeTests.PickerReturningFirst());

        var discovered = await SetupCommand.RunDiscoveryAsync(["--github"], forceDevice: true);

        await Assert.That(discovered).IsNotNull();
        await Assert.That(sink.Select(e => e.Name).ToArray()).IsEquivalentTo(
            new[] { "cli_setup_signin_opened", "cli_setup_signin_completed" }, CollectionOrdering.Matching);
    }

    [Test]
    public async Task RunDiscoveryAsync_github_zero_tenants_emits_signin_completed_and_tenant_none() {
        var sink = StartCapturingFunnel();

        using var handler = AuthHttp.Script(proxyConfig: """{"github_client_id":"cid"}""", tenants: "[]");

        SetupCommand.FacadeOverride = _ => OnboardingFacadeTests.NewFacade(new RecordingAuthProgress(), handler);

        var discovered = await SetupCommand.RunDiscoveryAsync(["--github"], forceDevice: true);

        await Assert.That(discovered).IsNull();
        // Today's setup fires SigninCompleted unconditionally once the token is acquired, and
        // TenantNone additionally when discovery then finds nothing — the two co-occur here.
        await Assert.That(sink.Select(e => e.Name).ToArray()).IsEquivalentTo(
            new[] { "cli_setup_signin_opened", "cli_setup_signin_completed", "cli_setup_tenant_none" },
            CollectionOrdering.Matching);
    }

    [Test]
    public async Task RunDiscoveryAsync_github_post_acquisition_discovery_error_still_emits_signin_completed() {
        var sink = StartCapturingFunnel();

        // No `tenants:` stub — /discover-tenants 500s AFTER the device flow already handed out a
        // token, landing AuthFailureReason.Other (not NoTenantsFound).
        using var handler = AuthHttp.Script(proxyConfig: """{"github_client_id":"cid"}""");

        SetupCommand.FacadeOverride = _ => OnboardingFacadeTests.NewFacade(new RecordingAuthProgress(), handler);

        var discovered = await SetupCommand.RunDiscoveryAsync(["--github"], forceDevice: true);

        await Assert.That(discovered).IsNull();
        await Assert.That(sink.Select(e => e.Name).ToArray()).IsEquivalentTo(
            new[] { "cli_setup_signin_opened", "cli_setup_signin_completed" }, CollectionOrdering.Matching);
    }

    [Test]
    public async Task RunDiscoveryAsync_github_signin_denied_emits_signin_failed() {
        var sink = StartCapturingFunnel();

        using var handler = AuthHttp.Script(
            proxyConfig: """{"github_client_id":"cid"}""",
            devicePoll: () => AuthHttp.Json("""{"error":"access_denied"}"""));

        SetupCommand.FacadeOverride = _ => OnboardingFacadeTests.NewFacade(new RecordingAuthProgress(), handler);

        var discovered = await SetupCommand.RunDiscoveryAsync(["--github"], forceDevice: true);

        await Assert.That(discovered).IsNull();
        await Assert.That(sink.Select(e => e.Name).ToArray()).IsEquivalentTo(
            new[] { "cli_setup_signin_opened", "cli_setup_signin_failed" }, CollectionOrdering.Matching);
    }

    [Test]
    public async Task RunDiscoveryAsync_github_unreachable_proxy_fires_no_extra_funnel_event() {
        var sink = StartCapturingFunnel();

        using var handler = AuthHttp.Script(); // no /config route — proxy unreachable

        SetupCommand.FacadeOverride = _ => OnboardingFacadeTests.NewFacade(new RecordingAuthProgress(), handler);

        var discovered = await SetupCommand.RunDiscoveryAsync(["--github"], forceDevice: true);

        await Assert.That(discovered).IsNull();
        // Other/Unreachable failures map to nothing beyond SigninOpened — only SigninDenied and
        // NoTenantsFound get a second event.
        await Assert.That(sink.Select(e => e.Name).ToArray()).IsEquivalentTo(
            new[] { "cli_setup_signin_opened" }, CollectionOrdering.Matching);
    }

    // ── Step 2: RunLoginStepAsync ────────────────────────────────────────────

    // AnsiConsole.Console is process-global state (see below) — fully serialize this test against
    // everything else, mirroring AuthProgressTests' console-redirection convention.
    [Test]
    [NotInParallel]
    public async Task RunLoginStepAsync_loginComplete_reports_the_already_published_identity_without_a_facade_call() {
        await ConfigMutator.MutateAsync(c => c with {
            Profiles      = new Dictionary<string, Profile> { ["acme"] = new() { ServerUrl = "https://acme.kcap.ai" } },
            ActiveProfile = "acme",
        });
        await TokenStore.SaveAsync("acme", new StoredTokens {
            AccessToken     = "tok",
            ExpiresAt       = DateTimeOffset.UtcNow.AddHours(1),
            GitHubUsername  = "alice",
            Provider        = AuthProvider.GitHubApp,
            ServerUrl       = "https://acme.kcap.ai",
        });

        SetupCommand.FacadeOverride = _ => throw new InvalidOperationException("loginComplete must not call the façade");

        // SetupCommand writes Step 2's banner via AnsiConsole (not IAuthProgress, and not plain
        // Console.Out — Spectre's static AnsiConsole.Console caches its writer at first use, so
        // Console.SetOut alone doesn't redirect it), so swap the singleton console to capture it.
        var originalConsole = AnsiConsole.Console;
        var buffer          = new StringWriter();
        AnsiConsole.Console = AnsiConsole.Create(new AnsiConsoleSettings {
            Ansi        = AnsiSupport.No,
            ColorSystem = ColorSystemSupport.NoColors,
            Out         = new AnsiConsoleOutput(buffer),
        });

        int exitCode;

        try {
            exitCode = await SetupCommand.RunLoginStepAsync(
                loginComplete: true, provider: AuthProvider.GitHubApp, serverUrl: "https://acme.kcap.ai",
                forceDevice: false, activeProfile: "acme");
        } finally {
            AnsiConsole.Console = originalConsole;
        }

        await Assert.That(exitCode).IsEqualTo(0);
        await Assert.That(buffer.ToString()).Contains("Logged in as alice");
    }

    [Test]
    public async Task RunLoginStepAsync_none_provider_skips_login_without_a_facade_call() {
        SetupCommand.FacadeOverride = _ => throw new InvalidOperationException("None provider must not call the façade");

        var exitCode = await SetupCommand.RunLoginStepAsync(
            loginComplete: false, provider: AuthProvider.None, serverUrl: "https://none.example",
            forceDevice: false, activeProfile: "default");

        await Assert.That(exitCode).IsEqualTo(0);
    }

    [Test]
    public async Task RunLoginStepAsync_explicit_login_commits_and_adopts_the_server_onto_the_active_profile() {
        using var handler = AuthHttp.Script(authConfig: """{"provider":"GitHubApp","github_client_id":"cid"}""");

        SetupCommand.FacadeOverride = _ => OnboardingFacadeTests.NewFacade(new RecordingAuthProgress(), handler);

        var exitCode = await SetupCommand.RunLoginStepAsync(
            loginComplete: false, provider: AuthProvider.GitHubApp, serverUrl: "https://acme.kcap.ai",
            forceDevice: true, activeProfile: "acme");

        await Assert.That(exitCode).IsEqualTo(0);
        await Assert.That(TokenFileExists("acme")).IsTrue();
        // adoptServer: true — setup's whole job is configuring the active profile for this server.
        await Assert.That(ReadConfig().Profiles["acme"].ServerUrl).IsEqualTo("https://acme.kcap.ai");
    }

    [Test]
    public async Task RunLoginStepAsync_explicit_login_failure_prints_login_failed_and_returns_one() {
        using var handler = AuthHttp.Script(authConfig: """{"provider":"martian"}""");

        SetupCommand.FacadeOverride = _ => OnboardingFacadeTests.NewFacade(new RecordingAuthProgress(), handler);

        // The provider param is Step 1's resolved value; Step 2 re-fetches /auth/config for the
        // actual login, so a server reporting an unrelated/unknown provider by then still fails.
        var exitCode = await SetupCommand.RunLoginStepAsync(
            loginComplete: false, provider: AuthProvider.GitHubApp, serverUrl: "https://acme.kcap.ai",
            forceDevice: false, activeProfile: "acme");

        await Assert.That(exitCode).IsEqualTo(1);
    }
}
