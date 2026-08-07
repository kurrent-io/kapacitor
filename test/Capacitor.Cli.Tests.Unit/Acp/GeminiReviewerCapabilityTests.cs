using Capacitor.Cli.Core;
using Capacitor.Cli.Daemon.Acp;

namespace Capacitor.Cli.Tests.Unit.Acp;

/// <summary>
/// The capability gate is the daemon OPERATOR's consent, and it is fail-closed on every axis: the operator
/// flag, and the build affirmation that stops an enabled flag carrying that consent across a vendor upgrade
/// which could invalidate the MCP-allowlist mechanism the reviewer's containment rests on.
/// </summary>
public class GeminiReviewerCapabilityTests {
    const string Installed = "0.54.0";

    [Test]
    public async Task EnabledAndAffirmed_IsTheOnlyPermittedCombination() {
        await Assert.That(GeminiReviewerCapability.Decide(true, Installed, Installed))
            .IsEqualTo(GeminiReviewerDecision.Allowed);
    }

    [Test]
    public async Task DisabledByTheOperator_IsRefusedEvenOnAnAffirmedBuild() {
        var decision = GeminiReviewerCapability.Decide(false, Installed, Installed);

        await Assert.That(decision).IsEqualTo(GeminiReviewerDecision.Disabled);
        await Assert.That(GeminiReviewerCapability.DenialReason(decision, Installed, Installed))
            .Contains("gemini_unattended_reviewer_disabled");
    }

    /// <summary>An unresolvable version is UNKNOWN, and unknown is denied — not assumed compatible.</summary>
    [Test]
    [Arguments(null)]
    [Arguments("")]
    [Arguments("   ")]
    public async Task AnUnresolvedVersion_IsRefused(string? installed) {
        await Assert.That(GeminiReviewerCapability.Decide(true, installed, Installed))
            .IsEqualTo(GeminiReviewerDecision.VersionUnresolved);
    }

    /// <summary>No minimum recorded is not the same as meeting one: a daemon that has never recorded a
    /// build must refuse rather than accept whatever is installed.</summary>
    [Test]
    [Arguments(null)]
    [Arguments("")]
    [Arguments("   ")]
    public async Task NoMinimumRecorded_IsRefused(string? minimum) {
        await Assert.That(GeminiReviewerCapability.Decide(true, Installed, minimum))
            .IsEqualTo(GeminiReviewerDecision.VersionNoMinimum);
    }

    /// <summary>
    /// The direction that matters, and it is the inverse of what this gate used to do: a NEWER build is
    /// admitted with no operator action. Refusing every vendor release was the treadmill the
    /// certified-set model was abandoned for, and exact-match only relocated it onto the operator.
    /// </summary>
    [Test]
    [Arguments("0.55.0")]
    [Arguments("0.54.1")]
    [Arguments("1.0.0")]
    public async Task ANewerBuildThanTheMinimum_IsAdmitted(string installed) {
        await Assert.That(GeminiReviewerCapability.Decide(true, installed, Installed))
            .IsEqualTo(GeminiReviewerDecision.Allowed);
    }

    /// <summary>…while an older build than the recorded minimum is still refused, and the denial names
    /// both versions or an operator cannot tell what to do about it.</summary>
    [Test]
    [Arguments("0.53.0")]
    [Arguments("0.1.0")]
    public async Task AnOlderBuildThanTheMinimum_IsRefused(string installed) {
        var decision = GeminiReviewerCapability.Decide(true, installed, Installed);

        await Assert.That(decision).IsEqualTo(GeminiReviewerDecision.VersionBelowMinimum);

        var reason = GeminiReviewerCapability.DenialReason(decision, installed, Installed);

        await Assert.That(reason).Contains("version_below_minimum");
        await Assert.That(reason).Contains(installed);
        await Assert.That(reason).Contains(Installed);
        await Assert.That(reason).Contains("kcap daemon reviewer affirm --vendor gemini");
    }

    [Test]
    public async Task SurroundingWhitespaceIsToleratedOnBothSides() {
        await Assert.That(GeminiReviewerCapability.Decide(true, $"  {Installed}\n", $"{Installed} "))
            .IsEqualTo(GeminiReviewerDecision.Allowed);
        // A `v` prefix is decoration the shared parse strips, not a different build — under the old
        // ordinal-equality rule this exact pair was refused.
        await Assert.That(GeminiReviewerCapability.Decide(true, $"v{Installed}", Installed))
            .IsEqualTo(GeminiReviewerDecision.Allowed);
    }

    /// <summary>The denial reason must name the actual cause, or an operator cannot act on it.</summary>
    [Test]
    public async Task TheDenialReasonDistinguishesEveryRefusal() {
        await Assert.That(GeminiReviewerCapability.DenialReason(GeminiReviewerDecision.Disabled, null, null))
            .Contains("disabled");
        await Assert.That(
                GeminiReviewerCapability.DenialReason(GeminiReviewerDecision.VersionUnresolved, null, Installed))
            .Contains("version_unresolved");
        await Assert.That(
                GeminiReviewerCapability.DenialReason(GeminiReviewerDecision.VersionBelowMinimum, Installed, null))
            .Contains("version_below_minimum");
        await Assert.That(
                GeminiReviewerCapability.DenialReason(GeminiReviewerDecision.VersionNoMinimum, Installed, null))
            .Contains("version_no_minimum");
        await Assert.That(
                GeminiReviewerCapability.DenialReason(
                    GeminiReviewerDecision.VersionIncomparable, "1.2.3.4.5", Installed))
            .Contains("version_incomparable");
    }

    /// <summary>
    /// The consent text is the acceptance artifact for what enabling this grants, so its content is
    /// asserted rather than just its presence — and it must name the variable that actually turns it on.
    /// </summary>
    [Test]
    public async Task TheDisabledReasonStatesWhatEnablingGrants_AndHowToEnableIt() {
        var reason = GeminiReviewerCapability.DenialReason(GeminiReviewerDecision.Disabled, null, null);

        await Assert.That(reason).Contains("code execution");
        await Assert.That(reason).Contains("credentials");
        await Assert.That(reason).Contains("KCAP_GEMINI_UNATTENDED_REVIEWER=1");
    }

    // ── version extraction, which the affirmation check depends on ──
    //
    // Review's point: requiring the whole trimmed output to BE a version is brittle. Measured, gemini 0.53.0
    // prints it to stdout and stderr, but a build that added a banner or an "update available" notice would
    // make the gate fail closed and silently disable the reviewer.

    [Test]
    [Arguments("0.53.0", "0.53.0")]
    [Arguments("0.53.0\n", "0.53.0")]
    [Arguments("  0.53.0  ", "0.53.0")]
    [Arguments("v0.53.0", "0.53.0")]
    [Arguments("gemini 0.53.0", "0.53.0")]
    [Arguments("Update available!\n0.53.0\n", "0.53.0")]
    [Arguments("0.53.0 (build abc)", "0.53.0")]
    public async Task AVersionTokenIsExtractedFromNoisyOutput(string output, string expected) {
        await Assert.That(VendorVersionResolver.ExtractVersionToken(output)).IsEqualTo(expected);
    }

    /// <summary>Anything not recognisably a version must read as UNKNOWN — which denies — rather than as a
    /// near-miss string that could be compared against the affirmed build.</summary>
    [Test]
    [Arguments(null)]
    [Arguments("")]
    [Arguments("   ")]
    [Arguments("no version here")]
    [Arguments("abc")]
    [Arguments("53")]
    public async Task NonVersionOutputExtractsToNull(string? output) {
        await Assert.That(VendorVersionResolver.ExtractVersionToken(output)).IsNull();
    }

    /// <summary>End to end: noisy output still gates correctly, which is the property that matters.</summary>
    [Test]
    public async Task ABannerBeforeTheVersion_StillPermitsAnAffirmedBuild() {
        var extracted = VendorVersionResolver.ExtractVersionToken(
            $"Update available: run npm i -g @google/gemini-cli\n{Installed}\n");

        await Assert.That(GeminiReviewerCapability.Decide(true, extracted, Installed))
            .IsEqualTo(GeminiReviewerDecision.Allowed);
    }

    /// <summary>
    /// The regression this model exists to prevent, now closed outright rather than made recoverable.
    ///
    /// <para>Historic case: the certified-set model made 0.54.0 unreachable against a certified 0.53.0
    /// until a maintainer shipped a new kcap. Exact-match affirmation removed the release coupling but
    /// still refused the upgrade until an operator re-affirmed. Under a minimum, the same pair needs
    /// <b>no action from anyone</b> — which is the whole point of the change.</para>
    /// </summary>
    [Test]
    public async Task TheOnePatchAheadUpgrade_NeedsNoActionAtAll() {
        await Assert.That(GeminiReviewerCapability.Decide(true, "0.54.0", "0.53.0"))
            .IsEqualTo(GeminiReviewerDecision.Allowed)
            .Because("a vendor patch release must not take the reviewer offline");
    }
}
