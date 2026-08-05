using Capacitor.Cli.Daemon;
using Capacitor.Cli.Daemon.Acp;

namespace Capacitor.Cli.Tests.Unit.Acp;

/// <summary>
/// The Kiro reviewer gate is fail-closed on three axes: platform, the operator's consent flag, and
/// whether the installed kiro-cli is the build this daemon affirmed.
/// </summary>
public class KiroReviewerCapabilityTests {
    [Test]
    public async Task EnabledAndAffirmed_IsTheOnlyPermittedCombination() =>
        await Assert.That(KiroReviewerCapability.Decide(true, "2.16.0", "2.16.0"))
            .IsEqualTo(KiroReviewerDecision.Allowed);

    [Test]
    public async Task DisabledByTheOperator_IsRefusedEvenOnAnAffirmedVersion() =>
        await Assert.That(KiroReviewerCapability.Decide(false, "2.16.0", "2.16.0"))
            .IsEqualTo(KiroReviewerDecision.Disabled);

    /// <summary>
    /// The direction that matters: a NEWER build is refused, not accepted. The record is an
    /// affirmation of a specific build, not a floor — a later build may have changed how it treats
    /// KIRO_HOME, which is the whole hazard.
    /// </summary>
    [Test]
    [Arguments("2.17.0")]
    [Arguments("2.16.1")]
    [Arguments("3.0.0")]
    [Arguments("2.15.2")]
    public async Task AChangedVersion_IsRefusedInBothDirections(string installed) =>
        await Assert.That(KiroReviewerCapability.Decide(true, installed, "2.16.0"))
            .IsEqualTo(KiroReviewerDecision.VersionUnaffirmed);

    /// <summary>
    /// The control for the seeding behaviour: an absent record must NOT read as permission. Without
    /// this, a seeding bug and a working gate look identical.
    /// </summary>
    [Test]
    [Arguments(null)]
    [Arguments("")]
    [Arguments("   ")]
    public async Task NoAffirmationOnRecord_IsRefused(string? affirmed) =>
        await Assert.That(KiroReviewerCapability.Decide(true, "2.16.0", affirmed))
            .IsEqualTo(KiroReviewerDecision.VersionUnaffirmed);

    [Test]
    [Arguments(null)]
    [Arguments("")]
    [Arguments("   ")]
    public async Task AnUnresolvedVersion_IsRefused(string? installed) =>
        await Assert.That(KiroReviewerCapability.Decide(true, installed, "2.16.0"))
            .IsEqualTo(KiroReviewerDecision.VersionUnresolved);

    /// <summary>Surrounding whitespace is not a version change.</summary>
    [Test]
    public async Task VersionComparisonIgnoresSurroundingWhitespace() =>
        await Assert.That(KiroReviewerCapability.Decide(true, " 2.16.0\n", "2.16.0"))
            .IsEqualTo(KiroReviewerDecision.Allowed);

    [Test]
    public async Task TheUnaffirmedReason_NamesBothVersionsAndTheFix() {
        var reason = KiroReviewerCapability.DenialReason(
            KiroReviewerDecision.VersionUnaffirmed, "2.17.0", "2.16.0");

        await Assert.That(reason).StartsWith("kiro_reviewer_version_unaffirmed");
        await Assert.That(reason).Contains("2.17.0");
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
        await Assert.That(reason).Contains("KiroUnattendedReviewerEnabled");
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
}
