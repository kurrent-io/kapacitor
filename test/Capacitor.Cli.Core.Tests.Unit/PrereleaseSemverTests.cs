namespace Capacitor.Cli.Core.Tests.Unit;

public class PrereleaseSemverTests {
    [Test]
    public async Task Orders_stable_versions() {
        await Assert.That(PrereleaseSemver.IsNewer("0.8.0", "0.7.9")).IsTrue();
        await Assert.That(PrereleaseSemver.IsNewer("0.7.9", "0.8.0")).IsFalse();
        await Assert.That(PrereleaseSemver.IsNewer("0.7.0", "0.7.0")).IsFalse();
    }

    [Test]
    public async Task Prerelease_is_lower_than_its_release() {
        await Assert.That(PrereleaseSemver.IsNewer("0.7.0", "0.7.0-beta.1")).IsTrue();
        await Assert.That(PrereleaseSemver.IsNewer("0.7.0-beta.1", "0.7.0")).IsFalse();
    }

    [Test]
    public async Task Orders_prereleases_numerically_not_lexically() {
        await Assert.That(PrereleaseSemver.IsNewer("0.7.0-beta.2", "0.7.0-beta.1")).IsTrue();
        await Assert.That(PrereleaseSemver.IsNewer("0.7.0-beta.10", "0.7.0-beta.2")).IsTrue();
    }

    [Test]
    public async Task Numeric_identifier_ranks_below_alphanumeric() {
        // SemVer 2.0: 0.7.0-alpha < 0.7.0-alpha.1 ; numeric < alphanumeric at same slot
        await Assert.That(PrereleaseSemver.IsNewer("0.7.0-alpha.1", "0.7.0-alpha")).IsTrue();
        await Assert.That(PrereleaseSemver.IsNewer("0.7.0-alpha.beta", "0.7.0-alpha.1")).IsTrue();
    }

    [Test]
    public async Task Ignores_build_metadata() {
        await Assert.That(PrereleaseSemver.Compare("0.7.0+aaa", "0.7.0+bbb")).IsEqualTo(0);
        await Assert.That(PrereleaseSemver.IsNewer("0.7.1+x", "0.7.0+y")).IsTrue();
    }

    [Test]
    public async Task Unparseable_or_unknown_never_claims_newer() {
        await Assert.That(PrereleaseSemver.IsNewer("unknown", "0.7.0")).IsFalse();
        await Assert.That(PrereleaseSemver.IsNewer("0.7.0", "unknown")).IsFalse();
        await Assert.That(PrereleaseSemver.IsNewer("0.7.0", null)).IsFalse();
        await Assert.That(PrereleaseSemver.IsNewer("garbage", "0.7.0")).IsFalse();
    }
}
