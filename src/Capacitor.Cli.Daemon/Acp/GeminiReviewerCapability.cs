namespace Capacitor.Cli.Daemon.Acp;

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
/// <para><b>Why a certified version and not a floor.</b> The security mechanism is the vendor's MCP
/// allowlist behaving as an exclusive exact-match gate that the repository's own settings cannot widen. That
/// was established by reading <c>gemini-cli</c> 0.53.0's own matcher, and the binary the daemon launches is
/// whatever <c>GeminiPath</c> resolves. An upgrade can change matching, config precedence, or empty-list
/// semantics — so a capability flag set months ago must not silently carry consent across it. Hence a set of
/// versions whose matcher behaviour has been <i>certified</i>, not a minimum: an unknown version takes the
/// reviewer offline, which is the safe direction.</para>
///
/// <para>Deliberately stricter than the interactive hosting path, which runs any installed Gemini.
/// Broken hosting degrades to a broken agent; a broken MCP gate degrades to repository-controlled process
/// execution.</para>
/// </summary>
internal static class GeminiReviewerCapability {
    /// <summary>
    /// Versions whose MCP-allowlist behaviour has been certified by the gated live certification.
    ///
    /// <para><b>Adding to this set is not a version bump.</b> It asserts that the hostile-repository and
    /// no-reload certifications were re-run against that build and still pass. If they were not, leave it
    /// out — an absent version disables the reviewer rather than trusting it.</para>
    /// </summary>
    internal static readonly IReadOnlySet<string> CertifiedVersions =
        new HashSet<string>(StringComparer.Ordinal) { "0.53.0" };

    /// <summary>
    /// Pure decision. <paramref name="resolvedVersion"/> is the version of the binary this launch will
    /// actually use — null when it could not be resolved, which is treated as unknown and therefore denied.
    /// </summary>
    internal static bool IsEnabled(bool operatorEnabled, string? resolvedVersion) =>
        operatorEnabled
     && resolvedVersion is { Length: > 0 }
     && CertifiedVersions.Contains(resolvedVersion.Trim());

    /// <summary>
    /// The refusal reason, for a coded error an operator can act on. Separated from
    /// <see cref="IsEnabled"/> so the two cannot disagree about WHY a launch was denied.
    /// </summary>
    internal static string DenialReason(bool operatorEnabled, string? resolvedVersion) =>
        !operatorEnabled
            ? "gemini_unattended_reviewer_disabled: this daemon has not enabled Gemini as an unattended "
            + "review-flow reviewer. Enabling it accepts that a review grants prompt-injected repository "
            + "content code execution with this daemon user's authority, including its credentials — set "
            + "GeminiUnattendedReviewerEnabled on the daemon (not on the server) only if that is acceptable."
        : resolvedVersion is not { Length: > 0 }
            ? "gemini_unattended_reviewer_version_unresolved: the installed gemini version could not be "
            + "determined, so its MCP-allowlist behaviour cannot be treated as certified. The reviewer's "
            + "only containment is that allowlist, so an unverifiable build is refused."
        : $"gemini_unattended_reviewer_version_uncertified: gemini {resolvedVersion.Trim()} is not in the "
            + $"certified set [{string.Join(", ", CertifiedVersions.Order(StringComparer.Ordinal))}]. The "
            + "reviewer's containment rests on that version's MCP-allowlist semantics, so a build whose "
            + "behaviour has not been certified is refused rather than assumed compatible.";
}
