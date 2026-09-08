using Capacitor.App.Services.Update;
using Velopack;

namespace Capacitor.App.Tests.Unit;

public class PrereleaseFilteringSourceTests {
    static VelopackAsset Asset(string version) =>
        new() { PackageId = "KurrentCapacitor", Version = SemanticVersion.Parse(version), Type = VelopackAssetType.Full, FileName = $"KurrentCapacitor-{version}-osx-arm64-full.nupkg" };

    [Test]
    public async Task Stable_install_drops_prereleases() {
        var kept = PrereleaseFilteringSource.Filter([Asset("0.12.0"), Asset("0.12.1-beta.1"), Asset("0.12.1")], allowPrerelease: false);

        await Assert.That(kept.Select(a => a.Version.ToString())).IsEquivalentTo(["0.12.0", "0.12.1"]);
    }

    [Test]
    public async Task Prerelease_install_keeps_everything() {
        var kept = PrereleaseFilteringSource.Filter([Asset("0.12.0"), Asset("0.12.1-beta.1")], allowPrerelease: true);

        await Assert.That(kept.Length).IsEqualTo(2);
    }

    [Test]
    public async Task Empty_feed_stays_empty() {
        await Assert.That(PrereleaseFilteringSource.Filter([], allowPrerelease: false)).IsEmpty();
    }
}
