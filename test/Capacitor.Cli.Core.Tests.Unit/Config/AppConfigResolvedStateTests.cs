using Capacitor.Cli.Core.Config;

namespace Capacitor.Cli.Core.Tests.Unit.Config;

// AppConfig.ResolvedServerUrl / ResolvedProfile are process-global static
// state (set by ResolveServerUrl, read by GetActiveProfileAsync and friends).
// SetResolvedState mutates that same state directly, so every test here must
// run serialized against the others to avoid cross-test interference — mirrors
// the KCAP_URL/KCAP_PROFILE env-var isolation pattern used for
// ArgParsingTests' SessionEnvVarMutation group.
public class AppConfigResolvedStateTests {
    const string ResolvedStateMutation = nameof(ResolvedStateMutation);

    static Profile TestProfile(string serverUrl) => new() { ServerUrl = serverUrl };

    [Test]
    [NotInParallel(nameof(ResolvedStateMutation))]
    public async Task SetResolvedState_AssignsExactServerUrlAndProfile() {
        var profile = TestProfile("http://example.test");

        AppConfig.SetResolvedState("http://example.test", "default", profile);

        await Assert.That(AppConfig.ResolvedServerUrl).IsEqualTo("http://example.test");
        await Assert.That(await AppConfig.GetActiveProfileAsync()).IsEqualTo(profile);
        await Assert.That(AppConfig.ResolvedProfile?.ProfileName).IsEqualTo("default");
    }

    [Test]
    [NotInParallel(nameof(ResolvedStateMutation))]
    public async Task SetResolvedState_WinsOverConflictingEnvironmentPrecedence() {
        // If SetResolvedState re-ran ResolveServerUrl's precedence chain (CLI
        // flag > KCAP_URL > KCAP_PROFILE > repo > active profile), a KCAP_URL
        // set to a different value than what setup just saved would win and
        // silently override the just-saved server URL. SetResolvedState must
        // assign the exact value directly, never re-resolving.
        var savedEnvUrl = Environment.GetEnvironmentVariable("KCAP_URL");
        Environment.SetEnvironmentVariable("KCAP_URL", "http://conflicting-env.test");

        try {
            var profile = TestProfile("http://example.test");

            AppConfig.SetResolvedState("http://example.test", "default", profile);

            await Assert.That(AppConfig.ResolvedServerUrl).IsEqualTo("http://example.test");
            await Assert.That((await AppConfig.GetActiveProfileAsync())?.ServerUrl).IsEqualTo("http://example.test");
        } finally {
            Environment.SetEnvironmentVariable("KCAP_URL", savedEnvUrl);
        }
    }
}
