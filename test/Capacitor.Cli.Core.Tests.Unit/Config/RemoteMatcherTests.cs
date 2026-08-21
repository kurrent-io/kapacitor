using Capacitor.Cli.Core.Config;

namespace Capacitor.Cli.Core.Tests.Unit.Config;

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
                ServerUrl = "https://contoso.kcap.io",
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
                ServerUrl = "https://contoso.kcap.io",
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
                ServerUrl = "https://contoso.kcap.io",
                Remotes = ["github.com/contoso/*"]
            }
        };
        var remoteUrls = new[] { "git@github.com:contoso/my-app.git" };

        var result = RemoteMatcher.FindMatchingProfile(profiles, remoteUrls);

        await Assert.That(result).IsEqualTo("contoso");
    }

    [Test]
    public async Task ExtractHost_from_ssh_and_https() {
        await Assert.That(RemoteMatcher.ExtractHost("git@github.com:kurrent-io/kcap.git")).IsEqualTo("github.com");
        await Assert.That(RemoteMatcher.ExtractHost("https://gitlab.com/group/project.git")).IsEqualTo("gitlab.com");
        await Assert.That(RemoteMatcher.ExtractHost("ssh://git@ghe.corp.com/team/app")).IsEqualTo("ghe.corp.com");
        await Assert.That(RemoteMatcher.ExtractHost("not a url")).IsNull();
    }

    [Test]
    public async Task ExtractHost_lowercases_mixed_case_host() {
        // gh/glab auth host keys are lowercase, and GitProviderRouter compares hosts
        // case-sensitively, so a mixed-case remote host must be canonicalized here.
        await Assert.That(RemoteMatcher.ExtractHost("git@GitHub.com:o/r.git")).IsEqualTo("github.com");
    }

    [Test]
    public async Task ExtractHost_https_with_port_strips_port() {
        await Assert.That(RemoteMatcher.ExtractHost("https://ghe.corp.com:8443/org/repo.git")).IsEqualTo("ghe.corp.com");
    }

    [Test]
    public async Task ExtractHost_ssh_proto_with_port_strips_port() {
        await Assert.That(RemoteMatcher.ExtractHost("ssh://git@gitlab.corp.com:2222/org/repo.git")).IsEqualTo("gitlab.corp.com");
    }

    [Test]
    public async Task NormalizeRemoteUrl_https_with_port_drops_port() {
        await Assert.That(RemoteMatcher.NormalizeRemoteUrl("https://ghe.corp.com:8443/org/repo.git")).IsEqualTo("ghe.corp.com/org/repo");
    }

    [Test]
    public async Task PathAfterHost_strips_leading_host_segment() {
        await Assert.That(RemoteMatcher.PathAfterHost("github.com/kurrent-io/kcap")).IsEqualTo("kurrent-io/kcap");
        await Assert.That(RemoteMatcher.PathAfterHost("gitlab.com/group/project")).IsEqualTo("group/project");
        await Assert.That(RemoteMatcher.PathAfterHost("nohostonly")).IsNull();
    }

    [Test]
    public async Task PathAfterHost_edge_inputs_return_null() {
        await Assert.That(RemoteMatcher.PathAfterHost("")).IsNull();            // empty
        await Assert.That(RemoteMatcher.PathAfterHost("/")).IsNull();           // lone slash → no host, no path
        await Assert.That(RemoteMatcher.PathAfterHost("host/")).IsNull();       // trailing slash → no path
        await Assert.That(RemoteMatcher.PathAfterHost("/owner/repo")).IsNull(); // leading slash → no host segment
    }
}
