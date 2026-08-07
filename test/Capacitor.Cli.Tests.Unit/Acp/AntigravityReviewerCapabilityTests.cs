using Capacitor.Cli.Daemon;
using Capacitor.Cli.Daemon.Acp;

namespace Capacitor.Cli.Tests.Unit.Acp;

/// <summary>
/// The Antigravity reviewer gate is fail-closed on three axes: the operator's consent flag, the
/// platform, and whether the installed <c>agy</c> meets the MINIMUM version this daemon recorded.
///
/// <para>The version half is the SHARED mechanism Kiro and Gemini use — a daemon-owned record, moved
/// only by <c>kcap daemon reviewer affirm</c>. It is a minimum, not an exact match, so an <c>agy</c>
/// that updates itself needs no operator action; a build found to be bad is excluded by raising the
/// floor past it. What stays Antigravity's own is the consent flag, the POSIX-only refusal, and the
/// binary-missing arm the factory adds around this decision.</para>
/// </summary>
public class AntigravityReviewerCapabilityTests {
    /// <summary>Pinned, never read from the running host: these arms are about consent and versions,
    /// and letting the CI leg decide the platform makes every one of them fail on Windows for a reason
    /// unrelated to what it asserts.</summary>
    const bool Posix = true;

    const string Minimum = "1.1.10";

    // ── the arms ──────────────────────────────────────────────────────────────────────────────────

    [Test]
    public async Task ConsentPlusAMinimumMeetingBuild_IsTheOnlyPermittedCombination() =>
        await Assert.That(AntigravityReviewerCapability.Decide(Posix, true, "1.1.10", Minimum))
            .IsEqualTo(AntigravityReviewerDecision.Allowed);

    /// <summary>Consent is read FIRST and short-circuits: an installed-but-wedged <c>agy</c> must not
    /// be probed — let alone hang a daemon start — for a feature the operator switched off. The
    /// below-minimum argument is what makes this a short-circuit assertion rather than a restatement
    /// of the disabled arm.</summary>
    [Test]
    [Arguments("1.1.10")]
    [Arguments("0.0.1")]
    [Arguments(null)]
    public async Task DisabledByTheOperator_IsRefusedWhateverTheVersionSays(string? installed) =>
        await Assert.That(AntigravityReviewerCapability.Decide(Posix, false, installed, Minimum))
            .IsEqualTo(AntigravityReviewerDecision.Disabled);

    /// <summary>The Windows arm, assertable from any host because the platform is a parameter. The
    /// per-launch home holds the reviewer's own transcript — and so the review context — and cannot be
    /// created owner-only there.</summary>
    [Test]
    public async Task AWindowsHost_IsRefusedEvenWhenConsentedAndCurrent() =>
        await Assert.That(AntigravityReviewerCapability.Decide(
                posixHost: false, operatorEnabled: true, installedVersion: "1.1.10", minimumVersion: Minimum))
            .IsEqualTo(AntigravityReviewerDecision.UnsupportedPlatform);

    // ── the minimum ───────────────────────────────────────────────────────────────────────────────

    /// <summary>The regression guard for the owner's decision: a NEWER build is allowed, not refused.
    /// An exact-match compare reintroduced here would fail this.</summary>
    [Test]
    [Arguments("1.1.11")]
    [Arguments("1.2.0")]
    [Arguments("2.0.0")]
    [Arguments("1.1.10.1")]
    public async Task ANewerVersion_IsAdmittedWithNoOperatorAction(string installed) =>
        await Assert.That(AntigravityReviewerCapability.Decide(Posix, true, installed, Minimum))
            .IsEqualTo(AntigravityReviewerDecision.Allowed);

    /// <summary>A floor, not a bar to clear: <c>&gt;=</c>, never <c>&gt;</c>.</summary>
    [Test]
    public async Task ExactlyTheMinimum_IsAllowed() =>
        await Assert.That(AntigravityReviewerCapability.Decide(Posix, true, Minimum, Minimum))
            .IsEqualTo(AntigravityReviewerDecision.Allowed);

    [Test]
    [Arguments("1.1.9")]
    [Arguments("1.0.0")]
    [Arguments("0.9.9")]
    public async Task AnOlderVersion_IsRefused(string installed) =>
        await Assert.That(AntigravityReviewerCapability.Decide(Posix, true, installed, Minimum))
            .IsEqualTo(AntigravityReviewerDecision.VersionBelowMinimum);

    /// <summary>A build we cannot identify has not been SHOWN to meet the minimum, so it is refused —
    /// and refused under its own arm, because the operator action differs from an old build's.</summary>
    [Test]
    [Arguments(null)]
    [Arguments("")]
    [Arguments("   ")]
    public async Task AnUnresolvedVersion_IsRefused(string? installed) =>
        await Assert.That(AntigravityReviewerCapability.Decide(Posix, true, installed, Minimum))
            .IsEqualTo(AntigravityReviewerDecision.VersionUnresolved);

    /// <summary>
    /// The control for the seeding behaviour: an absent record must NOT read as permission. Without
    /// this, a seeding bug and a working gate look identical.
    /// </summary>
    [Test]
    [Arguments(null)]
    [Arguments("")]
    [Arguments("   ")]
    public async Task NoMinimumOnRecord_IsRefused(string? minimum) =>
        await Assert.That(AntigravityReviewerCapability.Decide(Posix, true, "1.1.10", minimum))
            .IsEqualTo(AntigravityReviewerDecision.VersionNoMinimum);

    /// <summary>An unorderable pair must reach its OWN arm, never the below-minimum one — refusing an
    /// upgrade while calling it "too old" is the failure this arm exists to prevent.</summary>
    [Test]
    [Arguments("2.0.0", "1.2.3.4.5")]
    [Arguments("1.2.3.4.5", "2.0.0")]
    [Arguments("banana", "1.1.10")]
    public async Task AnUnorderablePair_IsIncomparableNotBelowMinimum(string installed, string minimum) =>
        await Assert.That(AntigravityReviewerCapability.Decide(Posix, true, installed, minimum))
            .IsEqualTo(AntigravityReviewerDecision.VersionIncomparable);

    /// <summary>A vendor prerelease suffix is not an unidentifiable build — the shared comparison
    /// strips it, and refusing here would take the reviewer offline on a build that meets the
    /// minimum.</summary>
    [Test]
    [Arguments("1.1.10-beta.1")]
    [Arguments("1.2.0+build7")]
    public async Task APrereleaseOrBuildSuffixStillMeetsTheMinimum(string installed) =>
        await Assert.That(AntigravityReviewerCapability.Decide(Posix, true, installed, Minimum))
            .IsEqualTo(AntigravityReviewerDecision.Allowed);

    /// <summary>Surrounding whitespace is not a version change.</summary>
    [Test]
    public async Task VersionComparisonIgnoresSurroundingWhitespace() =>
        await Assert.That(AntigravityReviewerCapability.Decide(Posix, true, " 1.1.10\n", Minimum))
            .IsEqualTo(AntigravityReviewerDecision.Allowed);

    // ── the denial reasons ────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// The consent text is what an operator reads before turning this on, so its content is asserted
    /// rather than its presence.
    /// </summary>
    [Test]
    public async Task TheDisabledReason_NamesTheSwitchAndSaysWhereItGoes() {
        var reason = Reason(AntigravityReviewerDecision.Disabled, null, null);

        await Assert.That(reason).StartsWith("antigravity_unattended_reviewer_disabled");
        await Assert.That(reason).Contains("KCAP_ANTIGRAVITY_UNATTENDED_REVIEWER");
        await Assert.That(reason).Contains("daemon's environment");
    }

    /// <summary>The platform refusal says WHY rather than merely refusing.</summary>
    [Test]
    public async Task TheUnsupportedPlatformReason_SaysWhyRatherThanJustRefusing() {
        var reason = Reason(AntigravityReviewerDecision.UnsupportedPlatform, null, null);

        await Assert.That(reason).StartsWith("antigravity_reviewer_unsupported_platform");
        await Assert.That(reason).Contains("owner-only");
    }

    [Test]
    public async Task TheBelowMinimumReason_NamesBothVersionsAndTheFix() {
        var reason = Reason(AntigravityReviewerDecision.VersionBelowMinimum, "1.1.8", Minimum);

        await Assert.That(reason).StartsWith("antigravity_reviewer_version_below_minimum");
        await Assert.That(reason).Contains("1.1.8");
        await Assert.That(reason).Contains(Minimum);
        await Assert.That(reason).Contains("kcap daemon reviewer affirm --vendor antigravity");
    }

    /// <summary>An operator whose <c>agy --version</c> stopped parsing needs a different action from
    /// one running an old build, which is the whole reason these are separate arms. It names the
    /// binary the DAEMON would launch, not whatever is first on PATH.</summary>
    [Test]
    public async Task TheUnresolvedReason_SendsTheOperatorToTheBinaryRatherThanToAnUpgrade() {
        var reason = Reason(AntigravityReviewerDecision.VersionUnresolved, null, Minimum);

        await Assert.That(reason).StartsWith("antigravity_reviewer_version_unresolved");
        await Assert.That(reason).Contains("--version");
        await Assert.That(reason).Contains("KCAP_ANTIGRAVITY_PATH");
        await Assert.That(reason).Contains("agy");
    }

    /// <summary>The most common misconfiguration — enabling the reviewer against an already-running
    /// daemon, which seeds its record at startup — so the remedy names BOTH the restart and the verb
    /// that avoids one.</summary>
    [Test]
    public async Task TheNoMinimumReason_SendsTheOperatorToARestartOrTheAffirmVerb() {
        var reason = Reason(AntigravityReviewerDecision.VersionNoMinimum, "1.1.10", null);

        await Assert.That(reason).StartsWith("antigravity_reviewer_version_no_minimum");
        await Assert.That(reason).Contains("KCAP_ANTIGRAVITY_UNATTENDED_REVIEWER");
        await Assert.That(reason).Contains("kcap daemon reviewer affirm --vendor antigravity");
    }

    [Test]
    public async Task TheIncomparableReason_SaysNeitherIsNewerRatherThanCallingTheBuildOld() {
        var reason = Reason(AntigravityReviewerDecision.VersionIncomparable, "1.2.3.4.5", Minimum);

        await Assert.That(reason).StartsWith("antigravity_reviewer_version_incomparable");
        await Assert.That(reason).Contains("1.2.3.4.5");
        await Assert.That(reason).Contains(Minimum);
        await Assert.That(reason).Contains("kcap daemon reviewer affirm --vendor antigravity");
    }

    /// <summary>Asking for the denial reason of a PERMITTED decision is a caller bug, not a text to
    /// invent — and a discard arm would have answered it with a neighbouring vendor's remedy.</summary>
    [Test]
    public async Task TheAllowedDecisionHasNoDenialReason() =>
        await Assert.That(() => Reason(AntigravityReviewerDecision.Allowed, "1.1.10", Minimum))
            .Throws<ArgumentOutOfRangeException>();

    /// <summary>No refusal may point an operator at a variable this vendor no longer reads. The floor
    /// used to be configuration; it is now a daemon-owned record moved by the affirm verb, and a text
    /// left behind would send an operator to a switch that does nothing.</summary>
    [Test]
    public async Task NoRefusalPointsAtTheRetiredConfigurationVariable() {
        foreach (var decision in Enum.GetValues<AntigravityReviewerDecision>()) {
            if (decision == AntigravityReviewerDecision.Allowed) continue;

            await Assert.That(Reason(decision, "1.1.8", Minimum))
                .DoesNotContain("KCAP_ANTIGRAVITY_MIN_CLI_VERSION")
                .Because($"{decision} must not send an operator to a variable this daemon no longer reads");
        }
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
    [Arguments(null, false)]
    public async Task TheConsentFlagOnlyAcceptsAnExplicitAffirmative(string? value, bool expected) =>
        await Assert.That(DaemonRunner.ParseConsentFlag(value)).IsEqualTo(expected);

    static string Reason(AntigravityReviewerDecision decision, string? installed, string? minimum) =>
        AntigravityReviewerCapability.DenialReason(decision, installed, minimum, binaryPath: "agy");
}
