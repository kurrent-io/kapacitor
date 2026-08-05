namespace Capacitor.Cli.Daemon.Acp;

internal enum KiroReviewerDecision {
    Allowed,
    UnsupportedPlatform,
    Disabled,
    VersionUnresolved,
    VersionUnaffirmed
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
/// <para><b>Why a version affirmation rather than a certified set.</b> Containment is source
/// suppression — an empty per-launch <see cref="KiroReviewerHome"/> plus the worktree layer's removal
/// of branch-authored config. The second is ours; the first is not, because Kiro honouring
/// <c>KIRO_HOME</c> and reading no other global config source are behaviours of the build. A
/// maintainer-curated certified set (the Gemini shape) would take the reviewer offline on every
/// vendor release, so this fails closed when the installed version CHANGES and the operator clears
/// it. See <see cref="KiroReviewerVersionStore"/>.</para>
/// </summary>
internal static class KiroReviewerCapability {
    /// <summary>Production entry point: reads the host platform, then defers to the pure overload.</summary>
    internal static KiroReviewerDecision Decide(
            bool operatorEnabled, string? installedVersion, string? affirmedVersion) =>
        Decide(!OperatingSystem.IsWindows(), operatorEnabled, installedVersion, affirmedVersion);

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
            bool posixHost, bool operatorEnabled, string? installedVersion, string? affirmedVersion) {
        // Windows has no 0700, so the transcript-bearing reviewer home cannot be made owner-only and
        // the disposal requirement cannot be met. Refuse rather than advertise a reviewer whose review
        // context is world-readable.
        if (!posixHost) return KiroReviewerDecision.UnsupportedPlatform;

        // Operator flag FIRST and short-circuiting. Evaluating the version probe alongside it would let
        // an installed-but-wedged vendor binary hang daemon startup on a feature the operator switched
        // off — the same trap the Gemini gate documents.
        if (!operatorEnabled) return KiroReviewerDecision.Disabled;

        if (installedVersion is not { Length: > 0 } installed || installed.Trim().Length == 0)
            return KiroReviewerDecision.VersionUnresolved;

        if (affirmedVersion is not { Length: > 0 } affirmed || affirmed.Trim().Length == 0)
            return KiroReviewerDecision.VersionUnaffirmed;

        return string.Equals(installed.Trim(), affirmed.Trim(), StringComparison.Ordinal)
            ? KiroReviewerDecision.Allowed
            : KiroReviewerDecision.VersionUnaffirmed;
    }

    /// <summary>
    /// The coded refusal an operator can act on. Separated from <see cref="Decide"/> so the two
    /// cannot disagree about WHY a launch was denied. The disabled text is the acceptance artifact
    /// for the risk in this type's summary, so its content is asserted, not just its presence.
    /// </summary>
    internal static string DenialReason(
            KiroReviewerDecision decision, string? installedVersion, string? affirmedVersion) =>
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
              + "determined, so it cannot be matched against the version this daemon affirmed. A build "
              + "we cannot identify is refused rather than assumed compatible.",

            _ =>
                $"kiro_reviewer_version_unaffirmed: kiro-cli {Describe(installedVersion)} is installed "
              + $"but this daemon affirmed {Describe(affirmedVersion)}. The reviewer's MCP containment "
              + "depends on this build honouring KIRO_HOME and reading no other global config source, "
              + "so a changed build is refused until an operator confirms it: run "
              + "`kcap daemon reviewer affirm --vendor kiro`."
        };

    static string Describe(string? version) =>
        version is { Length: > 0 } v && v.Trim().Length > 0 ? v.Trim() : "<none>";
}
