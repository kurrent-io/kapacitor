namespace kapacitor.Tests.Unit;

public class GitUrlParserTests {
    [Test]
    [Arguments("https://github.com/kurrent-io/kapacitor", "kurrent-io", "kapacitor")]
    [Arguments("https://github.com/kurrent-io/kapacitor.git", "kurrent-io", "kapacitor")]
    [Arguments("https://github.com/owner/repo-name", "owner", "repo-name")]
    [Arguments("https://github.com/owner/repo-name.git", "owner", "repo-name")]
    [Arguments("http://github.com/owner/repo", "owner", "repo")]
    [Arguments("http://github.com/owner/repo.git", "owner", "repo")]
    [Arguments("https://gitlab.com/my-org/my-repo", "my-org", "my-repo")]
    [Arguments("https://gitlab.com/my-org/my-repo.git", "my-org", "my-repo")]
    [Arguments("https://github.com/kurrent-io/Kurrent.Capacitor", "kurrent-io", "Kurrent.Capacitor")]
    [Arguments("https://github.com/kurrent-io/Kurrent.Capacitor.git", "kurrent-io", "Kurrent.Capacitor")]
    [Arguments("https://github.com/owner/a.b.c.git", "owner", "a.b.c")]
    public async Task ParseRemoteUrl_HttpsUrls_ReturnsOwnerAndRepo(string url, string expectedOwner, string expectedRepo) {
        var (owner, repoName) = GitUrlParser.ParseRemoteUrl(url);

        await Assert.That(owner).IsEqualTo(expectedOwner);
        await Assert.That(repoName).IsEqualTo(expectedRepo);
    }

    [Test]
    [Arguments("git@github.com:kurrent-io/kapacitor", "kurrent-io", "kapacitor")]
    [Arguments("git@github.com:kurrent-io/kapacitor.git", "kurrent-io", "kapacitor")]
    [Arguments("git@github.com:owner/repo-name", "owner", "repo-name")]
    [Arguments("git@github.com:owner/repo-name.git", "owner", "repo-name")]
    [Arguments("git@gitlab.com:my-org/my-repo", "my-org", "my-repo")]
    [Arguments("git@gitlab.com:my-org/my-repo.git", "my-org", "my-repo")]
    [Arguments("git@my-host.example.com:org/repo", "org", "repo")]
    [Arguments("git@github.com:kurrent-io/Kurrent.Capacitor", "kurrent-io", "Kurrent.Capacitor")]
    [Arguments("git@github.com:kurrent-io/Kurrent.Capacitor.git", "kurrent-io", "Kurrent.Capacitor")]
    [Arguments("git@github.com:owner/a.b.c.git", "owner", "a.b.c")]
    public async Task ParseRemoteUrl_SshUrls_ReturnsOwnerAndRepo(string url, string expectedOwner, string expectedRepo) {
        var (owner, repoName) = GitUrlParser.ParseRemoteUrl(url);

        await Assert.That(owner).IsEqualTo(expectedOwner);
        await Assert.That(repoName).IsEqualTo(expectedRepo);
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
