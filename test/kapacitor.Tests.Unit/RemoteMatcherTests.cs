using kapacitor.Config;

namespace kapacitor.Tests.Unit;

public class RemoteMatcherTests {
    [Test]
    [Arguments("https://github.com/contoso/repo.git", "github.com/contoso/repo")]
    [Arguments("https://github.com/contoso/repo", "github.com/contoso/repo")]
    [Arguments("git@github.com:contoso/repo.git", "github.com/contoso/repo")]
    [Arguments("git@github.com:contoso/repo", "github.com/contoso/repo")]
    [Arguments("ssh://git@github.com/contoso/repo.git", "github.com/contoso/repo")]
    [Arguments("https://user:token@github.com/contoso/repo.git", "github.com/contoso/repo")]
    [Arguments("https://oauth2:ghp_abc@github.com/contoso/repo", "github.com/contoso/repo")]
    public async Task NormalizeRemoteUrl_VariousFormats_ReturnsCanonical(string input, string expected) {
        var result = RemoteMatcher.NormalizeRemoteUrl(input);

        await Assert.That(result).IsEqualTo(expected);
    }

    [Test]
    [Arguments("")]
    [Arguments("not-a-url")]
    public async Task NormalizeRemoteUrl_Invalid_ReturnsNull(string input) {
        var result = RemoteMatcher.NormalizeRemoteUrl(input);

        await Assert.That(result).IsNull();
    }

    [Test]
    public async Task FindMatchingProfile_SingleMatch_ReturnsProfileName() {
        var profiles = new Dictionary<string, Profile> {
            ["default"] = new() { ServerUrl = "https://default.com" },
            ["contoso"] = new() {
                ServerUrl = "https://contoso.kapacitor.io",
                Remotes = ["github.com/contoso/*", "github.com/contoso-labs/*"]
            }
        };
        var remoteUrls = new[] { "https://github.com/contoso/my-app.git" };

        var result = RemoteMatcher.FindMatchingProfile(profiles, remoteUrls);

        await Assert.That(result).IsEqualTo("contoso");
    }

    [Test]
    public async Task FindMatchingProfile_NoMatch_ReturnsNull() {
        var profiles = new Dictionary<string, Profile> {
            ["contoso"] = new() {
                ServerUrl = "https://contoso.kapacitor.io",
                Remotes = ["github.com/contoso/*"]
            }
        };
        var remoteUrls = new[] { "https://github.com/other-org/repo.git" };

        var result = RemoteMatcher.FindMatchingProfile(profiles, remoteUrls);

        await Assert.That(result).IsNull();
    }

    [Test]
    public async Task FindMatchingProfile_MultipleMatches_Throws() {
        var profiles = new Dictionary<string, Profile> {
            ["alpha"] = new() {
                ServerUrl = "https://alpha.com",
                Remotes = ["github.com/shared-org/*"]
            },
            ["beta"] = new() {
                ServerUrl = "https://beta.com",
                Remotes = ["github.com/shared-org/*"]
            }
        };
        var remoteUrls = new[] { "https://github.com/shared-org/repo.git" };

        var exception = await Assert.That((Action)Act).ThrowsException();
        await Assert.That(exception!.Message).Contains("alpha");
        await Assert.That(exception.Message).Contains("beta");

        return;

        void Act() => RemoteMatcher.FindMatchingProfile(profiles, remoteUrls);
    }

    [Test]
    public async Task FindMatchingProfile_SshUrl_MatchesHttpsPattern() {
        var profiles = new Dictionary<string, Profile> {
            ["contoso"] = new() {
                ServerUrl = "https://contoso.kapacitor.io",
                Remotes = ["github.com/contoso/*"]
            }
        };
        var remoteUrls = new[] { "git@github.com:contoso/my-app.git" };

        var result = RemoteMatcher.FindMatchingProfile(profiles, remoteUrls);

        await Assert.That(result).IsEqualTo("contoso");
    }
}
