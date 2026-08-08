using Capacitor.Cli.Core;

namespace Capacitor.Cli.Daemon.Acp;

internal enum GeminiReviewerDecision {
    Allowed,
    Disabled,
    VersionUnresolved,
    VersionNoMinimum,
    VersionBelowMinimum,
    VersionIncomparable
}

/// <summary>
/// Whether THIS daemon may run Gemini as an unattended review-flow reviewer. Two conditions, both
/// fail-closed, and the type is pure so both are testable without a vendor or a process.
///
/// <para><b>Why a capability at all.</b> An unattended reviewer runs in a daemon-owned worktree with the
/// daemon's own HOME, so prompt-injected repository content that reaches the model's tool use gets code
/// execution with the daemon user's full authority — durable credential compromise included. That risk lands
/// on the DAEMON OPERATOR, who is not necessarily the person requesting the review: a caller can ask for
/// <c>vendor: "gemini"</c> without owning the host being exposed. So the decision belongs in daemon-local
/// configuration, and <b>enabling it is the operator's consent event</b>. A non-default plus documentation
/// would be informed guidance, not consent.</para>
///
/// <para><b>Why the build is gated, and why by affirmation.</b> The security mechanism is the vendor's MCP
/// allowlist behaving as an exclusive exact-match gate that the repository's own settings cannot widen. That
/// was established by reading <c>gemini-cli</c> 0.53.0's own matcher, and the binary the daemon launches is
/// whatever <c>GeminiPath</c> resolves — an upgrade can change matching, config precedence, or empty-list
/// semantics, so a capability flag set months ago must not silently carry consent across it.</para>
///
/// <para>This was previously a maintainer-curated set of certified versions, and that shape failed in
/// practice: the reviewer went offline at <c>0.54.0</c>, one patch ahead of the certified <c>0.53.0</c>, and
/// could only come back via a kcap release. Every Gemini release repeated it. It now uses Kiro's model —
/// fail closed when the installed build CHANGES, cleared by the operator who is already the consenting
/// party, via <c>kcap daemon reviewer affirm --vendor gemini</c>.</para>
///
/// <para><b>What that trade is, stated plainly.</b> The certified set asserted that a maintainer had read
/// that build's matcher. An affirmation asserts only that the operator accepted this build. It is the weaker
/// claim — but it is made by the party who carries the risk, on the machine that carries it, and the
/// alternative was a reviewer nobody could run.</para>
///
/// <para><b>And the recorded value is now a MINIMUM, not an exact match — which this doc used to argue
/// against.</b> The paragraph is kept rather than deleted, because it states the real cost: a floor
/// assumes the allowlist's semantics can only improve, which is an assumption about someone else's code,
/// and it silently admits a future build that changed matching to prefix, flipped empty-list semantics, or
/// let repository settings win. That objection stands on its merits and was overruled deliberately —
/// exact-match kept the treadmill the certified set was abandoned for, merely relocating it from a kcap
/// release onto the operator, who then re-took the same acceptance on every patch. The acceptance now
/// carries forward across upgrades instead, and the affirm verb raises the floor past a build once found
/// to be bad. If a Gemini release is ever found to have weakened the allowlist, this is the paragraph that
/// says what to reach for.</para>
///
/// <para>Deliberately stricter than the interactive hosting path, which runs any installed Gemini.
/// Broken hosting degrades to a broken agent; a broken MCP gate degrades to repository-controlled process
/// execution.</para>
/// </summary>
internal static class GeminiReviewerCapability {
    /// <summary>
    /// Pure decision. <paramref name="installedVersion"/> is the version of the binary this launch will
    /// actually use — null when it could not be resolved, which is treated as unknown and therefore denied.
    /// </summary>
    internal static GeminiReviewerDecision Decide(
            bool operatorEnabled, string? installedVersion, string? minimumVersion) {
        // Operator flag FIRST and short-circuiting, so a daemon that opted out never interrogates the
        // vendor binary at all — an installed-but-wedged binary must not hang startup on a feature that
        // is switched off.
        if (!operatorEnabled) return GeminiReviewerDecision.Disabled;

        // No discard: `_ => Allowed` silently ADMITTED any arm added later, which is the wrong
        // direction for this gate. CS8509 makes the next one a build failure instead.
        return ReviewerVersionAffirmations.Decide(installedVersion, minimumVersion) switch {
            ReviewerVersionAffirmation.MeetsMinimum      => GeminiReviewerDecision.Allowed,
            ReviewerVersionAffirmation.Unresolved        => GeminiReviewerDecision.VersionUnresolved,
            ReviewerVersionAffirmation.NoMinimumRecorded => GeminiReviewerDecision.VersionNoMinimum,
            ReviewerVersionAffirmation.BelowMinimum      => GeminiReviewerDecision.VersionBelowMinimum,
            ReviewerVersionAffirmation.Incomparable      => GeminiReviewerDecision.VersionIncomparable
        };
    }

    /// <summary>
    /// The refusal reason, for a coded error an operator can act on. Separated from
    /// <see cref="Decide"/> so the two cannot disagree about WHY a launch was denied.
    /// </summary>
    internal static string DenialReason(
            GeminiReviewerDecision decision, string? installedVersion, string? minimumVersion) =>
        decision switch {
            GeminiReviewerDecision.Disabled =>
                "gemini_unattended_reviewer_disabled: this daemon has EXPLICITLY disabled Gemini as an "
              + "unattended review-flow reviewer. Unset KCAP_GEMINI_UNATTENDED_REVIEWER in the daemon's "
              + "environment (not on the server) to restore the default, which is enabled — or set it "
              + "to 1. Worth knowing before you re-enable: a Gemini review grants prompt-injected "
              + "repository content code execution with this daemon user's authority. That is a real "
              + "risk, but it is the same posture as the never-gated Claude, Codex, Cursor and Copilot "
              + "reviewers, so this switch narrows nothing a requester cannot route around.",

            GeminiReviewerDecision.VersionUnresolved =>
                "gemini_reviewer_version_unresolved: the installed gemini version could not be "
              + "determined, so it cannot be compared against this daemon's recorded minimum. The "
              + "reviewer's only containment is that build's MCP allowlist, so an unverifiable build is "
              + "refused.",

            GeminiReviewerDecision.VersionNoMinimum =>
                "gemini_reviewer_version_no_minimum: this daemon has no recorded minimum gemini "
              + "version, so there is nothing to check the installed build against. A daemon records "
              + "one automatically at startup, so the usual cause is that the version probe failed "
              + "then — check that `gemini --version` succeeds for the daemon user, and restart. To "
              + "record one now without restarting, run `kcap daemon reviewer affirm --vendor gemini`.",

            GeminiReviewerDecision.VersionIncomparable =>
                $"gemini_reviewer_version_incomparable: gemini {Describe(installedVersion)} and this "
              + $"daemon's recorded minimum {Describe(minimumVersion)} cannot be ordered as version "
              + "numbers, so neither can be said to be newer. Record the installed build as the "
              + "minimum with `kcap daemon reviewer affirm --vendor gemini`.",

            // Exhaustive, like Decide's switch and for a weaker but real version of the same reason: a
            // discard here would print the below-minimum text for a future arm that means something
            // else, telling an operator to fix the wrong thing. Allowed throws rather than returning a
            // string, because asking for the denial reason of a permitted decision is a caller bug.
            GeminiReviewerDecision.Allowed =>
                throw new ArgumentOutOfRangeException(
                    nameof(decision), decision, "Allowed is not a denial and has no reason."),

            GeminiReviewerDecision.VersionBelowMinimum =>
                $"gemini_reviewer_version_below_minimum: gemini {Describe(installedVersion)} is "
              + $"installed but this daemon's recorded minimum is {Describe(minimumVersion)}. The "
              + "reviewer's containment rests on the build's MCP-allowlist semantics — an exclusive "
              + "exact-match gate the reviewed repository cannot widen — so an OLDER build than the "
              + "one recorded is refused. Upgrade gemini, or deliberately lower the minimum to the "
              + "installed build with `kcap daemon reviewer affirm --vendor gemini`."
        };

    static string Describe(string? version) => ReviewerVersionAffirmations.Describe(version);
}
