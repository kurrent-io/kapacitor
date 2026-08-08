using Capacitor.Cli.Core;

namespace Capacitor.Cli.Daemon.Acp;

internal enum AntigravityReviewerDecision {
    Allowed,
    UnsupportedPlatform,
    Disabled,
    VersionUnresolved,
    VersionNoMinimum,
    VersionBelowMinimum,
    VersionIncomparable
}

/// <summary>
/// Whether THIS daemon may run Antigravity's CLI (<c>agy</c>) as an unattended review-flow reviewer.
/// Pure, so every arm is testable without a vendor or a process.
///
/// <para><b>What enabling it consents to.</b> An unattended reviewer runs in a daemon-owned worktree
/// under a per-launch, owner-only <c>HOME</c>, and its findings text is returned to whoever requested
/// the review. That risk lands on the daemon OPERATOR, who is not necessarily the requester, which is
/// why the decision is daemon-local and enabling it is the consent event rather than a documented
/// default. Deliberately NOT Kiro's whole-filesystem-read paragraph: that claim is about a trusted
/// <c>fs_read</c> primitive this vendor does not expose, and a borrowed risk statement would be a
/// false one in either direction.</para>
///
/// <para><b>Consent is the ONE arm that is reviewer-only</b> (see
/// <c>AntigravityHostedAgentRuntimeFactory.LaunchRefusal</c>'s parameter doc). The paragraph above is
/// exactly why: the risk it describes is cross-principal, and a HOSTED launch has no counterpart —
/// the server resolves a launch's daemon with the caller's own user id, so the launcher is the
/// daemon's owner. Hosted Antigravity ships on by default; every other arm below still gates it.</para>
///
/// <para><b>Why a version MINIMUM.</b> Containment here is source suppression — an empty per-launch
/// <see cref="AntigravityReviewerHome"/> in place of the operator's own <c>~/.gemini</c>, whose kcap
/// capture plugin would otherwise fire against the conversation this runtime is already recording.
/// That is a HOSTED concern as much as a reviewer one, which is why this arm is not parameterised.
/// That the build honours <c>HOME</c> and reads no other global config source is a behaviour of the
/// BUILD, not of this repository. The recorded value is the OLDEST build this daemon will run: an
/// upgrade needs no action — which matters more here than for any sibling, since <c>agy</c> was
/// observed auto-updating itself mid-session (1.1.8 → 1.1.10) — a downgrade below it is refused, and
/// a build later found to be bad is excluded by raising the floor past it.</para>
///
/// <para><b>Why that record is state and not configuration.</b> An earlier revision of this gate read
/// the floor from <c>KCAP_ANTIGRAVITY_MIN_CLI_VERSION</c>. A value the operator could set from a shell
/// profile would be re-affirmed by their dotfiles rather than by them — the same "consent that isn't
/// consent" failure the enable flag exists to avoid. Kiro and Gemini use the same model; the shared
/// comparison lives in <see cref="Core.ReviewerVersionAffirmations"/> and the record in
/// <see cref="Core.ReviewerVersionStore"/>.</para>
///
/// <para><b>This is the whole ladder</b> (consent → platform → build), and
/// <c>AntigravityHostedAgentRuntimeFactory.LaunchRefusal</c> is its only production caller: it adds
/// the one arm this decision cannot express — a binary that does not resolve at all — and takes every
/// other verdict, and every text, from here. Both the advertisement seam and the launch boundary read
/// that one method, so the minimum cannot be enforced at only one of them, and the hosted/review
/// difference is a PARAMETER of that method rather than a second ladder that must agree with it.</para>
///
/// <para><b>The denial texts below are read by both launch shapes</b> (consent excepted, which only a
/// review can reach), so they describe the containment rather than the reviewer — a hosted operator
/// must never be sent to the reviewer consent flag by a version arm.</para>
/// </summary>
internal static class AntigravityReviewerCapability {
    /// <summary>
    /// The decision, with the platform passed IN rather than read from the ambient OS. There is
    /// deliberately no ambient-reading overload: the one production caller holds a platform seam of its
    /// own, and an overload that read the OS here would be the obvious thing to reach for next.
    ///
    /// <para>Kiro's gate records why it must be a parameter: reading the OS inside the decision
    /// short-circuited every consent and version arm to
    /// <see cref="AntigravityReviewerDecision.UnsupportedPlatform"/> on the Windows CI leg, so a dozen
    /// tests failed for a reason unrelated to what they asserted. As a parameter, every arm is
    /// reachable from any host — including the Windows one, which is otherwise unassertable on
    /// POSIX.</para>
    /// </summary>
    internal static AntigravityReviewerDecision Decide(
            bool posixHost, bool operatorEnabled, string? installedVersion, string? minimumVersion) {
        // Consent FIRST and short-circuiting: an installed-but-wedged agy must not be probed — let
        // alone stall a daemon start — for a feature the operator switched off.
        if (!operatorEnabled) return AntigravityReviewerDecision.Disabled;

        // Windows has no 0700, so the per-launch home that holds the reviewer's own transcript — and
        // therefore the review context — cannot be made owner-only.
        if (!posixHost) return AntigravityReviewerDecision.UnsupportedPlatform;

        // No discard: `_ => Allowed` would silently ADMIT any arm added later, which is the wrong
        // direction for this gate. CS8509 makes the next one a build failure instead.
        return ReviewerVersionAffirmations.Decide(installedVersion, minimumVersion) switch {
            ReviewerVersionAffirmation.MeetsMinimum      => AntigravityReviewerDecision.Allowed,
            ReviewerVersionAffirmation.Unresolved        => AntigravityReviewerDecision.VersionUnresolved,
            ReviewerVersionAffirmation.NoMinimumRecorded => AntigravityReviewerDecision.VersionNoMinimum,
            ReviewerVersionAffirmation.BelowMinimum      => AntigravityReviewerDecision.VersionBelowMinimum,
            ReviewerVersionAffirmation.Incomparable      => AntigravityReviewerDecision.VersionIncomparable
        };
    }

    /// <summary>
    /// The coded refusal an operator can act on. Separated from <see cref="Decide"/> so the two cannot
    /// disagree about WHY a launch was denied.
    /// </summary>
    /// <param name="binaryPath">The binary the daemon would launch, named in the unresolved arm so an
    /// operator checks the right one rather than whatever is first on PATH.</param>
    internal static string DenialReason(
            AntigravityReviewerDecision decision, string? installedVersion, string? minimumVersion,
            string binaryPath) =>
        decision switch {
            AntigravityReviewerDecision.Disabled =>
                "antigravity_unattended_reviewer_disabled: this daemon has EXPLICITLY disabled "
              + "unattended Antigravity reviews. Unset KCAP_ANTIGRAVITY_UNATTENDED_REVIEWER in the "
              + "daemon's environment (not on the server) to restore the default, which is enabled — or "
              + "set it to 1. A review runs under this daemon user's authority either way, as it does "
              + "for the never-gated Claude, Codex, Cursor and Copilot reviewers.",

            AntigravityReviewerDecision.UnsupportedPlatform =>
                "antigravity_reviewer_unsupported_platform: the per-launch home holds the agent's own "
              + "conversation transcript and cannot be created owner-only on Windows.",

            AntigravityReviewerDecision.VersionUnresolved =>
                $"antigravity_reviewer_version_unresolved: the version of '{binaryPath}' could not be "
              + "determined, so it cannot be compared against this daemon's recorded minimum. Check "
              + "that the Antigravity CLI is installed (the `agy` binary — the IDE alone is not "
              + "enough), that `agy --version` succeeds, and set KCAP_ANTIGRAVITY_PATH if it lives "
              + "elsewhere. A build we cannot identify is refused rather than assumed compatible.",

            AntigravityReviewerDecision.VersionNoMinimum =>
                "antigravity_reviewer_version_no_minimum: this daemon has no recorded minimum agy "
              + "version, so there is nothing to check the installed build against. A daemon records "
              + "one at startup whenever the Antigravity CLI resolves, so the usual cause is a daemon "
              + "that started before `agy` was installed — restart it, or record the installed build "
              + "now with `kcap daemon reviewer affirm --vendor antigravity`.",

            AntigravityReviewerDecision.VersionIncomparable =>
                $"antigravity_reviewer_version_incomparable: agy {Describe(installedVersion)} and this "
              + $"daemon's recorded minimum {Describe(minimumVersion)} cannot be ordered as version "
              + "numbers, so neither can be said to be newer. Record the installed build as the "
              + "minimum with `kcap daemon reviewer affirm --vendor antigravity`.",

            // Exhaustive, like Decide's switch: a discard would print the below-minimum text for a
            // future arm meaning something else, sending an operator to the wrong fix. Allowed throws
            // because asking for the denial reason of a permitted decision is a caller bug.
            AntigravityReviewerDecision.Allowed =>
                throw new ArgumentOutOfRangeException(
                    nameof(decision), decision, "Allowed is not a denial and has no reason."),

            AntigravityReviewerDecision.VersionBelowMinimum =>
                $"antigravity_reviewer_version_below_minimum: agy {Describe(installedVersion)} is "
              + $"installed but this daemon's recorded minimum is {Describe(minimumVersion)}. "
              + "Containment here depends on the build honouring HOME and reading no other global "
              + "config source — for a hosted agent as much as for a reviewer — so an OLDER build than "
              + "the one recorded is refused. Upgrade the Antigravity CLI, or deliberately lower the "
              + "minimum to the installed build with `kcap daemon reviewer affirm --vendor antigravity`."
        };

    static string Describe(string? version) => ReviewerVersionAffirmations.Describe(version);
}
