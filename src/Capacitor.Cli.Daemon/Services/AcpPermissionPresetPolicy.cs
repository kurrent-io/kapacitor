using Capacitor.Cli.Core;
using Capacitor.Cli.Daemon.Acp;

namespace Capacitor.Cli.Daemon.Services;

/// <summary>
/// Single source of truth for a caller-selected ACP permission preset: eligibility + token
/// validation, consumed by the orchestrator's pre-flight guard. Sibling of
/// <see cref="Harness.Codex.CodexPosturePolicy"/> and a textual mirror of the server's <c>AcpPresetRequestPolicy</c>
/// (the two live in different repositories, so there is no shared type). A preset is valid ONLY for an
/// interactive, non-borrowed launch of an ACP-permission-routed vendor — the daemon's authoritative
/// set is the ACP descriptor registry (<see cref="AcpVendorDescriptors"/>). Every other shape is a
/// coded rejection, never a silent drop.
/// </summary>
internal static class AcpPermissionPresetPolicy {
    /// <summary>Coded rejection for an ineligible or malformed preset; <c>null</c> when the launch may
    /// continue — which includes every preset-less launch, whatever its shape.</summary>
    public static string? RejectionReason(LaunchAgentCommand cmd) {
        if (cmd.AcpPermissionPreset is not { } preset) return null;

        if (!AcpPermissionPresets.RoutedVendors.Contains(cmd.Vendor))
            return $"acp_preset_wrong_vendor: a permission preset was supplied for vendor '{cmd.Vendor}' — "
                 + "presets apply only to ACP-hosted agents.";

        // Positive eligibility: anything that is not an interactive launch is rejected, so a value
        // outside the known enum (LaunchKind crosses the wire as a number) cannot be treated as
        // interactive. Known kinds keep their specific diagnostic.
        if (cmd.Kind != LaunchKind.Default)
            return "acp_preset_not_overridable: " + cmd.Kind switch {
                LaunchKind.ReviewFlow => "review-flow reviewers run under their own containment posture, so a "
                                       + "permission preset does not apply.",
                LaunchKind.Review     => "PR-review launches use a fixed containment; preset selection applies "
                                       + "only to interactive launches.",
                _                     => $"launch kind '{cmd.Kind}' is not an interactive launch; preset "
                                       + "selection applies only to interactive launches."
            };

        if (cmd.Borrowed)
            return "acp_preset_not_overridable: a borrowed launch runs in the requester's real checkout, so a "
                 + "permission preset does not apply.";

        if (!AcpPermissionPresets.IsKnown(preset))
            return $"acp_preset_invalid: unknown preset '{preset}'. Valid values: explore, edit.";

        return null;
    }
}
