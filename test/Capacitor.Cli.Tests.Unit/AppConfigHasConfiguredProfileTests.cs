using Capacitor.Cli.Core.Config;

namespace Capacitor.Cli.Tests.Unit;

// The setup funnel's `has_existing_profile` reported true on every run ever recorded, including
// on machines that had never run kcap. LoadProfileConfig synthesizes a `default` entry when
// config.json does not exist, so the `Profiles.Count > 0` test the funnel used could not be false
// by construction — the property carried no information at all.
//
// These pin the distinction the funnel actually needs: a profile someone configured, not one the
// loader invented to keep its return type non-empty.
public class AppConfigHasConfiguredProfileTests {
    [Test]
    public async Task Synthesized_default_profile_is_not_a_configured_profile() {
        // Byte-for-byte what LoadProfileConfig returns when config.json does not exist.
        var fresh = new ProfileConfig { Profiles = new() { ["default"] = new() } };

        await Assert.That(AppConfig.HasConfiguredProfile(fresh)).IsFalse();
    }

    [Test]
    public async Task Profile_with_a_server_url_is_a_configured_profile() {
        var configured = new ProfileConfig {
            Profiles = new() { ["default"] = new() { ServerUrl = "https://acme.kcap.ai" } },
        };

        await Assert.That(AppConfig.HasConfiguredProfile(configured)).IsTrue();
    }

    [Test]
    public async Task Blank_server_url_does_not_count_as_configured() {
        var blank = new ProfileConfig {
            Profiles = new() { ["default"] = new() { ServerUrl = "   " } },
        };

        await Assert.That(AppConfig.HasConfiguredProfile(blank)).IsFalse();
    }

    [Test]
    public async Task A_configured_profile_counts_even_when_it_is_not_the_active_one() {
        // "Has this person set kcap up before?" is a question about any profile, not the active
        // one — re-running setup after `kcap use` pointed elsewhere is still a re-run.
        var config = new ProfileConfig {
            ActiveProfile = "default",
            Profiles = new() {
                ["default"] = new(),
                ["work"]    = new() { ServerUrl = "https://acme.kcap.ai" },
            },
        };

        await Assert.That(AppConfig.HasConfiguredProfile(config)).IsTrue();
    }

    [Test]
    public async Task No_profiles_at_all_is_not_configured() {
        await Assert.That(AppConfig.HasConfiguredProfile(new ProfileConfig())).IsFalse();
    }
}
