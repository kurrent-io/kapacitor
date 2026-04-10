using System.Text.Json;
using kapacitor.Config;

namespace kapacitor.Tests.Unit;

public class ConfigMigrationTests {
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
}
