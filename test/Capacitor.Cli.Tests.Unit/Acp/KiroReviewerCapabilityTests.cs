using Capacitor.Cli.Daemon;
using Capacitor.Cli.Daemon.Acp;

namespace Capacitor.Cli.Tests.Unit.Acp;

/// <summary>
/// The Kiro reviewer gate is fail-closed on three axes: platform, the operator's consent flag, and
/// whether the installed kiro-cli meets the MINIMUM version this daemon recorded.
/// </summary>
public class KiroReviewerCapabilityTests {
    /// <summary>Pinned, never read from the running host: these arms are about consent and versions,
    /// and letting the CI leg decide the platform made every one of them fail on Windows for a reason
    /// unrelated to what they assert.</summary>
    const bool Posix = true;

    [Test]
    public async Task EnabledAndAffirmed_IsTheOnlyPermittedCombination() =>
        await Assert.That(KiroReviewerCapability.Decide(Posix, true, "2.16.0", "2.16.0"))
            .IsEqualTo(KiroReviewerDecision.Allowed);

    [Test]
    public async Task DisabledByTheOperator_IsRefusedEvenOnAnAffirmedVersion() =>
        await Assert.That(KiroReviewerCapability.Decide(Posix, false, "2.16.0", "2.16.0"))
            .IsEqualTo(KiroReviewerDecision.Disabled);

    /// <summary>
    /// The direction that matters, and it is now the opposite of what this gate used to do: a NEWER
    /// build is ADMITTED. The record is a floor, not an affirmation of one specific build, because
    /// refusing every vendor patch release was a treadmill with no safety payoff. If this ever
    /// reverts to an equality compare, these arguments are what catch it.
    /// </summary>
    [Test]
    [Arguments("2.17.0")]
    [Arguments("2.16.1")]
    [Arguments("3.0.0")]
    public async Task ANewerVersion_IsAdmittedWithNoOperatorAction(string installed) =>
        await Assert.That(KiroReviewerCapability.Decide(Posix, true, installed, "2.16.0"))
            .IsEqualTo(KiroReviewerDecision.Allowed);

    /// <summary>The other half of "minimum": older than the record is still refused.</summary>
    [Test]
    [Arguments("2.15.2")]
    [Arguments("1.0.0")]
    public async Task AnOlderVersion_IsRefused(string installed) =>
        await Assert.That(KiroReviewerCapability.Decide(Posix, true, installed, "2.16.0"))
            .IsEqualTo(KiroReviewerDecision.VersionBelowMinimum);

    /// <summary>An unorderable pair must reach its OWN arm, never the below-minimum one — refusing an
    /// upgrade while calling it "too old" is the failure this arm exists to prevent.</summary>
    [Test]
    [Arguments("2.0.0", "1.2.3.4.5")]
    [Arguments("1.2.3.4.5", "2.0.0")]
    public async Task AnUnorderablePair_IsIncomparableNotBelowMinimum(string installed, string minimum) =>
        await Assert.That(KiroReviewerCapability.Decide(Posix, true, installed, minimum))
            .IsEqualTo(KiroReviewerDecision.VersionIncomparable);

    /// <summary>
    /// The control for the seeding behaviour: an absent record must NOT read as permission. Without
    /// this, a seeding bug and a working gate look identical.
    /// </summary>
    [Test]
    [Arguments(null)]
    [Arguments("")]
    [Arguments("   ")]
    public async Task NoMinimumOnRecord_IsRefused(string? minimum) =>
        await Assert.That(KiroReviewerCapability.Decide(Posix, true, "2.16.0", minimum))
            .IsEqualTo(KiroReviewerDecision.VersionNoMinimum);

    [Test]
    [Arguments(null)]
    [Arguments("")]
    [Arguments("   ")]
    public async Task AnUnresolvedVersion_IsRefused(string? installed) =>
        await Assert.That(KiroReviewerCapability.Decide(Posix, true, installed, "2.16.0"))
            .IsEqualTo(KiroReviewerDecision.VersionUnresolved);

    /// <summary>Surrounding whitespace is not a version change.</summary>
    [Test]
    public async Task VersionComparisonIgnoresSurroundingWhitespace() =>
        await Assert.That(KiroReviewerCapability.Decide(Posix, true, " 2.16.0\n", "2.16.0"))
            .IsEqualTo(KiroReviewerDecision.Allowed);

    [Test]
    public async Task TheBelowMinimumReason_NamesBothVersionsAndTheFix() {
        var reason = KiroReviewerCapability.DenialReason(
            KiroReviewerDecision.VersionBelowMinimum, "2.15.0", "2.16.0");

        await Assert.That(reason).StartsWith("kiro_reviewer_version_below_minimum");
        await Assert.That(reason).Contains("2.15.0");
        await Assert.That(reason).Contains("2.16.0");
        await Assert.That(reason).Contains("kcap daemon reviewer affirm");
    }

    /// <summary>
    /// The consent text IS the acceptance artifact for the accepted risk, so its content is the
    /// assertion. A refusal that merely names a flag would let an operator enable this without ever
    /// being told what they are accepting.
    /// </summary>
    [Test]
    public async Task TheDisabledReason_StatesTheRiskAndTheTrustDomainCondition() {
        var reason = KiroReviewerCapability.DenialReason(KiroReviewerDecision.Disabled, null, null);

        await Assert.That(reason).StartsWith("kiro_unattended_reviewer_disabled");
        await Assert.That(reason).Contains("every file this daemon user can read");
        await Assert.That(reason).Contains("return what it read to whoever requested the review");
        await Assert.That(reason).Contains("one trust domain");
        await Assert.That(reason).Contains("KCAP_KIRO_UNATTENDED_REVIEWER");
    }

    /// <summary>
    /// Enabling a reviewer is a security consent event, so only an explicit affirmative counts —
    /// a typo, a blank, or an unrecognised value must not be read as consent.
    /// </summary>
    [Test]
    [Arguments("1", true)]
    [Arguments("true", true)]
    [Arguments("TRUE", true)]
    [Arguments("yes", true)]
    [Arguments("on", true)]
    [Arguments("0", false)]
    [Arguments("false", false)]
    [Arguments("", false)]
    [Arguments("   ", false)]
    [Arguments("ture", false)]
    [Arguments("enabled", false)]
    [Arguments(null, false)]
    public async Task TheConsentFlagOnlyAcceptsAnExplicitAffirmative(string? value, bool expected) =>
        await Assert.That(DaemonRunner.ParseConsentFlag(value)).IsEqualTo(expected);

    /// <summary>
    /// The Windows arm, now assertable from any host. It refuses BEFORE the operator flag and before
    /// any version comparison — a fully-consented, correctly-affirmed daemon is still refused, because
    /// the reviewer home holds review context and cannot be created owner-only there.
    /// </summary>
    [Test]
    public async Task AWindowsHost_IsRefusedEvenWhenEnabledAndAffirmed() =>
        await Assert.That(KiroReviewerCapability.Decide(
                posixHost: false, operatorEnabled: true,
                installedVersion: "2.16.0", minimumVersion: "2.16.0"))
            .IsEqualTo(KiroReviewerDecision.UnsupportedPlatform);

    [Test]
    public async Task TheUnsupportedPlatformReason_SaysWhyRatherThanJustRefusing() {
        var reason = KiroReviewerCapability.DenialReason(
            KiroReviewerDecision.UnsupportedPlatform, null, null);

        await Assert.That(reason).StartsWith("kiro_reviewer_unsupported_platform");
        await Assert.That(reason).Contains("owner-only");
    }
}
