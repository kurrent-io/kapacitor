using Capacitor.Cli.Daemon.Acp;

namespace Capacitor.Cli.Tests.Unit.Acp;

/// <summary>
/// The OpenCode reviewer gate is fail-closed on three axes: platform, the operator's consent flag, and
/// whether the installed opencode meets the MINIMUM version this daemon recorded.
/// </summary>
public class OpenCodeReviewerCapabilityTests {
    /// <summary>Pinned, never read from the running host: these arms are about consent and versions, and
    /// letting the CI leg decide the platform would make every one of them fail on Windows for a reason
    /// unrelated to what they assert — the trap the Kiro gate records having fallen into.</summary>
    const bool Posix = true;

    [Test]
    public async Task EnabledAndAffirmed_IsTheOnlyPermittedCombination() =>
        await Assert.That(OpenCodeReviewerCapability.Decide(Posix, true, "1.18.9", "1.18.9"))
            .IsEqualTo(OpenCodeReviewerDecision.Allowed);

    [Test]
    public async Task DisabledByTheOperator_IsRefusedEvenOnAnAffirmedVersion() =>
        await Assert.That(OpenCodeReviewerCapability.Decide(Posix, false, "1.18.9", "1.18.9"))
            .IsEqualTo(OpenCodeReviewerDecision.Disabled);

    /// <summary>Windows cannot make the config dir owner-only, and its EMPTINESS is the containment —
    /// a directory another local user can write is one they can seed an MCP server into.</summary>
    [Test]
    public async Task Windows_IsRefusedBeforeConsentIsEvenConsulted() =>
        await Assert.That(OpenCodeReviewerCapability.Decide(posixHost: false, true, "1.18.9", "1.18.9"))
            .IsEqualTo(OpenCodeReviewerDecision.UnsupportedPlatform);

    /// <summary>The record is a FLOOR, not an affirmation of one build: refusing every vendor patch
    /// release is a treadmill with no safety payoff. If this ever reverts to an equality compare, these
    /// arguments are what catch it.</summary>
    [Test]
    [Arguments("1.18.10")]
    [Arguments("1.19.0")]
    [Arguments("2.0.0")]
    public async Task ANewerVersion_IsAdmittedWithNoOperatorAction(string installed) =>
        await Assert.That(OpenCodeReviewerCapability.Decide(Posix, true, installed, "1.18.9"))
            .IsEqualTo(OpenCodeReviewerDecision.Allowed);

    /// <summary>The other half of "minimum": older than the record is still refused, because the
    /// containment depends on build behaviour.</summary>
    [Test]
    [Arguments("1.18.8")]
    [Arguments("1.0.0")]
    public async Task AnOlderVersion_IsRefused(string installed) =>
        await Assert.That(OpenCodeReviewerCapability.Decide(Posix, true, installed, "1.18.9"))
            .IsEqualTo(OpenCodeReviewerDecision.VersionBelowMinimum);

    [Test]
    public async Task AnUnresolvedInstalledVersion_IsRefused() =>
        await Assert.That(OpenCodeReviewerCapability.Decide(Posix, true, null, "1.18.9"))
            .IsEqualTo(OpenCodeReviewerDecision.VersionUnresolved);

    [Test]
    public async Task NoRecordedMinimum_IsRefused() =>
        await Assert.That(OpenCodeReviewerCapability.Decide(Posix, true, "1.18.9", null))
            .IsEqualTo(OpenCodeReviewerDecision.VersionNoMinimum);

    [Test]
    public async Task IncomparableVersions_AreRefused() =>
        await Assert.That(OpenCodeReviewerCapability.Decide(Posix, true, "nightly", "1.18.9"))
            .IsEqualTo(OpenCodeReviewerDecision.VersionIncomparable);

    /// <summary>
    /// The consent text is the acceptance artifact for this gate's actual risk, so its CONTENT is
    /// asserted rather than merely its presence. The narrow tool surface makes it tempting to describe
    /// this reviewer as contained; the unbounded READ is the thing an operator is consenting to and the
    /// text has to say so.
    /// </summary>
    [Test]
    public async Task TheDisabledReason_NamesTheUnboundedReadAndTheVariableThatEnablesIt() {
        var reason = OpenCodeReviewerCapability.DenialReason(
            OpenCodeReviewerDecision.Disabled, "1.18.9", "1.18.9");

        await Assert.That(reason).StartsWith("opencode_unattended_reviewer_disabled");
        await Assert.That(reason).Contains("NOT path-scoped");
        await Assert.That(reason).Contains("every file this daemon user can read");
        await Assert.That(reason).Contains("KCAP_OPENCODE_UNATTENDED_REVIEWER=1");
        // The distinction that makes the consent decision comprehensible rather than alarming.
        await Assert.That(reason).Contains("no shell and no write");
    }

    /// <summary>Asking for the denial reason of a PERMITTED decision is a caller bug, not a blank
    /// string — a discard arm here would have printed the below-minimum text for whatever arm is added
    /// next, sending an operator to the wrong fix.</summary>
    [Test]
    public async Task Allowed_HasNoDenialReason() =>
        await Assert.That(() => OpenCodeReviewerCapability.DenialReason(
                OpenCodeReviewerDecision.Allowed, "1.18.9", "1.18.9"))
            .Throws<ArgumentOutOfRangeException>();

    /// <summary>Every refusal must be actionable: a coded prefix plus the command or variable that
    /// clears it. Enumerated over the real enum so an arm added later without a reason goes red.</summary>
    [Test]
    public async Task EveryRefusal_CarriesACodedPrefix() {
        foreach (var decision in Enum.GetValues<OpenCodeReviewerDecision>()) {
            if (decision == OpenCodeReviewerDecision.Allowed) continue;

            var reason = OpenCodeReviewerCapability.DenialReason(decision, "1.18.9", "1.18.9");

            await Assert.That(reason).StartsWith("opencode_");
            await Assert.That(reason).Contains(":");
        }
    }
}
