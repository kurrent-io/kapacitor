using Capacitor.Cli.Core.Config;

namespace Capacitor.Cli.Core.Tests.Unit;

public class ProviderApiKeyPolicyTests {
    [Test]
    public async Task Default_scrubs_when_no_env_and_no_profile() {
        await Assert.That(ProviderApiKeyPolicy.ShouldKeepProviderKey(envValue: null, profile: null)).IsFalse();
    }

    [Test]
    public async Task Default_scrubs_when_profile_opts_out() {
        var profile = new Profile { UseProviderApiKey = false };
        await Assert.That(ProviderApiKeyPolicy.ShouldKeepProviderKey(null, profile)).IsFalse();
    }

    [Test]
    public async Task Profile_opt_in_keeps_key() {
        var profile = new Profile { UseProviderApiKey = true };
        await Assert.That(ProviderApiKeyPolicy.ShouldKeepProviderKey(null, profile)).IsTrue();
    }

    [Test]
    [Arguments("1")]
    [Arguments("true")]
    [Arguments("TRUE")]
    [Arguments("yes")]
    [Arguments("on")]
    public async Task Env_var_truthy_overrides_profile_off(string envValue) {
        var profile = new Profile { UseProviderApiKey = false };
        await Assert.That(ProviderApiKeyPolicy.ShouldKeepProviderKey(envValue, profile)).IsTrue();
    }

    [Test]
    [Arguments("0")]
    [Arguments("false")]
    [Arguments("no")]
    [Arguments("off")]
    public async Task Env_var_falsy_overrides_profile_on(string envValue) {
        var profile = new Profile { UseProviderApiKey = true };
        await Assert.That(ProviderApiKeyPolicy.ShouldKeepProviderKey(envValue, profile)).IsFalse();
    }

    [Test]
    [Arguments("")]
    [Arguments("   ")]
    [Arguments("maybe")]
    public async Task Env_var_unrecognised_falls_through_to_profile(string envValue) {
        var profile = new Profile { UseProviderApiKey = true };
        await Assert.That(ProviderApiKeyPolicy.ShouldKeepProviderKey(envValue, profile)).IsTrue();
    }

    [Test]
    [Arguments("1",     true)]
    [Arguments("true",  true)]
    [Arguments("YES",   true)]
    [Arguments("on",    true)]
    [Arguments("0",     false)]
    [Arguments("false", false)]
    [Arguments("no",    false)]
    [Arguments("off",   false)]
    public async Task TryParseBool_recognises_truthy_and_falsy(string value, bool expected) {
        await Assert.That(ProviderApiKeyPolicy.TryParseBool(value)).IsEqualTo(expected);
    }

    [Test]
    [Arguments(null)]
    [Arguments("")]
    [Arguments("   ")]
    [Arguments("maybe")]
    [Arguments("2")]
    public async Task TryParseBool_returns_null_on_unrecognised(string? value) {
        await Assert.That(ProviderApiKeyPolicy.TryParseBool(value)).IsNull();
    }
}
