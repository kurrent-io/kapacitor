using Capacitor.Cli.Daemon;
using Capacitor.Cli.Daemon.Acp;
using Capacitor.Cli.Daemon.Services;
using Microsoft.Extensions.Logging.Abstractions;

namespace Capacitor.Cli.Tests.Unit.Acp;

/// <summary>
/// The Antigravity reviewer gate is fail-closed on three axes: the operator's consent flag, the
/// platform, and whether the installed <c>agy</c> meets a minimum version FLOOR.
///
/// <para>The floor is where this vendor deliberately diverges from Kiro and Gemini, which require an
/// operator to affirm each installed build. <c>agy</c> was observed auto-updating itself mid-session
/// (1.1.8 → 1.1.10), so an affirmation gate would park the reviewer on a release cadence the operator
/// neither controls nor can predict. Meeting the floor is enough.</para>
/// </summary>
public class AntigravityReviewerCapabilityTests {
    /// <summary>Pinned, never read from the running host: these arms are about consent and versions,
    /// and letting the CI leg decide the platform makes every one of them fail on Windows for a reason
    /// unrelated to what it asserts.</summary>
    const bool Posix = true;

    const string Floor = "1.1.10";

    // ── the arms ──────────────────────────────────────────────────────────────────────────────────

    [Test]
    public async Task ConsentPlusAFloorMeetingBuild_IsTheOnlyPermittedCombination() =>
        await Assert.That(AntigravityReviewerCapability.Decide(Posix, true, "1.1.10", Floor))
            .IsEqualTo(AntigravityReviewerDecision.Allowed);

    /// <summary>Consent is read FIRST and short-circuits: an installed-but-wedged <c>agy</c> must not
    /// be probed — let alone hang a daemon start — for a feature the operator switched off. The
    /// below-floor argument is what makes this a short-circuit assertion rather than a restatement of
    /// the disabled arm.</summary>
    [Test]
    [Arguments("1.1.10")]
    [Arguments("0.0.1")]
    [Arguments(null)]
    public async Task DisabledByTheOperator_IsRefusedWhateverTheVersionSays(string? installed) =>
        await Assert.That(AntigravityReviewerCapability.Decide(Posix, false, installed, Floor))
            .IsEqualTo(AntigravityReviewerDecision.Disabled);

    /// <summary>The Windows arm, assertable from any host because the platform is a parameter. The
    /// per-launch home holds the reviewer's own transcript — and so the review context — and cannot be
    /// created owner-only there.</summary>
    [Test]
    public async Task AWindowsHost_IsRefusedEvenWhenConsentedAndCurrent() =>
        await Assert.That(AntigravityReviewerCapability.Decide(
                posixHost: false, operatorEnabled: true, installedVersion: "1.1.10", minimumVersion: Floor))
            .IsEqualTo(AntigravityReviewerDecision.UnsupportedPlatform);

    // ── the floor ─────────────────────────────────────────────────────────────────────────────────

    /// <summary>The regression guard for the owner's decision: a NEWER build is allowed, not refused.
    /// An exact-match or affirmation compare reintroduced here would fail this.</summary>
    [Test]
    [Arguments("1.1.11")]
    [Arguments("1.2.0")]
    [Arguments("2.0.0")]
    [Arguments("1.1.10.1")]
    public async Task AboveTheFloor_IsAllowed(string installed) =>
        await Assert.That(AntigravityReviewerCapability.Decide(Posix, true, installed, Floor))
            .IsEqualTo(AntigravityReviewerDecision.Allowed);

    /// <summary>A floor, not a bar to clear: <c>&gt;=</c>, never <c>&gt;</c>.</summary>
    [Test]
    public async Task ExactlyTheFloor_IsAllowed() =>
        await Assert.That(AntigravityReviewerCapability.Decide(Posix, true, Floor, Floor))
            .IsEqualTo(AntigravityReviewerDecision.Allowed);

    [Test]
    [Arguments("1.1.9")]
    [Arguments("1.0.0")]
    [Arguments("0.9.9")]
    public async Task BelowTheFloor_IsRefused(string installed) =>
        await Assert.That(AntigravityReviewerCapability.Decide(Posix, true, installed, Floor))
            .IsEqualTo(AntigravityReviewerDecision.VersionBelowMinimum);

    /// <summary>A build we cannot identify has not been SHOWN to meet the floor, so it is refused —
    /// and refused under its own arm, because the operator action differs from an old build's.</summary>
    [Test]
    [Arguments(null)]
    [Arguments("")]
    [Arguments("   ")]
    [Arguments("banana")]
    [Arguments("unknown")]
    [Arguments("v")]
    public async Task AnUnidentifiableBuild_IsUnresolvedRatherThanBelowTheFloor(string? installed) =>
        await Assert.That(AntigravityReviewerCapability.Decide(Posix, true, installed, Floor))
            .IsEqualTo(AntigravityReviewerDecision.VersionUnresolved);

    /// <summary>A vendor prerelease suffix is not an unidentifiable build — the shared comparison
    /// strips it, and refusing here would take the reviewer offline on a build that meets the
    /// floor.</summary>
    [Test]
    [Arguments("1.1.10-beta.1")]
    [Arguments("1.2.0+build7")]
    public async Task APrereleaseOrBuildSuffixStillMeetsTheFloor(string installed) =>
        await Assert.That(AntigravityReviewerCapability.Decide(Posix, true, installed, Floor))
            .IsEqualTo(AntigravityReviewerDecision.Allowed);

    /// <summary>A floor that is not itself a version fails CLOSED. The value is operator-settable, so
    /// a typo must refuse rather than admit every build.</summary>
    [Test]
    [Arguments("")]
    [Arguments("latest")]
    [Arguments("1.1.x")]
    public async Task AnUnparseableFloor_RefusesRatherThanAdmittingEverything(string minimum) =>
        await Assert.That(AntigravityReviewerCapability.Decide(Posix, true, "9.9.9", minimum))
            .IsNotEqualTo(AntigravityReviewerDecision.Allowed);

    // ── the denial reasons ────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// The consent text is what an operator reads before turning this on, so its content is asserted
    /// rather than its presence.
    /// </summary>
    [Test]
    public async Task TheDisabledReason_NamesTheSwitchAndSaysWhereItGoes() {
        var reason = Reason(AntigravityReviewerDecision.Disabled, null);

        await Assert.That(reason).StartsWith("antigravity_unattended_reviewer_disabled");
        await Assert.That(reason).Contains("KCAP_ANTIGRAVITY_UNATTENDED_REVIEWER");
        await Assert.That(reason).Contains("daemon's environment");
    }

    /// <summary>
    /// The strongest available pin on "one ladder, not two": the shipped factory judges consent with
    /// its own inline check, and this asserts the capability's text is BYTE-IDENTICAL to what that
    /// check produces. If either side is reworded on its own, this goes red — so the two cannot drift
    /// into disagreeing about what an operator is told.
    /// </summary>
    [Test]
    public async Task TheDisabledReason_IsExactlyWhatTheFactorysOwnLadderReports() {
        var config = new DaemonConfig {
            AntigravityPath                      = "agy",
            AntigravityUnattendedReviewerEnabled = false,
            Name                                 = "test-daemon"
        };

        var factoryReason = new AntigravityHostedAgentRuntimeFactory(
            config, NullLoggerFactory.Instance, turnSource: null, binaryExists: _ => true)
            .DescribeUnattendedSupport().WithheldReason;

        await Assert.That(Reason(AntigravityReviewerDecision.Disabled, null)).IsEqualTo(factoryReason);
    }

    /// <summary>The platform refusal says WHY rather than merely refusing. Its text cannot be compared
    /// against the factory's the way the consent one is — that ladder reads the ambient OS, so the arm
    /// is unreachable from a POSIX test host — so the shared code is the pin.</summary>
    [Test]
    public async Task TheUnsupportedPlatformReason_SaysWhyRatherThanJustRefusing() {
        var reason = Reason(AntigravityReviewerDecision.UnsupportedPlatform, null);

        await Assert.That(reason).StartsWith("antigravity_reviewer_unsupported_platform");
        await Assert.That(reason).Contains("owner-only");
    }

    [Test]
    public async Task TheBelowMinimumReason_NamesBothVersionsAndTheUpgrade() {
        var reason = Reason(AntigravityReviewerDecision.VersionBelowMinimum, "1.1.8");

        await Assert.That(reason).StartsWith("antigravity_reviewer_version_below_minimum");
        await Assert.That(reason).Contains("1.1.8");
        await Assert.That(reason).Contains(Floor);
        await Assert.That(reason).Contains("upgrade");
        await Assert.That(reason).Contains("KCAP_ANTIGRAVITY_MIN_CLI_VERSION");
    }

    /// <summary>An operator whose <c>agy --version</c> stopped parsing needs a different action from
    /// one running an old build, which is the whole reason these are separate arms.</summary>
    [Test]
    public async Task TheUnresolvedReason_SendsTheOperatorToTheBinaryRatherThanToAnUpgrade() {
        var reason = Reason(AntigravityReviewerDecision.VersionUnresolved, null);

        await Assert.That(reason).StartsWith("antigravity_reviewer_version_unresolved");
        await Assert.That(reason).Contains("--version");
        await Assert.That(reason).Contains("KCAP_ANTIGRAVITY_PATH");
    }

    /// <summary>
    /// Antigravity has NO affirmation model and no <c>affirm</c> verb will exist for it, so no refusal
    /// may send an operator to one. Ranges over every arm because the trap is a text copied from a
    /// sibling reviewer, which is exactly how it would arrive.
    /// </summary>
    [Test]
    public async Task NoRefusalPointsAtAnAffirmCommand() {
        foreach (var decision in Enum.GetValues<AntigravityReviewerDecision>()) {
            if (decision == AntigravityReviewerDecision.Allowed) continue;

            await Assert.That(Reason(decision, "1.1.8")).DoesNotContain("affirm")
                .Because($"{decision} must not send an operator to a verb this vendor does not have");
        }
    }

    static string Reason(AntigravityReviewerDecision decision, string? installed) =>
        AntigravityReviewerCapability.DenialReason(decision, installed, Floor, binaryPath: "agy");
}
