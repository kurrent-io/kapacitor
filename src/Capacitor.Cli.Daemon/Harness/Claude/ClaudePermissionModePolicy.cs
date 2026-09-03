using Capacitor.Cli.Core;
using Capacitor.Cli.Core.Harness.Claude;

namespace Capacitor.Cli.Daemon.Harness.Claude;

/// <summary>
/// Eligibility and token validation for a caller-selected Claude permission mode, mirrored
/// textually by the server's <c>ClaudePermissionModeRequestPolicy</c> (different repositories, no
/// shared type). A mode is valid only for an interactive, non-borrowed Claude launch: a reviewer's
/// bypass and a borrowed checkout's prompting are containment guarantees, not preferences. Every
/// other shape is a coded rejection, never a silent drop.
/// </summary>
internal static class ClaudePermissionModePolicy {
    const string Vendor = "claude";

    /// <summary><c>null</c> when the launch may continue, which includes every mode-less launch.</summary>
    public static string? RejectionReason(LaunchAgentCommand cmd) {
        if (cmd.PermissionMode is not { } mode) return null;

        if (!string.Equals(cmd.Vendor, Vendor, StringComparison.OrdinalIgnoreCase))
            return $"permission_mode_wrong_vendor: a permission mode was supplied for vendor '{cmd.Vendor}' — "
                 + "permission modes apply only to Claude launches.";

        if (cmd.Kind != LaunchKind.Default)
            return "permission_mode_not_overridable: " + cmd.Kind switch {
                LaunchKind.ReviewFlow => "review-flow reviewers run under their own containment posture, so a "
                                       + "permission mode does not apply.",
                LaunchKind.Review     => "PR-review launches use a fixed containment; permission mode selection "
                                       + "applies only to interactive launches.",
                _                     => $"launch kind '{cmd.Kind}' is not an interactive launch; permission mode "
                                       + "selection applies only to interactive launches."
            };

        if (cmd.Borrowed)
            return "permission_mode_not_overridable: a borrowed launch runs in the requester's real checkout, so a "
                 + "permission mode does not apply.";

        if (!ClaudePermissionModes.IsOffered(mode))
            return $"permission_mode_invalid: unknown permission mode '{mode}'. "
                 + $"Valid values: {string.Join(", ", ClaudePermissionModes.Offered)}.";

        return null;
    }

    /// <summary>Advertised on connect so the server refuses a mode toward a daemon that would ignore it.</summary>
    public static string[] AdvertisedVendors(IEnumerable<string> supportedVendors) =>
        supportedVendors.Any(v => string.Equals(v, Vendor, StringComparison.OrdinalIgnoreCase))
            ? [Vendor]
            : [];
}
