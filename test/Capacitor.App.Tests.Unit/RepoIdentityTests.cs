using Capacitor.App.Services;

namespace Capacitor.App.Tests.Unit;

public class RepoIdentityTests {
    [Test]
    public async Task LocalAndRemoteCheckoutsOfOneRepoShareAKey() {
        var resolver = new RepoIdentityResolver(_ => "git@github.com:Kurrent-io/kcap-cli.git");
        var local = resolver.ForLocalRoot("/home/me/kcap-cli");
        var remote = RepoIdentityResolver.ForRemote("kurrent-io", "kcap-cli", "/work/kcap-cli", "u1/work-mac");
        await Assert.That(local.Key).IsEqualTo(remote.Key);
        await Assert.That(local.Key).IsEqualTo("repo:kurrent-io/kcap-cli");
    }

    [Test]
    public async Task RemoteWithoutIdentityIsMachineScoped() {
        var a = RepoIdentityResolver.ForRemote(null, null, "/work/repo", "u1/work-mac");
        var b = RepoIdentityResolver.ForRemote(null, null, "/work/repo", "u1/home-pc");
        await Assert.That(a.Key).IsNotEqualTo(b.Key);
        await Assert.That(a.Label).IsEqualTo("repo");
    }

    [Test]
    public async Task LocalWithoutRemoteStaysPathScoped() {
        var resolver = new RepoIdentityResolver(_ => null);
        var id = resolver.ForLocalRoot("/home/me/private");
        await Assert.That(id.Key).IsEqualTo("path:/home/me/private");
        await Assert.That(id.Label).IsEqualTo("private");
    }

    [Test]
    public async Task LocalResolutionIsMemoized() {
        var reads = 0;
        var resolver = new RepoIdentityResolver(_ => { reads++; return null; });
        resolver.ForLocalRoot("/r");
        resolver.ForLocalRoot("/r");
        await Assert.That(reads).IsEqualTo(1);
    }
}
