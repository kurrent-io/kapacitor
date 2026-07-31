using Capacitor.Cli.Core;
using Capacitor.Cli.Core.LocalIpc;

namespace Capacitor.Cli.Daemon.Services;

/// <summary>
/// Single source of truth for the caller-selected Codex launch posture: eligibility + token
/// validation (<see cref="RejectionReason"/>, consumed by the orchestrator's pre-flight guard) and
/// effective resolution (<see cref="Resolve"/>, consumed by <see cref="CodexLauncher.BuildArgs"/>
/// AND by the launch path that stamps the applied pair onto the AgentInstance, so the registration
/// echo can never diverge from the argv).
///
/// Selection applies ONLY to interactive daemon-owned-worktree launches. A borrowed cwd is the
/// user's real checkout and is always read-only; a review-flow reviewer is unattended and always
/// runs with approvals off. Those two values are containment guarantees, not preferences, so a
/// posture supplied for either fails the launch rather than being silently dropped or honoured.
/// </summary>
internal static class CodexPosturePolicy {
    static readonly HashSet<string> Sandboxes = new(StringComparer.Ordinal) {
        "read-only", "workspace-write", "danger-full-access"
    };

    static readonly HashSet<string> Approvals = new(StringComparer.Ordinal) {
        "untrusted", "on-request", "never"
    };

    /// <summary>Coded rejection for an ineligible or malformed posture block; <c>null</c> when the
    /// launch may continue — which includes every posture-less launch, whatever its shape.</summary>
    public static string? RejectionReason(LaunchAgentCommand cmd) {
        if (cmd.CodexPosture is not { } posture) return null;

        if (!string.Equals(cmd.Vendor, "codex", StringComparison.OrdinalIgnoreCase))
            return $"codex_posture_wrong_vendor: a Codex launch posture was supplied for vendor '{cmd.Vendor}' — "
                 + "the posture block applies only to Codex launches.";

        if (cmd.Kind == LaunchKind.ReviewFlow)
            return "codex_posture_not_overridable: review-flow reviewers run unattended, so their approval "
                 + "policy is daemon-derived and cannot be overridden.";

        if (cmd.Kind == LaunchKind.Review)
            return "codex_posture_not_overridable: PR-review launches use a fixed posture; selection applies "
                 + "only to interactive launches.";

        if (cmd.Borrowed)
            return "codex_posture_not_overridable: a borrowed launch runs in the requester's real checkout and "
                 + "is always read-only, so its posture is daemon-derived and cannot be overridden.";

        if (string.IsNullOrWhiteSpace(posture.Sandbox) || string.IsNullOrWhiteSpace(posture.Approval))
            return "codex_posture_invalid: a posture block must carry both a sandbox and an approval policy.";

        if (!Sandboxes.Contains(posture.Sandbox))
            return $"codex_posture_invalid: unknown sandbox '{posture.Sandbox}'. "
                 + "Valid values: read-only, workspace-write, danger-full-access.";

        if (string.Equals(posture.Approval, "on-failure", StringComparison.Ordinal))
            return "codex_posture_invalid: approval policy 'on-failure' is deprecated upstream and not supported.";

        if (!Approvals.Contains(posture.Approval))
            return $"codex_posture_invalid: unknown approval policy '{posture.Approval}'. "
                 + "Valid values: untrusted, on-request, never.";

        return null;
    }

    /// <summary>The effective sandbox/approval pair: the selected posture when one is present, else
    /// the derived containment defaults. Callers establish eligibility via
    /// <see cref="RejectionReason"/> before passing a non-null posture.</summary>
    public static (string Sandbox, string Approval) Resolve(
            WorkLocation work, bool isReviewFlow, CodexLaunchPosture? posture) =>
        posture is not null
            ? (posture.Sandbox, posture.Approval)
            : (work == WorkLocation.BorrowedCwd ? "read-only" : "workspace-write",
               isReviewFlow ? "never" : "on-request");
}
