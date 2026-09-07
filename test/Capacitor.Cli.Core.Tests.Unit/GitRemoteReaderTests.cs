namespace Capacitor.Cli.Core.Tests.Unit;

public class GitRemoteReaderTests {
    [TempDir] public required TempDir Tmp { get; init; }

    void WriteConfig(string content) {
        Tmp.CreateDir(".git");
        Tmp.CreateFile(Path.Combine(".git", "config"), content);
    }

    [Test]
    public async Task ReadsTheOriginUrl() {
        WriteConfig("""
        [core]
            bare = false
        [remote "origin"]
            url = git@github.com:kurrent-io/kcap-cli.git
            fetch = +refs/heads/*:refs/remotes/origin/*
        [remote "fork"]
            url = git@github.com:someone/kcap-cli.git
        """);
        await Assert.That(GitRemoteReader.ReadOriginUrl(Tmp.Path))
            .IsEqualTo("git@github.com:kurrent-io/kcap-cli.git");
    }

    [Test]
    public async Task NoOriginSectionIsNull() {
        WriteConfig("""
        [remote "upstream"]
            url = https://github.com/kurrent-io/kcap-cli.git
        """);
        await Assert.That(GitRemoteReader.ReadOriginUrl(Tmp.Path)).IsNull();
    }

    [Test]
    public async Task MissingConfigIsNull() =>
        await Assert.That(GitRemoteReader.ReadOriginUrl(Tmp.Path)).IsNull();
}
