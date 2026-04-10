using kapacitor.Config;

namespace kapacitor.Tests.Unit;

public class ProfileResolverTests {
    static ProfileConfig TwoProfileConfig() => new() {
        ActiveProfile = "default",
        Profiles = new Dictionary<string, Profile> {
            ["default"] = new() { ServerUrl = "https://default.com" },
            ["contoso"] = new() {
                ServerUrl = "https://contoso.kapacitor.io",
                Remotes = ["github.com/contoso/*"]
            }
        },
        ProfileBindings = new Dictionary<string, string> {
            ["/repos/bound-project"] = "contoso"
        }
    };

    [Test]
    public async Task Resolve_CliServerUrlFlag_BypassesProfiles() {
        var resolver = new ProfileResolver(
            TwoProfileConfig(),
            cliServerUrl: "https://override.com",
            envUrl: null,
            envProfile: null,
            repoConfig: null,
            repoRemoteUrls: [],
            repoPath: null
        );

        var result = resolver.Resolve();

        await Assert.That(result.ServerUrl).IsEqualTo("https://override.com");
        await Assert.That(result.ProfileName).IsNull();
    }

    [Test]
    public async Task Resolve_EnvUrl_BypassesProfiles() {
        var resolver = new ProfileResolver(
            TwoProfileConfig(),
            cliServerUrl: null,
            envUrl: "https://env-override.com",
            envProfile: null,
            repoConfig: null,
            repoRemoteUrls: [],
            repoPath: null
        );

        var result = resolver.Resolve();

        await Assert.That(result.ServerUrl).IsEqualTo("https://env-override.com");
        await Assert.That(result.ProfileName).IsNull();
    }

    [Test]
    public async Task Resolve_EnvProfile_ReturnsNamedProfile() {
        var resolver = new ProfileResolver(
            TwoProfileConfig(),
            cliServerUrl: null,
            envUrl: null,
            envProfile: "contoso",
            repoConfig: null,
            repoRemoteUrls: [],
            repoPath: null
        );

        var result = resolver.Resolve();

        await Assert.That(result.ServerUrl).IsEqualTo("https://contoso.kapacitor.io");
        await Assert.That(result.ProfileName).IsEqualTo("contoso");
    }

    [Test]
    public async Task Resolve_RepoConfig_MatchesLocalProfile() {
        var resolver = new ProfileResolver(
            TwoProfileConfig(),
            cliServerUrl: null,
            envUrl: null,
            envProfile: null,
            repoConfig: new RepoConfig { Profile = "contoso", ServerUrl = "https://contoso.kapacitor.io" },
            repoRemoteUrls: [],
            repoPath: null
        );

        var result = resolver.Resolve();

        await Assert.That(result.ServerUrl).IsEqualTo("https://contoso.kapacitor.io");
        await Assert.That(result.ProfileName).IsEqualTo("contoso");
    }

    [Test]
    public async Task Resolve_GitRemoteMatch_ReturnsMatchedProfile() {
        var resolver = new ProfileResolver(
            TwoProfileConfig(),
            cliServerUrl: null,
            envUrl: null,
            envProfile: null,
            repoConfig: null,
            repoRemoteUrls: ["https://github.com/contoso/my-app.git"],
            repoPath: null
        );

        var result = resolver.Resolve();

        await Assert.That(result.ServerUrl).IsEqualTo("https://contoso.kapacitor.io");
        await Assert.That(result.ProfileName).IsEqualTo("contoso");
    }

    [Test]
    public async Task Resolve_ProfileBinding_ReturnsMatchedProfile() {
        var resolver = new ProfileResolver(
            TwoProfileConfig(),
            cliServerUrl: null,
            envUrl: null,
            envProfile: null,
            repoConfig: null,
            repoRemoteUrls: [],
            repoPath: "/repos/bound-project"
        );

        var result = resolver.Resolve();

        await Assert.That(result.ServerUrl).IsEqualTo("https://contoso.kapacitor.io");
        await Assert.That(result.ProfileName).IsEqualTo("contoso");
    }

    [Test]
    public async Task Resolve_NoSignals_FallsBackToActiveProfile() {
        var resolver = new ProfileResolver(
            TwoProfileConfig(),
            cliServerUrl: null,
            envUrl: null,
            envProfile: null,
            repoConfig: null,
            repoRemoteUrls: [],
            repoPath: null
        );

        var result = resolver.Resolve();

        await Assert.That(result.ServerUrl).IsEqualTo("https://default.com");
        await Assert.That(result.ProfileName).IsEqualTo("default");
    }

    [Test]
    public async Task Resolve_RepoConfigMismatchUrl_WarnsButUsesProfileUrl() {
        var config = TwoProfileConfig();
        var resolver = new ProfileResolver(
            config,
            cliServerUrl: null,
            envUrl: null,
            envProfile: null,
            repoConfig: new RepoConfig { Profile = "contoso", ServerUrl = "https://stale-url.com" },
            repoRemoteUrls: [],
            repoPath: null
        );

        var result = resolver.Resolve();

        await Assert.That(result.ServerUrl).IsEqualTo("https://contoso.kapacitor.io");
        await Assert.That(result.Warning).IsNotNull();
        // Warning should mention "stale"
    }

    [Test]
    public async Task Resolve_RepoConfigUnknownProfile_ReturnsNullServerUrl() {
        var config = TwoProfileConfig();
        var resolver = new ProfileResolver(
            config,
            cliServerUrl: null,
            envUrl: null,
            envProfile: null,
            repoConfig: new RepoConfig { Profile = "unknown", ServerUrl = "https://unknown.com" },
            repoRemoteUrls: [],
            repoPath: null
        );

        var result = resolver.Resolve();

        await Assert.That(result.ServerUrl).IsNull();
        await Assert.That(result.Warning).IsNotNull();
        // Warning should mention "unknown"
    }
}
