namespace Capacitor.Cli.Core.Tests.Unit;

public class GitUrlParserTests {
    [Test]
    [Arguments("https://github.com/kurrent-io/kcap-cli", "kurrent-io", "kcap-cli")]
    [Arguments("https://github.com/kurrent-io/kcap.git", "kurrent-io", "kcap")]
    [Arguments("https://github.com/owner/repo-name", "owner", "repo-name")]
    [Arguments("https://github.com/owner/repo-name.git", "owner", "repo-name")]
    [Arguments("http://github.com/owner/repo", "owner", "repo")]
    [Arguments("http://github.com/owner/repo.git", "owner", "repo")]
    [Arguments("https://gitlab.com/my-org/my-repo", "my-org", "my-repo")]
    [Arguments("https://gitlab.com/my-org/my-repo.git", "my-org", "my-repo")]
    [Arguments("https://github.com/kurrent-io/kcap-server", "kurrent-io", "kcap-server")]
    [Arguments("https://github.com/kurrent-io/kcap-server.git", "kurrent-io", "kcap-server")]
    [Arguments("https://github.com/owner/a.b.c", "owner", "a.b.c")]
    [Arguments("https://github.com/owner/a.b.c.git", "owner", "a.b.c")]
    [Arguments("https://gitlab.com/group/subgroup/project", "group/subgroup", "project")]
    [Arguments("https://gitlab.com/group/subgroup/project.git", "group/subgroup", "project")]
    [Arguments("https://gitlab.com/a/b/c/deep-project.git", "a/b/c", "deep-project")]
    public async Task ParseRemoteUrl_HttpsUrls_ReturnsOwnerAndRepo(string url, string expectedOwner, string expectedRepo) {
        var (owner, repoName) = GitUrlParser.ParseRemoteUrl(url);

        await Assert.That(owner).IsEqualTo(expectedOwner);
        await Assert.That(repoName).IsEqualTo(expectedRepo);
    }

    [Test]
    [Arguments("git@github.com:kurrent-io/kcap-cli", "kurrent-io", "kcap-cli")]
    [Arguments("git@github.com:kurrent-io/kcap.git", "kurrent-io", "kcap")]
    [Arguments("git@github.com:owner/repo-name", "owner", "repo-name")]
    [Arguments("git@github.com:owner/repo-name.git", "owner", "repo-name")]
    [Arguments("git@gitlab.com:my-org/my-repo", "my-org", "my-repo")]
    [Arguments("git@gitlab.com:my-org/my-repo.git", "my-org", "my-repo")]
    [Arguments("git@my-host.example.com:org/repo", "org", "repo")]
    [Arguments("git@github.com:kurrent-io/kcap-server", "kurrent-io", "kcap-server")]
    [Arguments("git@github.com:kurrent-io/kcap-server.git", "kurrent-io", "kcap-server")]
    [Arguments("git@github.com:owner/a.b.c", "owner", "a.b.c")]
    [Arguments("git@github.com:owner/a.b.c.git", "owner", "a.b.c")]
    [Arguments("git@gitlab.com:group/subgroup/project.git", "group/subgroup", "project")]
    [Arguments("git@gitlab.com:a/b/c/deep-project", "a/b/c", "deep-project")]
    public async Task ParseRemoteUrl_SshUrls_ReturnsOwnerAndRepo(string url, string expectedOwner, string expectedRepo) {
        var (owner, repoName) = GitUrlParser.ParseRemoteUrl(url);

        await Assert.That(owner).IsEqualTo(expectedOwner);
        await Assert.That(repoName).IsEqualTo(expectedRepo);
    }

    [Test]
    [Arguments("ssh://git@gitlab.com/group/project.git", "group", "project")]
    [Arguments("ssh://git@gitlab.corp.com:2222/org/repo.git", "org", "repo")]
    public async Task ParseRemoteUrl_SshProtoUrls_ReturnsOwnerAndRepo(string url, string expectedOwner, string expectedRepo) {
        var (owner, repoName) = GitUrlParser.ParseRemoteUrl(url);

        await Assert.That(owner).IsEqualTo(expectedOwner);
        await Assert.That(repoName).IsEqualTo(expectedRepo);
    }

    [Test]
    public async Task ParseRemoteUrl_SshProtoUrl_NestedGroup_ReturnsMultiSegmentOwner() {
        var (owner, repoName) = GitUrlParser.ParseRemoteUrl("ssh://git@gitlab.com/group/sub/project.git");

        await Assert.That(owner).IsEqualTo("group/sub");
        await Assert.That(repoName).IsEqualTo("project");
    }

    [Test]
    public async Task ParseRemoteUrl_Null_ReturnsBothNull() {
        var (owner, repoName) = GitUrlParser.ParseRemoteUrl(null);

        await Assert.That(owner).IsNull();
        await Assert.That(repoName).IsNull();
    }

    [Test]
    [Arguments("")]
    [Arguments("not-a-url")]
    [Arguments("ftp://github.com/owner/repo")]
    [Arguments("https://")]
    [Arguments("git@")]
    public async Task ParseRemoteUrl_InvalidUrls_ReturnsBothNull(string url) {
        var (owner, repoName) = GitUrlParser.ParseRemoteUrl(url);

        await Assert.That(owner).IsNull();
        await Assert.That(repoName).IsNull();
    }
}
