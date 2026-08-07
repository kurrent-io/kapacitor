namespace Capacitor.Cli.Daemon.Acp;

internal enum AntigravityReviewerDecision {
    Allowed,
    UnsupportedPlatform,
    Disabled,
    VersionUnresolved,
    VersionBelowMinimum
}

/// <summary>
/// Whether THIS daemon may run Antigravity's CLI (<c>agy</c>) as an unattended review-flow reviewer.
/// Pure, so every arm is testable without a vendor or a process.
///
/// <para><b>Why a minimum version FLOOR rather than an operator affirmation.</b> Kiro and Gemini
/// require an operator to affirm each installed build, because their containment depends on the build
/// honouring a HOME override. <c>agy</c> was observed auto-updating itself mid-session (1.1.8 →
/// 1.1.10) while this reviewer was being explored, so an affirmation gate would park the reviewer on
/// a release cadence the operator neither controls nor can predict — the reviewer would be offline
/// more often than not, and clearing the gate would become a reflex rather than a decision. Meeting
/// the floor is therefore enough; a defect in a later build is handled by a report and a raised
/// floor, which is why <see cref="DaemonConfig.AntigravityMinimumCliVersion"/> is operator-settable.
/// There is deliberately no <c>affirm</c> verb for this vendor, and no refusal below may point at
/// one.</para>
///
/// <para>The comparison is <see cref="DaemonRunner.CliVersionAllowed"/> — the same range grammar
/// reviewer certification already uses. A second version parser in this codebase is exactly the
/// "two things that must agree, with nothing making them" shape.</para>
///
/// <para><b>Ordering matches the shipped factory ladder</b> (consent → platform → build), so
/// <c>AntigravityHostedAgentRuntimeFactory.ReviewerRefusal</c> can delegate to this without changing
/// what any operator is told. <see cref="AntigravityReviewerDecision.Disabled"/>'s text is pinned
/// byte-for-byte against that ladder by test.</para>
/// </summary>
internal static class AntigravityReviewerCapability {
    /// <summary>Production entry point: reads the host platform, then defers to the pure overload.</summary>
    internal static AntigravityReviewerDecision Decide(
            bool operatorEnabled, string? installedVersion, string minimumVersion) =>
        Decide(!OperatingSystem.IsWindows(), operatorEnabled, installedVersion, minimumVersion);

    /// <summary>
    /// The decision, with the platform passed IN rather than read from the ambient OS.
    ///
    /// <para>Kiro's gate records why: reading the OS inside the decision short-circuited every
    /// consent and version arm to <see cref="AntigravityReviewerDecision.UnsupportedPlatform"/> on the
    /// Windows CI leg, so a dozen tests failed for a reason unrelated to what they asserted. As a
    /// parameter, every arm is reachable from any host — including the Windows one, which is
    /// otherwise unassertable on POSIX.</para>
    /// </summary>
    internal static AntigravityReviewerDecision Decide(
            bool posixHost, bool operatorEnabled, string? installedVersion, string minimumVersion) {
        // Consent FIRST and short-circuiting: an installed-but-wedged agy must not be probed — let
        // alone stall a daemon start — for a feature the operator switched off.
        if (!operatorEnabled) return AntigravityReviewerDecision.Disabled;

        // Windows has no 0700, so the per-launch home that holds the reviewer's own transcript — and
        // therefore the review context — cannot be made owner-only.
        if (!posixHost) return AntigravityReviewerDecision.UnsupportedPlatform;

        // Parseability asked THROUGH the same comparison rather than with a second parser: every
        // version that parses at all satisfies this range, so a false here means precisely "we could
        // not identify this build" and never "this build is old".
        if (!DaemonRunner.CliVersionAllowed(installedVersion, AnyParseableVersion))
            return AntigravityReviewerDecision.VersionUnresolved;

        // An unparseable FLOOR yields false here, so an operator typo refuses rather than admitting
        // every build.
        return DaemonRunner.CliVersionAllowed(installedVersion, ">=" + minimumVersion)
            ? AntigravityReviewerDecision.Allowed
            : AntigravityReviewerDecision.VersionBelowMinimum;
    }

    const string AnyParseableVersion = ">=0.0";

    /// <summary>
    /// The coded refusal an operator can act on. Separated from <see cref="Decide"/> so the two cannot
    /// disagree about WHY a launch was denied.
    /// </summary>
    /// <param name="binaryPath">The binary the daemon would launch, named in the unresolved arm so an
    /// operator checks the right one rather than whatever is first on PATH.</param>
    internal static string DenialReason(
            AntigravityReviewerDecision decision, string? installedVersion, string minimumVersion,
            string binaryPath) =>
        decision switch {
            // Byte-identical to AntigravityHostedAgentRuntimeFactory.ReviewerRefusal's consent arm,
            // pinned by test. Deliberately does NOT carry Kiro's whole-filesystem-read paragraph: that
            // claim is about a trusted fs_read primitive this vendor does not expose, and a borrowed
            // risk statement would be a false one in either direction.
            AntigravityReviewerDecision.Disabled =>
                "antigravity_unattended_reviewer_disabled: unattended Antigravity reviews are off on "
              + "this daemon. Set KCAP_ANTIGRAVITY_UNATTENDED_REVIEWER=1 in the daemon's environment "
              + "to opt in.",

            AntigravityReviewerDecision.UnsupportedPlatform =>
                "antigravity_reviewer_unsupported_platform: the reviewer's per-launch home holds "
              + "review context and cannot be created owner-only on Windows.",

            AntigravityReviewerDecision.VersionUnresolved =>
                $"antigravity_reviewer_version_unresolved: the version of '{binaryPath}' could not be "
              + $"determined, so it cannot be shown to meet the {minimumVersion} minimum. Check that "
              + "the Antigravity CLI is installed (the `agy` binary — the IDE alone is not enough), "
              + "that `agy --version` succeeds, and set KCAP_ANTIGRAVITY_PATH if it lives elsewhere.",

            _ =>
                $"antigravity_reviewer_version_below_minimum: agy {Describe(installedVersion)} is "
              + $"installed, below the {minimumVersion} minimum this daemon requires. Every behaviour "
              + "this reviewer depends on was established on that build, so please upgrade the "
              + "Antigravity CLI — or, if this build is known good, raise the floor with "
              + "KCAP_ANTIGRAVITY_MIN_CLI_VERSION."
        };

    static string Describe(string? version) =>
        string.IsNullOrWhiteSpace(version) ? "(unknown)" : version.Trim();
}
