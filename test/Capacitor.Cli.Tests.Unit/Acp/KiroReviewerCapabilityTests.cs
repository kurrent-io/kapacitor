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
    /// This arm is now reached only when an operator EXPLICITLY disabled the reviewer, so the text has a
    /// different job than it used to: tell them how to undo it, and — because the obvious next question
    /// is "was I protecting myself with that?" — say plainly that the switch does not narrow anything a
    /// requester cannot route around by naming an ungated vendor.
    /// </summary>
    [Test]
    public async Task TheDisabledReason_SaysHowToUndoItAndThatItGuardsLittle() {
        var reason = KiroReviewerCapability.DenialReason(KiroReviewerDecision.Disabled, null, null);

        await Assert.That(reason).StartsWith("kiro_unattended_reviewer_disabled");
        await Assert.That(reason).Contains("EXPLICITLY disabled");
        await Assert.That(reason).Contains("KCAP_KIRO_UNATTENDED_REVIEWER");
        // The honest caveat: gating one vendor while four run ungated with more capability stops nobody.
        await Assert.That(reason).Contains("never gated");
    }

    /// <summary>
    /// The switch is an opt-OUT: unset means ENABLED, matching the never-gated
    /// Claude/Codex/Cursor/Copilot reviewers.
    ///
    /// <para>An UNRECOGNISED value resolves to DISABLED, not to the default. That looks contradictory
    /// until you notice that setting the variable at all is only ever an attempt to turn the reviewer
    /// OFF — enabling requires no variable — so an unreadable value is a failed "off", and honouring the
    /// evident intent means failing closed. Review corrected an earlier revision that failed open here
    /// by borrowing the general "a typo must not take a feature offline" rule, which does not transfer
    /// to a setting with only one use.</para>
    /// </summary>
    [Test]
    [Arguments("1", true)]
    [Arguments("true", true)]
    [Arguments("TRUE", true)]
    [Arguments("yes", true)]
    [Arguments("on", true)]
    [Arguments("0", false)]
    [Arguments("false", false)]
    [Arguments("FALSE", false)]
    [Arguments("no", false)]
    [Arguments("off", false)]
    [Arguments("", true)]
    [Arguments("   ", true)]
    [Arguments(null, true)]
    // Set-but-unrecognised is DISABLED, not enabled. Since unset already enables, the only reason to set
    // this variable is to turn the reviewer off — so an unreadable value is a failed "off", and honouring
    // that intent means failing closed. Review corrected an earlier revision that failed open here.
    [Arguments("ture", false)]
    [Arguments("enabled", false)]
    [Arguments("2", false)]
    public async Task TheConsentFlagIsAnOptOut(string? value, bool expected) =>
        await Assert.That(DaemonRunner.ParseConsentFlag(value)).IsEqualTo(expected);

    /// <summary>
    /// The asymmetry spelled out, because "unset enables but garbage disables" reads contradictory until
    /// you notice that setting the variable at all is only ever an attempt to disable.
    /// </summary>
    [Test]
    public async Task UnsetEnables_ButAnUnreadableValueDisables() {
        await Assert.That(DaemonRunner.ParseConsentFlag(null)).IsTrue();
        await Assert.That(DaemonRunner.ParseConsentFlag("")).IsTrue();

        await Assert.That(DaemonRunner.ParseConsentFlag("flase")).IsFalse();
        await Assert.That(DaemonRunner.ParseConsentFlag("disabled")).IsFalse();

        // A typo'd ENABLE attempt lands on the pre-change behaviour, which is the safe side.
        await Assert.That(DaemonRunner.ParseConsentFlag("y")).IsFalse();
    }

    /// <summary>
    /// A value that is SET but unreadable must be reported. Without this, an operator who typed
    /// <c>flase</c> meaning to disable a reviewer gets it enabled with no signal at all — the one
    /// direction this default flip could surprise someone in.
    /// </summary>
    [Test]
    [Arguments("flase")]
    [Arguments("enabled")]
    [Arguments("nope")]
    public async Task AnUnparseableValue_IsReportedRatherThanSilentlyIgnored(string value) {
        var warning = DaemonRunner.DescribeUnparseableConsent("KCAP_KIRO_UNATTENDED_REVIEWER", value);

        await Assert.That(warning).IsNotNull();
        await Assert.That(warning!).Contains("KCAP_KIRO_UNATTENDED_REVIEWER");
        await Assert.That(warning).Contains(value);
        await Assert.That(warning).Contains("DISABLED");
        await Assert.That(warning).Contains("0/false/no/off");
        // The warning must state the outcome it actually produced, or it sends the operator the wrong way.
        await Assert.That(DaemonRunner.ParseConsentFlag(value)).IsFalse();
    }

    /// <summary>Nothing to report for a value the parse understands, or for the ordinary unset case —
    /// a warning on every boot for a correctly-configured daemon would train people to ignore it.</summary>
    [Test]
    [Arguments(null)]
    [Arguments("")]
    [Arguments("1")]
    [Arguments("0")]
    [Arguments("false")]
    [Arguments("ON")]
    public async Task ARecognisedOrAbsentValue_IsNotReported(string? value) =>
        await Assert.That(DaemonRunner.DescribeUnparseableConsent("KCAP_KIRO_UNATTENDED_REVIEWER", value))
            .IsNull();

    /// <summary>
    /// A quoting artifact must not turn a DISABLE into an enable. The realistic sources are a mis-quoted
    /// service-unit entry, a <c>.cmd</c> wrapper's <c>set "K=V"</c>, and a hand-edited plist — all of
    /// which deliver the value with its quotes attached. Since disabling is the operator's only lever,
    /// failing open there is the mistake worth preventing outright. Raised by review.
    /// </summary>
    [Test]
    [Arguments("\"0\"")]
    [Arguments("'0'")]
    [Arguments("\"false\"")]
    [Arguments("  \"off\"  ")]
    [Arguments("'no'")]
    public async Task AQuotedFalseyValue_StillDisables(string value) {
        await Assert.That(DaemonRunner.ParseConsentFlag(value)).IsFalse();
        // ...and does not also warn, since it was understood.
        await Assert.That(DaemonRunner.DescribeUnparseableConsent("KCAP_KIRO_UNATTENDED_REVIEWER", value))
            .IsNull();
    }

    /// <summary>Only ONE level, and only MATCHED quotes: anything still unrecognised afterwards is
    /// reported rather than stripped again, so this cannot quietly accept arbitrary shapes.</summary>
    [Test]
    [Arguments("\"\"0\"\"")]
    [Arguments("\"0")]
    [Arguments("0\"")]
    [Arguments("\"0'")]
    public async Task OnlyOneLevelOfMatchedQuotesIsStripped(string value) {
        // Unrecognised, therefore disabled AND reported. Note these shapes all arise from a botched
        // DISABLE attempt, so failing closed happens to land on what the operator wanted — the quote
        // stripping is what makes that the RIGHT answer rather than an accidentally-right one.
        await Assert.That(DaemonRunner.ParseConsentFlag(value)).IsFalse();
        await Assert.That(DaemonRunner.DescribeUnparseableConsent("KCAP_KIRO_UNATTENDED_REVIEWER", value))
            .IsNotNull();
    }

    /// <summary>
    /// The structural guarantee that replaced a by-inspection one: the parse and the warning are derived
    /// from a single recogniser, so they cannot disagree about which values are understood. A
    /// disagreement would either warn on every boot for a correct config, or swallow a typo silently.
    /// </summary>
    [Test]
    [Arguments("1")] [Arguments("0")] [Arguments("true")] [Arguments("false")]
    [Arguments("yes")] [Arguments("no")] [Arguments("on")] [Arguments("off")]
    [Arguments("YES")] [Arguments("Off")] [Arguments("\"1\"")]
    [Arguments("flase")] [Arguments("enabled")] [Arguments("2")] [Arguments("-")]
    [Arguments(null)] [Arguments("")] [Arguments("   ")]
    public async Task TheParseAndTheWarningShareOneValueSet(string? value) {
        var recognised = DaemonRunner.RecogniseConsent(value) is not null;
        var warned     = DaemonRunner.DescribeUnparseableConsent("VAR", value) is not null;

        await Assert.That(warned).IsEqualTo(!recognised);
        // And the parse never contradicts the recogniser on a value it DOES understand.
        if (DaemonRunner.RecogniseConsent(value) is { } known)
            await Assert.That(DaemonRunner.ParseConsentFlag(value)).IsEqualTo(known);
    }

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
