using Capacitor.Cli.Core;

namespace Capacitor.Cli.Daemon.Acp;

internal enum GeminiReviewerDecision {
    Allowed,
    Disabled,
    VersionUnresolved,
    VersionUnaffirmed
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
/// alternative was a reviewer nobody could run. A <i>minimum-version floor</i> was considered and rejected:
/// it would assume the allowlist's semantics can only improve, which is an assumption about someone else's
/// code, and would silently admit a future build that changed matching to prefix, flipped empty-list
/// semantics, or let repository settings win.</para>
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
            bool operatorEnabled, string? installedVersion, string? affirmedVersion) {
        // Operator flag FIRST and short-circuiting, so a daemon that opted out never interrogates the
        // vendor binary at all — an installed-but-wedged binary must not hang startup on a feature that
        // is switched off.
        if (!operatorEnabled) return GeminiReviewerDecision.Disabled;

        return ReviewerVersionAffirmations.Decide(installedVersion, affirmedVersion) switch {
            ReviewerVersionAffirmation.Unresolved => GeminiReviewerDecision.VersionUnresolved,
            ReviewerVersionAffirmation.Unaffirmed => GeminiReviewerDecision.VersionUnaffirmed,
            _                                     => GeminiReviewerDecision.Allowed
        };
    }

    /// <summary>
    /// The refusal reason, for a coded error an operator can act on. Separated from
    /// <see cref="Decide"/> so the two cannot disagree about WHY a launch was denied.
    /// </summary>
    internal static string DenialReason(
            GeminiReviewerDecision decision, string? installedVersion, string? affirmedVersion) =>
        decision switch {
            GeminiReviewerDecision.Disabled =>
                "gemini_unattended_reviewer_disabled: this daemon has not enabled Gemini as an unattended "
              + "review-flow reviewer. Enabling it accepts that a review grants prompt-injected repository "
              + "content code execution with this daemon user's authority, including its credentials — set "
              + "KCAP_GEMINI_UNATTENDED_REVIEWER=1 in the daemon's environment (not on the server) only if "
              + "that is acceptable.",

            GeminiReviewerDecision.VersionUnresolved =>
                "gemini_reviewer_version_unresolved: the installed gemini version could not be "
              + "determined, so it cannot be matched against the version this daemon affirmed. The "
              + "reviewer's only containment is that build's MCP allowlist, so an unverifiable build is "
              + "refused.",

            _ =>
                $"gemini_reviewer_version_unaffirmed: gemini {Describe(installedVersion)} is installed but "
              + $"this daemon affirmed {Describe(affirmedVersion)}. The reviewer's containment rests on "
              + "this build's MCP-allowlist semantics — an exclusive exact-match gate the reviewed "
              + "repository cannot widen — so a changed build is refused until an operator confirms it: "
              + "run `kcap daemon reviewer affirm --vendor gemini`."
        };

    static string Describe(string? version) => ReviewerVersionAffirmations.Describe(version);
}
