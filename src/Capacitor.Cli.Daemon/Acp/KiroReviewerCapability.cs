using Capacitor.Cli.Core;

namespace Capacitor.Cli.Daemon.Acp;

internal enum KiroReviewerDecision {
    Allowed,
    UnsupportedPlatform,
    Disabled,
    VersionUnresolved,
    VersionNoMinimum,
    VersionBelowMinimum,
    VersionIncomparable
}

/// <summary>
/// Whether THIS daemon may run Kiro as an unattended review-flow reviewer. Pure, so every arm is
/// testable without a vendor or a process.
///
/// <para><b>What enabling it consents to, stated because it is broader than it looks.</b> An
/// unattended reviewer runs in a daemon-owned worktree with the daemon's own HOME, and a trusted
/// <c>fs_read</c> is measurably NOT path-scoped — it is a whole-filesystem read primitive under the
/// daemon's uid. So a review can read every file this daemon user can read, its own credentials
/// included, and its findings text is returned to whoever requested the review. That risk lands on
/// the daemon OPERATOR, who is not necessarily the requester, which is why the decision is
/// daemon-local configuration and enabling it is the consent event rather than a documented default.
/// The reviewer is supported only where the operator and the review requesters are in one trust
/// domain.</para>
///
/// <para><b>Why a version MINIMUM rather than a certified set or an exact affirmation.</b> Containment
/// is source suppression — an empty per-launch <see cref="KiroReviewerHome"/> plus the worktree
/// layer's removal of branch-authored config. The second is ours; the first is not, because Kiro
/// honouring <c>KIRO_HOME</c> and reading no other global config source are behaviours of the build.
/// A maintainer-curated certified set took the reviewer offline on every vendor release, recoverable
/// only by a kcap release; an exact affirmation fixed the release-coupling but kept the treadmill,
/// merely relocating it onto the operator. The recorded value is now the OLDEST build this daemon
/// will run, so a vendor upgrade needs no action and a downgrade below it is refused. The trade — a
/// future build that weakens its own containment is admitted silently — is accepted, and the affirm
/// verb raises the floor past a build once found to be bad. Gemini uses the same model; the shared
/// comparison lives in <see cref="Core.ReviewerVersionAffirmations"/> and the record in
/// <see cref="Core.ReviewerVersionStore"/>.</para>
/// </summary>
internal static class KiroReviewerCapability {
    /// <summary>Production entry point: reads the host platform, then defers to the pure overload.</summary>
    internal static KiroReviewerDecision Decide(
            bool operatorEnabled, string? installedVersion, string? minimumVersion) =>
        Decide(!OperatingSystem.IsWindows(), operatorEnabled, installedVersion, minimumVersion);

    /// <summary>
    /// The decision, with the platform passed IN rather than read from the ambient OS.
    ///
    /// <para>An earlier revision called <c>OperatingSystem.IsWindows()</c> inside this method while its
    /// own summary claimed to be pure. It was not, and the cost was immediate: on the Windows CI leg
    /// every non-platform arm short-circuited to <see cref="KiroReviewerDecision.UnsupportedPlatform"/>,
    /// so a dozen tests asserting consent and version behaviour failed for a reason that had nothing to
    /// do with what they were testing. Taking the platform as an argument makes every arm reachable
    /// from any host — including the Windows arm itself, which was previously unassertable on POSIX.</para>
    /// </summary>
    internal static KiroReviewerDecision Decide(
            bool posixHost, bool operatorEnabled, string? installedVersion, string? minimumVersion) {
        // Windows has no 0700, so the transcript-bearing reviewer home cannot be made owner-only and
        // the disposal requirement cannot be met. Refuse rather than advertise a reviewer whose review
        // context is world-readable.
        if (!posixHost) return KiroReviewerDecision.UnsupportedPlatform;

        // Operator flag FIRST and short-circuiting. Evaluating the version probe alongside it would let
        // an installed-but-wedged vendor binary hang daemon startup on a feature the operator switched
        // off — the same trap the Gemini gate documents.
        if (!operatorEnabled) return KiroReviewerDecision.Disabled;

        // No discard: `_ => Allowed` silently ADMITTED any arm added later, which is the wrong
        // direction for this gate. CS8509 makes the next one a build failure instead.
        return ReviewerVersionAffirmations.Decide(installedVersion, minimumVersion) switch {
            ReviewerVersionAffirmation.MeetsMinimum      => KiroReviewerDecision.Allowed,
            ReviewerVersionAffirmation.Unresolved        => KiroReviewerDecision.VersionUnresolved,
            ReviewerVersionAffirmation.NoMinimumRecorded => KiroReviewerDecision.VersionNoMinimum,
            ReviewerVersionAffirmation.BelowMinimum      => KiroReviewerDecision.VersionBelowMinimum,
            ReviewerVersionAffirmation.Incomparable      => KiroReviewerDecision.VersionIncomparable
        };
    }

    /// <summary>
    /// The coded refusal an operator can act on. Separated from <see cref="Decide"/> so the two
    /// cannot disagree about WHY a launch was denied. The disabled text is the acceptance artifact
    /// for the risk in this type's summary, so its content is asserted, not just its presence.
    /// </summary>
    internal static string DenialReason(
            KiroReviewerDecision decision, string? installedVersion, string? minimumVersion) =>
        decision switch {
            KiroReviewerDecision.UnsupportedPlatform =>
                "kiro_reviewer_unsupported_platform: the Kiro unattended reviewer is POSIX-only. Its "
              + "isolated home holds the reviewer's own transcript, and so the review context, and "
              + "cannot be created owner-only on this platform.",

            KiroReviewerDecision.Disabled =>
                "kiro_unattended_reviewer_disabled: this daemon has not enabled Kiro as an unattended "
              + "review-flow reviewer. Enabling it grants a review read access to every file this "
              + "daemon user can read — including its own credentials — with no filesystem boundary, "
              + "and a reviewer can return what it read to whoever requested the review. Enable it "
              + "only on a daemon whose operator and review requesters are in one trust domain: set "
              + "KCAP_KIRO_UNATTENDED_REVIEWER=1 in the daemon's environment (not on the server).",

            KiroReviewerDecision.VersionUnresolved =>
                "kiro_reviewer_version_unresolved: the installed kiro-cli version could not be "
              + "determined, so it cannot be compared against this daemon's recorded minimum. A build "
              + "we cannot identify is refused rather than assumed compatible.",

            KiroReviewerDecision.VersionNoMinimum =>
                "kiro_reviewer_version_no_minimum: this daemon has no recorded minimum kiro-cli "
              + "version, so there is nothing to check the installed build against. The usual cause is "
              + "enabling the reviewer against an already-running daemon — it records a minimum at "
              + "startup, so restart it with KCAP_KIRO_UNATTENDED_REVIEWER set. To set one now without "
              + "restarting, run `kcap daemon reviewer affirm --vendor kiro`.",

            KiroReviewerDecision.VersionIncomparable =>
                $"kiro_reviewer_version_incomparable: kiro-cli {Describe(installedVersion)} and this "
              + $"daemon's recorded minimum {Describe(minimumVersion)} cannot be ordered as version "
              + "numbers, so neither can be said to be newer. Record the installed build as the "
              + "minimum with `kcap daemon reviewer affirm --vendor kiro`.",

            // Exhaustive, like Decide's switch: a discard would print the below-minimum text for a
            // future arm meaning something else, sending an operator to the wrong fix. Allowed throws
            // because asking for the denial reason of a permitted decision is a caller bug.
            KiroReviewerDecision.Allowed =>
                throw new ArgumentOutOfRangeException(
                    nameof(decision), decision, "Allowed is not a denial and has no reason."),

            KiroReviewerDecision.VersionBelowMinimum =>
                $"kiro_reviewer_version_below_minimum: kiro-cli {Describe(installedVersion)} is "
              + $"installed but this daemon's recorded minimum is {Describe(minimumVersion)}. The "
              + "reviewer's containment depends on the build honouring KIRO_HOME and reading no other "
              + "global config source, so an OLDER build than the one recorded is refused. Upgrade "
              + "kiro-cli, or deliberately lower the minimum to the installed build with "
              + "`kcap daemon reviewer affirm --vendor kiro`."
        };

    static string Describe(string? version) => ReviewerVersionAffirmations.Describe(version);
}
