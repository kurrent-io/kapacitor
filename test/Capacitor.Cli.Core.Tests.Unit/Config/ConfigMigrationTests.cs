using System.Text.Json;
using Capacitor.Cli.Core.Config;

namespace Capacitor.Cli.Core.Tests.Unit.Config;

public class ConfigMigrationTests {
    [Test]
    public async Task Migrate_V1FlatConfig_CreatesDefaultProfile() {
        var v1Json = """
            {
                "server_url": "https://my-server.com",
                "daemon": { "name": "dev", "max_agents": 3 },
                "default_visibility": "private",
                "update_check": false,
                "excluded_repos": ["owner/secret"]
            }
            """;

        var (config, wasMigrated, _) = ConfigMigration.MigrateIfNeeded(v1Json);

        await Assert.That(wasMigrated).IsTrue();

        await Assert.That(config.Version).IsEqualTo(2);
        await Assert.That(config.ActiveProfile).IsEqualTo("default");
        await Assert.That(config.Profiles).ContainsKey("default");

        var defaultProfile = config.Profiles["default"];
        await Assert.That(defaultProfile.ServerUrl).IsEqualTo("https://my-server.com");
        await Assert.That(defaultProfile.Daemon!.Name).IsEqualTo("dev");
        await Assert.That(defaultProfile.Daemon.MaxAgents).IsEqualTo(3);
        await Assert.That(defaultProfile.DefaultVisibility).IsEqualTo("private");
        await Assert.That(defaultProfile.UpdateCheck).IsFalse();
        await Assert.That(defaultProfile.ExcludedRepos).Contains("owner/secret");
    }

    [Test]
    public async Task Migrate_V2Config_NoMigration() {
        var v2Json = """
            {
                "version": 2,
                "active_profile": "default",
                "profiles": {
                    "default": { "server_url": "https://example.com" }
                },
                "profile_bindings": {}
            }
            """;

        var result = ConfigMigration.MigrateIfNeeded(v2Json);

        await Assert.That(result.WasMigrated).IsFalse();
        await Assert.That(result.Config.ActiveProfile).IsEqualTo("default");
    }

    [Test]
    public async Task Migrate_EmptyJson_CreatesEmptyV2() {
        var result = ConfigMigration.MigrateIfNeeded("{}");

        await Assert.That(result.WasMigrated).IsTrue();
        await Assert.That(result.Config.Version).IsEqualTo(2);
        await Assert.That(result.Config.Profiles).ContainsKey("default");
    }

    [Test]
    public async Task Migrate_NonObjectJson_CreatesEmptyV2() {
        var result = ConfigMigration.MigrateIfNeeded("[]");

        await Assert.That(result.WasMigrated).IsTrue();
        await Assert.That(result.Config.Version).IsEqualTo(2);
        await Assert.That(result.Config.Profiles).ContainsKey("default");
    }

    [Test]
    public async Task ProfileConfig_RoundTrips_ThroughJson() {
        var config = new ProfileConfig {
            ActiveProfile = "default",
            Profiles = new Dictionary<string, Profile> {
                ["default"] = new() {
                    ServerUrl = "https://example.com",
                    Daemon = new DaemonSettings { Name = "dev", MaxAgents = 5 },
                    DefaultVisibility = "org_public",
                    UpdateCheck = true,
                    ExcludedRepos = []
                },
                ["contoso"] = new() {
                    ServerUrl = "https://contoso.kcap.io",
                    Daemon = new DaemonSettings { Name = "consulting", MaxAgents = 2 },
                    DefaultVisibility = "private",
                    UpdateCheck = true,
                    ExcludedRepos = [],
                    Remotes = ["github.com/contoso/*"]
                }
            },
            ProfileBindings = new Dictionary<string, string> {
                ["/home/user/contoso-project"] = "contoso"
            }
        };

        var json = JsonSerializer.Serialize(config, ProfileConfigJsonContext.Default.ProfileConfig);
        var deserialized = JsonSerializer.Deserialize(json, ProfileConfigJsonContext.Default.ProfileConfig);

        await Assert.That(deserialized).IsNotNull();
        await Assert.That(deserialized!.Version).IsEqualTo(2);
        await Assert.That(deserialized.ActiveProfile).IsEqualTo("default");
        await Assert.That(deserialized.Profiles).Count().IsEqualTo(2);
        await Assert.That(deserialized.Profiles["contoso"].ServerUrl).IsEqualTo("https://contoso.kcap.io");
        await Assert.That(deserialized.Profiles["contoso"].Remotes).Contains("github.com/contoso/*");
        await Assert.That(deserialized.ProfileBindings["/home/user/contoso-project"]).IsEqualTo("contoso");
    }

    [Test]
    public async Task ProfileConfig_ExcludedPaths_RoundTrips() {
        var json = """{ "excluded_paths": ["/home/alice/secret", "/srv/private"] }""";
        var profile = JsonSerializer.Deserialize(json, ProfileConfigJsonContext.Default.Profile)!;

        await Assert.That(profile.ExcludedPaths).Contains("/home/alice/secret");
        await Assert.That(profile.ExcludedPaths).Contains("/srv/private");
    }

    [Test]
    public async Task Active_returns_the_entry_active_profile_names() {
        var activeProfile = new Profile { ExcludedPaths = ["/from/active"] };
        var config        = new ProfileConfig {
            ActiveProfile = "default",
            Profiles      = new Dictionary<string, Profile> { ["default"] = activeProfile }
        };

        await Assert.That(config.Active).IsSameReferenceAs(activeProfile);
    }

    [Test]
    public async Task Active_is_null_when_active_profile_names_no_entry() {
        var config = new ProfileConfig {
            ActiveProfile = "missing",
            Profiles      = new Dictionary<string, Profile>()
        };

        await Assert.That(config.Active).IsNull();
    }

    [Test]
    public async Task ProfileConfig_DisableSessionGuidelines_RoundTripsTrue() {
        var json   = """{ "disable_session_guidelines": true }""";
        var config = JsonSerializer.Deserialize(json, ProfileConfigJsonContext.Default.Profile)!;
        await Assert.That(config.DisableSessionGuidelines).IsTrue();
    }

    [Test]
    public async Task ProfileConfig_DisableSessionGuidelines_NullWhenAbsent() {
        var json   = "{}";
        var config = JsonSerializer.Deserialize(json, ProfileConfigJsonContext.Default.Profile)!;
        await Assert.That(config.DisableSessionGuidelines).IsNull();
    }

    // --- update_check migration-default quirk ---
    //
    // STJ source-gen does not apply a record member-initializer default (`= true`)
    // for a JSON property absent from the payload, so deserializing a v1 config
    // through ConfigJsonContext.Default.LegacyV1Config yields UpdateCheck == false
    // when "update_check" is missing — even though the v1 default was true. The
    // migration must read the raw JSON node to tell "absent" apart from "explicitly
    // false" and preserve the true v1 default.

    [Test]
    public async Task Migrate_V1MissingUpdateCheck_DefaultsToTrue() {
        var v1Json = """
            {
                "server_url": "https://my-server.com"
            }
            """;

        var result = ConfigMigration.MigrateIfNeeded(v1Json);

        await Assert.That(result.WasMigrated).IsTrue();
        await Assert.That(result.Config.Profiles["default"].UpdateCheck).IsTrue();
    }

    [Test]
    public async Task Migrate_V1UpdateCheckFalse_StaysFalse() {
        var v1Json = """
            {
                "server_url": "https://my-server.com",
                "update_check": false
            }
            """;

        var result = ConfigMigration.MigrateIfNeeded(v1Json);

        await Assert.That(result.WasMigrated).IsTrue();
        await Assert.That(result.Config.Profiles["default"].UpdateCheck).IsFalse();
    }

    [Test]
    public async Task Migrate_V1UpdateCheckTrue_StaysTrue() {
        var v1Json = """
            {
                "server_url": "https://my-server.com",
                "update_check": true
            }
            """;

        var result = ConfigMigration.MigrateIfNeeded(v1Json);

        await Assert.That(result.WasMigrated).IsTrue();
        await Assert.That(result.Config.Profiles["default"].UpdateCheck).IsTrue();
    }

    [Test]
    public async Task Migrate_V1OtherFields_MapOntoDefaultProfile() {
        var v1Json = """
            {
                "server_url": "https://my-server.com",
                "default_visibility": "private",
                "excluded_repos": ["owner/secret", "owner/other"]
            }
            """;

        var result = ConfigMigration.MigrateIfNeeded(v1Json);

        await Assert.That(result.WasMigrated).IsTrue();

        var defaultProfile = result.Config.Profiles["default"];
        await Assert.That(defaultProfile.ServerUrl).IsEqualTo("https://my-server.com");
        await Assert.That(defaultProfile.DefaultVisibility).IsEqualTo("private");
        await Assert.That(defaultProfile.ExcludedRepos).Contains("owner/secret");
        await Assert.That(defaultProfile.ExcludedRepos).Contains("owner/other");
        // The migration-default quirk fix: update_check absent from v1 JSON -> true.
        await Assert.That(defaultProfile.UpdateCheck).IsTrue();
    }

    [Test]
    public async Task Migrate_V2Config_IsPassthroughAndUnaffectedByUpdateCheckFix() {
        var v2Json = """
            {
                "version": 2,
                "active_profile": "default",
                "profiles": {
                    "default": { "server_url": "https://example.com", "update_check": false }
                },
                "profile_bindings": {}
            }
            """;

        var result = ConfigMigration.MigrateIfNeeded(v2Json);

        await Assert.That(result.WasMigrated).IsFalse();
        await Assert.That(result.ShouldPersist).IsFalse();
        // The v1-only migration-default fix must not leak into the v2 passthrough path.
        await Assert.That(result.Config.Profiles["default"].UpdateCheck).IsFalse();
    }
}
