using System.Text.Json;
using Capacitor.Cli.Core.Config;

namespace Capacitor.Cli.Tests.Unit;

/// <summary>
/// A hand-written, truncated, or older-version config.json omits keys that a
/// kcap-written one always contains. STJ source-gen builds these init-only
/// records through an object initializer that assigns <c>default(T)</c> for every
/// absent property, discarding the record's member initializers — so every such
/// key used to deserialize to null/false/0 and crash the first command that
/// touched it. These pin the declared defaults to the load boundary.
/// </summary>
public class ConfigDefaultsTests {
    static ProfileConfig Load(string json) => ConfigMigration.MigrateIfNeeded(json).Config;

    const string MinimalV2 = """
        {
            "version": 2,
            "active_profile": "test",
            "profiles": { "test": { "server_url": "https://example.test" } }
        }
        """;

    [Test]
    public async Task Absent_profile_bindings_is_an_empty_map() {
        await Assert.That(Load(MinimalV2).ProfileBindings).IsNotNull().And.IsEmpty();
    }

    [Test]
    public async Task Absent_profiles_is_an_empty_map() {
        var config = Load("""{ "version": 2, "active_profile": "test" }""");

        await Assert.That(config.Profiles).IsNotNull().And.IsEmpty();
    }

    [Test]
    public async Task Absent_active_profile_falls_back_to_default() {
        var config = Load("""{ "version": 2, "profiles": { "default": {} } }""");

        await Assert.That(config.ActiveProfile).IsEqualTo("default");
    }

    [Test]
    public async Task Absent_cwd_remap_is_an_empty_array() {
        await Assert.That(Load(MinimalV2).CwdRemap).IsNotNull().And.IsEmpty();
    }

    [Test]
    public async Task Absent_profile_collections_are_empty_arrays() {
        var profile = Load(MinimalV2).Profiles["test"];

        await Assert.That(profile.ExcludedRepos).IsNotNull().And.IsEmpty();
        await Assert.That(profile.ExcludedPaths).IsNotNull().And.IsEmpty();
        await Assert.That(profile.Remotes).IsNotNull().And.IsEmpty();
    }

    [Test]
    public async Task Absent_profile_strings_keep_their_declared_defaults() {
        var profile = Load(MinimalV2).Profiles["test"];

        await Assert.That(profile.DefaultVisibility).IsEqualTo("org_public");
        await Assert.That(profile.UpdateChannel).IsEqualTo("latest");
    }

    // update_check and daemon.max_agents declare defaults that differ from
    // default(T), so only the raw JSON can tell "absent" from an explicit false/0.

    [Test]
    public async Task Absent_update_check_defaults_to_true() {
        await Assert.That(Load(MinimalV2).Profiles["test"].UpdateCheck).IsTrue();
    }

    [Test]
    public async Task Explicit_update_check_false_is_preserved() {
        var config = Load("""
            {
                "version": 2,
                "profiles": { "test": { "update_check": false } }
            }
            """);

        await Assert.That(config.Profiles["test"].UpdateCheck).IsFalse();
    }

    [Test]
    public async Task Absent_max_agents_defaults_to_five() {
        var config = Load("""
            {
                "version": 2,
                "profiles": { "test": { "daemon": { "name": "dev" } } }
            }
            """);

        await Assert.That(config.Profiles["test"].Daemon!.MaxAgents).IsEqualTo(5);
    }

    [Test]
    public async Task Explicit_max_agents_is_preserved() {
        var config = Load("""
            {
                "version": 2,
                "profiles": { "test": { "daemon": { "max_agents": 2 } } }
            }
            """);

        await Assert.That(config.Profiles["test"].Daemon!.MaxAgents).IsEqualTo(2);
    }

    [Test]
    public async Task Null_valued_keys_are_treated_as_absent() {
        var config = Load("""
            {
                "version": 2,
                "active_profile": null,
                "profiles": { "test": null },
                "profile_bindings": null,
                "cwd_remap": null
            }
            """);

        await Assert.That(config.ActiveProfile).IsEqualTo("default");
        await Assert.That(config.ProfileBindings).IsNotNull().And.IsEmpty();
        await Assert.That(config.CwdRemap).IsNotNull().And.IsEmpty();
        await Assert.That(config.Profiles["test"].DefaultVisibility).IsEqualTo("org_public");
    }

    [Test]
    public async Task Binding_with_no_target_profile_is_dropped() {
        var config = Load("""
            {
                "version": 2,
                "profiles": { "test": {} },
                "profile_bindings": { "/repos/orphan": null }
            }
            """);

        await Assert.That(config.ProfileBindings).IsEmpty();
    }

    // An unreadable "version" must not be mistaken for a v1 config: that path rebuilds the
    // config from flat fields a v2 file doesn't have and persists the result, dropping the
    // profiles map. It has to stay on the v2 branch, where the failure is the JsonException
    // AppConfig.LoadProfileConfig already reports and recovers from.
    [Test]
    public async Task Wrong_typed_version_reports_a_handled_error_instead_of_migrating() {
        var json = """{ "version": "2", "profiles": { "test": {} } }""";

        await Assert.That(() => ConfigMigration.MigrateIfNeeded(json)).Throws<JsonException>();
    }

    [Test]
    public async Task Wrong_typed_scalars_report_a_handled_error() {
        var json = """
            {
                "version": 2,
                "profiles": { "test": { "update_check": "yes" } }
            }
            """;

        await Assert.That(() => ConfigMigration.MigrateIfNeeded(json)).Throws<JsonException>();
    }

    // The reported crash: every command aborted here before dispatching, so a user
    // in this state could not even repair the file with `kcap config set`.
    [Test]
    public async Task Resolver_binding_lookup_survives_a_minimal_config() {
        var resolver = new ProfileResolver(
            Load(MinimalV2),
            cliServerUrl: null,
            envUrl: null,
            envProfile: null,
            repoConfig: null,
            repoRemoteUrls: [],
            repoPath: "/repos/anything"
        );

        var result = resolver.Resolve();

        await Assert.That(result.ServerUrl).IsEqualTo("https://example.test");
        await Assert.That(result.ProfileName).IsEqualTo("test");
    }

    [Test]
    public async Task Resolver_active_profile_fallback_survives_a_config_with_no_profiles() {
        var resolver = new ProfileResolver(
            Load("""{ "version": 2 }"""),
            cliServerUrl: null,
            envUrl: null,
            envProfile: null,
            repoConfig: null,
            repoRemoteUrls: [],
            repoPath: "/repos/anything"
        );

        var result = resolver.Resolve();

        await Assert.That(result.ServerUrl).IsNull();
        await Assert.That(result.Warning).Contains("not found");
    }
}
