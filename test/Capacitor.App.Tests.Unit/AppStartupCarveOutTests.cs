using System.Text.Json;
using Capacitor.App.Services.Onboarding;
using Capacitor.Cli.Core;
using Capacitor.Cli.Core.Auth;
using Capacitor.Cli.Core.Config;
using AppUnderTest = Capacitor.App.App;

namespace Capacitor.App.Tests.Unit;

/// Task 15 decision-2 carve-out: App.StartAsync evaluates OnboardingGate.EvaluateAsync() FIRST and
/// derives whether the lifecycle graph's auto-actions stay open (Complete) or close permanently
/// (Incomplete). StartAsync itself needs a real daemon/profile — not a unit-test seam, same reason
/// AppStartupTests drives extracted statics instead (see that file's own header comment) — so this
/// exercises the two pure seams App exposes for the carve-out: AutoActionsPermanentlyClosed (the
/// gate→flag switch) and ResolveConsentFlipIdentity (the ConsentFlipCoordinator identity delegate,
/// MUST-WIRE 1). DaemonLifecycleControllerTests covers the controller-level ctor param behavior
/// (fake lane, no gate involved) — this file is the App-level wiring half only.
///
/// [NotInParallel]: shares OnboardingGateTests' one real config.json under the assembly-wide
/// KCAP_CONFIG_DIR (see OnboardingGateGlobalSetup) — same isolation rule, same shared resource.
[NotInParallel(nameof(OnboardingGateTests))]
public class AppStartupCarveOutTests {
    const string ProfileName = "acme";
    const string ServerUrl = "https://acme.example";

    static string ConfigPath => AppConfig.GetConfigPath();
    static string TokensDir  => PathHelpers.ConfigPath("tokens");

    [Before(Test)]
    public void Cleanup() {
        if (File.Exists(ConfigPath)) File.Delete(ConfigPath);
        if (Directory.Exists(TokensDir)) Directory.Delete(TokensDir, recursive: true);
        AppConfig.ResetResolvedStateForTesting();

        // Same unauthenticated-path test-isolation rule OnboardingGateTests follows: a stray
        // KCAP_URL/KCAP_PROFILE from the developer's shell must not redirect config resolution.
        Environment.SetEnvironmentVariable("KCAP_URL", null);
        Environment.SetEnvironmentVariable("KCAP_PROFILE", null);
    }

    // ---- AutoActionsPermanentlyClosed: the pure gate→flag switch ----

    [Test]
    public async Task Complete_gate_keeps_auto_actions_open() {
        await Assert.That(AppUnderTest.AutoActionsPermanentlyClosed(new GateResult.Complete())).IsFalse();
    }

    [Test]
    [Arguments(GateReason.NoProfile)]
    [Arguments(GateReason.InvalidServerUrl)]
    [Arguments(GateReason.NoToken)]
    [Arguments(GateReason.TokenUnusableBinding)]
    [Arguments(GateReason.TokenUnusableExpired)]
    public async Task Incomplete_gate_closes_auto_actions_for_every_reason(GateReason reason) {
        await Assert.That(AppUnderTest.AutoActionsPermanentlyClosed(new GateResult.Incomplete(reason))).IsTrue();
    }

    // ---- End-to-end against a REAL OnboardingGate.EvaluateAsync() — the brief's two §10 rows ----
    // that apply without a wizard: valid URL + no token, and an invalid/non-HTTP URL.

    [Test]
    public async Task ValidUrl_noToken_fixture_closes_auto_actions() {
        WriteConfig(SingleProfileConfig(new Profile { ServerUrl = ServerUrl }));

        var gate = await OnboardingGate.EvaluateAsync(CancellationToken.None);

        await Assert.That(gate).IsTypeOf<GateResult.Incomplete>();
        await Assert.That(((GateResult.Incomplete)gate).Reason).IsEqualTo(GateReason.NoToken);
        await Assert.That(AppUnderTest.AutoActionsPermanentlyClosed(gate)).IsTrue();
    }

    [Test]
    public async Task InvalidNonHttpUrl_fixture_closes_auto_actions() {
        WriteConfig(SingleProfileConfig(new Profile { ServerUrl = "file:///tmp/x" }));

        var gate = await OnboardingGate.EvaluateAsync(CancellationToken.None);

        await Assert.That(gate).IsTypeOf<GateResult.Incomplete>();
        await Assert.That(((GateResult.Incomplete)gate).Reason).IsEqualTo(GateReason.InvalidServerUrl);
        await Assert.That(AppUnderTest.AutoActionsPermanentlyClosed(gate)).IsTrue();
    }

    // Symmetric control: a genuinely Complete fixture must NOT close auto-actions.
    [Test]
    public async Task Complete_fixture_keeps_auto_actions_open() {
        var profile = new Profile { ServerUrl = ServerUrl, AuthProvider = new AuthProviderStamp("none", ServerUrl) };
        WriteConfig(SingleProfileConfig(profile));

        var gate = await OnboardingGate.EvaluateAsync(CancellationToken.None);

        await Assert.That(gate).IsTypeOf<GateResult.Complete>();
        await Assert.That(AppUnderTest.AutoActionsPermanentlyClosed(gate)).IsFalse();
    }

    // ---- ResolveConsentFlipIdentity: MUST-WIRE 1's ConsentFlipCoordinator identity delegate ----

    [Test]
    public async Task ResolveConsentFlipIdentity_resolves_active_profile_server_and_daemon_name() {
        var profile = new Profile { ServerUrl = ServerUrl, Daemon = new DaemonSettings { Name = "acme-daemon" } };
        WriteConfig(SingleProfileConfig(profile));

        var (resolvedProfile, server, daemonName) = AppUnderTest.ResolveConsentFlipIdentity();

        await Assert.That(resolvedProfile).IsEqualTo(ProfileName);
        await Assert.That(server).IsEqualTo(ServerIdentity.Canonicalize(ServerUrl));
        await Assert.That(daemonName).IsEqualTo("acme-daemon");
    }

    // Mirrors ConsentFlipCoordinatorTests' own unparseable-server fallback: Canonicalize(...) is null here.
    [Test]
    public async Task ResolveConsentFlipIdentity_falls_back_to_the_raw_server_when_unparseable() {
        WriteConfig(SingleProfileConfig(new Profile { ServerUrl = "not a url" }));

        var (_, server, _) = AppUnderTest.ResolveConsentFlipIdentity();

        await Assert.That(server).IsEqualTo("not a url");
    }

    // No config.json at all: ConfigMutator.LoadPure degrades to a fresh default rather than throwing.
    [Test]
    public async Task ResolveConsentFlipIdentity_no_config_file_yields_the_default_profile_with_no_server() {
        var (resolvedProfile, server, daemonName) = AppUnderTest.ResolveConsentFlipIdentity();

        await Assert.That(resolvedProfile).IsEqualTo("default");
        await Assert.That(server).IsEqualTo("");
        await Assert.That(daemonName).IsNotEmpty(); // DaemonNameResolver's OS-username/machine/"daemon" fallback chain
    }

    // ---- EvaluateGateSafelyAsync: round-1 review — a gate exception must not brick startup ----

    [Test]
    public async Task EvaluateGateSafelyAsync_passes_a_successful_result_through_unchanged() {
        var complete = new GateResult.Complete();

        var result = await AppUnderTest.EvaluateGateSafelyAsync(_ => Task.FromResult<GateResult>(complete), CancellationToken.None);

        await Assert.That(result).IsSameReferenceAs(complete);
    }

    [Test]
    public async Task EvaluateGateSafelyAsync_degrades_an_unexpected_exception_to_incomplete() {
        var result = await AppUnderTest.EvaluateGateSafelyAsync(
            _ => throw new InvalidOperationException("boom"), CancellationToken.None);

        await Assert.That(result).IsTypeOf<GateResult.Incomplete>();
        await Assert.That(((GateResult.Incomplete)result).Reason).IsEqualTo(GateReason.EvaluationFailed);
    }

    // A cancellation matching the caller's OWN token is shutdown, not a gate failure — it must
    // propagate rather than be swallowed into a fabricated Incomplete result.
    [Test]
    public async Task EvaluateGateSafelyAsync_rethrows_a_cancellation_matching_the_callers_token() {
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAsync<OperationCanceledException>(() =>
            AppUnderTest.EvaluateGateSafelyAsync(_ => throw new OperationCanceledException(), cts.Token));
    }

    // An OperationCanceledException NOT tied to the caller's token (ct never cancelled) is just
    // another unexpected exception — it degrades exactly like InvalidOperationException above.
    [Test]
    public async Task EvaluateGateSafelyAsync_degrades_an_unrelated_cancellation_to_incomplete() {
        var result = await AppUnderTest.EvaluateGateSafelyAsync(
            _ => throw new OperationCanceledException("unrelated"), CancellationToken.None);

        await Assert.That(result).IsTypeOf<GateResult.Incomplete>();
        await Assert.That(((GateResult.Incomplete)result).Reason).IsEqualTo(GateReason.EvaluationFailed);
    }

    static ProfileConfig SingleProfileConfig(Profile profile) =>
        new() { ActiveProfile = ProfileName, Profiles = new() { [ProfileName] = profile } };

    static void WriteConfig(ProfileConfig config) =>
        File.WriteAllText(ConfigPath, JsonSerializer.Serialize(config, ProfileConfigJsonContext.Default.ProfileConfig));
}
