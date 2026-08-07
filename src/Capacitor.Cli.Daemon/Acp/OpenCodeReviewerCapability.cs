using Capacitor.Cli.Core;

namespace Capacitor.Cli.Daemon.Acp;

internal enum OpenCodeReviewerDecision {
    Allowed,
    UnsupportedPlatform,
    Disabled,
    VersionUnresolved,
    VersionNoMinimum,
    VersionBelowMinimum,
    VersionIncomparable
}

/// <summary>
/// Whether THIS daemon may run OpenCode as an unattended review-flow reviewer. Pure, so every arm is
/// testable without a vendor or a process.
///
/// <para><b>What enabling it consents to.</b> The reviewer's tool surface is deliberately narrow —
/// <see cref="OpenCodeReviewerPermissions"/> denies everything and admits only the read family plus the
/// injected result channel, with no shell and no write, verified against a positive control. But
/// <c>read</c>/<c>grep</c>/<c>glob</c>/<c>list</c> are NOT path-scoped: they are whole-filesystem read
/// primitives under the daemon's uid. So a review can read every file this daemon user can read, its
/// own credentials included, and its findings text goes back to whoever requested the review. That risk
/// lands on the daemon OPERATOR, who is not necessarily the requester, which is why the decision is
/// daemon-local configuration and enabling it is the consent event. Identical in substance to the Kiro
/// gate; the narrower tool surface changes the blast radius of a WRITE, not of a read.</para>
///
/// <para><b>Why a version MINIMUM rather than a certified set or an exact affirmation.</b> Same model
/// as Kiro and Gemini, and for the same reason: containment here is source suppression plus a
/// permission table, and both are behaviours of the installed build — OpenCode honouring
/// <c>OPENCODE_CONFIG_DIR</c>, <c>OPENCODE_DISABLE_PROJECT_CONFIG</c> and <c>OPENCODE_PERMISSION</c>,
/// and applying that table to flattened MCP tool names. A curated certified set takes the reviewer
/// offline on every vendor release; an exact affirmation moves that treadmill onto the operator. The
/// recorded value is the OLDEST build this daemon will run, so an upgrade needs no action and a
/// downgrade below it is refused. The trade — a future build that weakens its own containment is
/// admitted silently — is accepted, and <c>kcap daemon reviewer affirm --vendor opencode</c> raises the
/// floor past a build once found to be bad. The shared comparison lives in
/// <see cref="ReviewerVersionAffirmations"/> and the record in <see cref="ReviewerVersionStore"/>.</para>
/// </summary>
internal static class OpenCodeReviewerCapability {
    /// <summary>Production entry point: reads the host platform, then defers to the pure overload.</summary>
    internal static OpenCodeReviewerDecision Decide(
            bool operatorEnabled, string? installedVersion, string? minimumVersion) =>
        Decide(!OperatingSystem.IsWindows(), operatorEnabled, installedVersion, minimumVersion);

    /// <summary>
    /// The decision, with the platform passed IN rather than read from the ambient OS — so every arm,
    /// the Windows one included, is reachable from any host. Reading the OS inside would make the whole
    /// consent-and-version ladder unassertable on the Windows CI leg, the trap
    /// <see cref="KiroReviewerCapability.Decide(bool, bool, string?, string?)"/> records having fallen
    /// into.
    /// </summary>
    internal static OpenCodeReviewerDecision Decide(
            bool posixHost, bool operatorEnabled, string? installedVersion, string? minimumVersion) {
        // Windows has no 0700, so the reviewer's config dir cannot be made owner-only — and its
        // EMPTINESS is the containment, which another local user could defeat by seeding an MCP server
        // into a writable directory.
        if (!posixHost) return OpenCodeReviewerDecision.UnsupportedPlatform;

        // Operator flag FIRST and short-circuiting: evaluating the version probe alongside it would let
        // an installed-but-wedged vendor binary stall daemon startup on a feature that is switched off.
        if (!operatorEnabled) return OpenCodeReviewerDecision.Disabled;

        // No discard: `_ => Allowed` would silently ADMIT any arm added later, the wrong direction for
        // this gate. CS8509 makes the next one a build failure instead.
        return ReviewerVersionAffirmations.Decide(installedVersion, minimumVersion) switch {
            ReviewerVersionAffirmation.MeetsMinimum      => OpenCodeReviewerDecision.Allowed,
            ReviewerVersionAffirmation.Unresolved        => OpenCodeReviewerDecision.VersionUnresolved,
            ReviewerVersionAffirmation.NoMinimumRecorded => OpenCodeReviewerDecision.VersionNoMinimum,
            ReviewerVersionAffirmation.BelowMinimum      => OpenCodeReviewerDecision.VersionBelowMinimum,
            ReviewerVersionAffirmation.Incomparable      => OpenCodeReviewerDecision.VersionIncomparable
        };
    }

    /// <summary>
    /// The coded refusal an operator can act on. Separated from <see cref="Decide"/> so the two cannot
    /// disagree about WHY a launch was denied. The disabled text is the acceptance artifact for the risk
    /// in this type's summary, so its content is asserted, not just its presence.
    /// </summary>
    internal static string DenialReason(
            OpenCodeReviewerDecision decision, string? installedVersion, string? minimumVersion) =>
        decision switch {
            OpenCodeReviewerDecision.UnsupportedPlatform =>
                "opencode_reviewer_unsupported_platform: the OpenCode unattended reviewer is POSIX-only. "
              + "Its containment is an EMPTY per-launch config directory, which cannot be created "
              + "owner-only on this platform — another local user could seed MCP servers into it.",

            OpenCodeReviewerDecision.Disabled =>
                "opencode_unattended_reviewer_disabled: this daemon has not enabled OpenCode as an "
              + "unattended review-flow reviewer. The reviewer gets no shell and no write tools, but its "
              + "read tools are NOT path-scoped: enabling this grants a review read access to every file "
              + "this daemon user can read — including its own credentials — and a reviewer can return "
              + "what it read to whoever requested the review. Enable it only on a daemon whose operator "
              + "and review requesters are in one trust domain: set "
              + "KCAP_OPENCODE_UNATTENDED_REVIEWER=1 in the daemon's environment (not on the server).",

            OpenCodeReviewerDecision.VersionUnresolved =>
                "opencode_reviewer_version_unresolved: the installed opencode version could not be "
              + "determined, so it cannot be compared against this daemon's recorded minimum. A build we "
              + "cannot identify is refused rather than assumed compatible.",

            OpenCodeReviewerDecision.VersionNoMinimum =>
                "opencode_reviewer_version_no_minimum: this daemon has no recorded minimum opencode "
              + "version, so there is nothing to check the installed build against. The usual cause is "
              + "enabling the reviewer against an already-running daemon — it records a minimum at "
              + "startup, so restart it with KCAP_OPENCODE_UNATTENDED_REVIEWER set. To set one now "
              + "without restarting, run `kcap daemon reviewer affirm --vendor opencode`.",

            OpenCodeReviewerDecision.VersionIncomparable =>
                $"opencode_reviewer_version_incomparable: opencode {Describe(installedVersion)} and this "
              + $"daemon's recorded minimum {Describe(minimumVersion)} cannot be ordered as version "
              + "numbers, so neither can be said to be newer. Record the installed build as the minimum "
              + "with `kcap daemon reviewer affirm --vendor opencode`.",

            // Exhaustive, like Decide's switch: a discard would print the below-minimum text for a
            // future arm meaning something else, sending an operator to the wrong fix. Allowed throws
            // because asking for the denial reason of a permitted decision is a caller bug.
            OpenCodeReviewerDecision.Allowed =>
                throw new ArgumentOutOfRangeException(
                    nameof(decision), decision, "Allowed is not a denial and has no reason."),

            OpenCodeReviewerDecision.VersionBelowMinimum =>
                $"opencode_reviewer_version_below_minimum: opencode {Describe(installedVersion)} is "
              + $"installed but this daemon's recorded minimum is {Describe(minimumVersion)}. The "
              + "reviewer's containment depends on the build honouring OPENCODE_CONFIG_DIR, "
              + "OPENCODE_DISABLE_PROJECT_CONFIG and OPENCODE_PERMISSION — and applying that permission "
              + "table to flattened MCP tool names — so an OLDER build than the one recorded is refused. "
              + "Upgrade opencode, or deliberately lower the minimum to the installed build with "
              + "`kcap daemon reviewer affirm --vendor opencode`."
        };

    static string Describe(string? version) => ReviewerVersionAffirmations.Describe(version);
}
