using System.Text.Json;
using kapacitor.Config;

namespace kapacitor.Tests.Unit;

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
                    ServerUrl = "https://contoso.kapacitor.io",
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
        await Assert.That(deserialized.Profiles["contoso"].ServerUrl).IsEqualTo("https://contoso.kapacitor.io");
        await Assert.That(deserialized.Profiles["contoso"].Remotes).Contains("github.com/contoso/*");
        await Assert.That(deserialized.ProfileBindings["/home/user/contoso-project"]).IsEqualTo("contoso");
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
}
