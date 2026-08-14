using Capacitor.App.Services;

namespace Capacitor.App.Tests.Unit;

public class KcapCliCompatibilityTests {
    [Test]
    [Arguments("0.12.0-beta.1", true)]
    [Arguments("0.12.0-beta.2", true)]
    [Arguments("0.12.0", true)]
    [Arguments("0.12.1-beta.1", true)]
    [Arguments("0.13.0", true)]
    [Arguments("0.11.9", false)]
    [Arguments("0.12.0-beta.0", false)]
    [Arguments("0.12.0-alpha.9", false)] // alpha < beta
    [Arguments("01.2.3", false)] // strict parse: leading-zero core
    [Arguments("0.12.0-beta.01", false)] // strict parse: leading-zero prerelease numeric
    [Arguments("0.12.0-", false)]
    [Arguments("0.12", false)]
    [Arguments("", false)]
    [Arguments(null, false)]
    [Arguments("unknown", false)]
    [Arguments("v0.12.0", false)]
    [Arguments("0.12.0+build.5", true)] // build metadata ignored
    [Arguments("0.12.0-beta.1+x", true)]
    public async Task Satisfies_reports_whether_version_meets_the_floor(string? version, bool expected) {
        await Assert.That(KcapCliCompatibility.Satisfies(version)).IsEqualTo(expected);
    }

    [Test]
    [Arguments("0.12.0-beta.1", true)]
    [Arguments("0.12.0", true)]
    [Arguments("0.11.9", true)] // valid grammar; below-floor is Satisfies' concern, not StrictParse's
    [Arguments("0.12.0-alpha.9", true)]
    [Arguments("01.2.3", false)]
    [Arguments("0.12.0-beta.01", false)]
    [Arguments("0.12.0-", false)]
    [Arguments("0.12", false)]
    [Arguments("", false)]
    [Arguments("unknown", false)]
    [Arguments("v0.12.0", false)]
    [Arguments("0.12.0+build.5", true)]
    [Arguments("0.12.0-beta.1+x", true)]
    public async Task StrictParse_validates_the_semver_grammar(string version, bool expected) {
        await Assert.That(KcapCliCompatibility.StrictParse(version)).IsEqualTo(expected);
    }
}
