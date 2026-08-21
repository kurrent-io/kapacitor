using System.Diagnostics.CodeAnalysis;

namespace Capacitor.Cli.Daemon.Acp;

/// <summary>A resolved launch-time permission preset: its wire token and the exact set of ACP
/// <c>ToolKind</c> tokens it auto-approves. Consumed by both the pre-flight policy (validation) and
/// the <see cref="AcpInteractionBridge"/> (enforcement), from ONE map, so the two cannot drift.</summary>
internal sealed record AcpLaunchPermissionPreset(string Token, IReadOnlySet<string> AutoApprovedKinds);

/// <summary>
/// The single launch-time permission-preset vocabulary for ACP-hosted agents. Enforcement is by ACP
/// tool kind only: a request whose <c>toolCall.kind</c> is in the preset's set is auto-approved with a
/// least-privilege <c>allow_once</c> option; everything else (including a kind-less frame) keeps
/// prompting. The two v1 presets are monotone — <c>edit</c> is a superset of <c>explore</c> — and
/// neither pre-approves <c>execute</c> (shell) or <c>fetch</c> (network).
/// </summary>
internal static class AcpPermissionPresets {
    internal const string Explore = "explore";
    internal const string Edit    = "edit";

    static readonly AcpLaunchPermissionPreset ExplorePreset = new(
        Explore, new HashSet<string>(StringComparer.Ordinal) { "read", "search" });

    static readonly AcpLaunchPermissionPreset EditPreset = new(
        Edit, new HashSet<string>(StringComparer.Ordinal) { "read", "search", "edit", "move", "delete" });

    /// <summary>Resolves a wire token to its preset; false (and null out) for null/blank/unknown.</summary>
    internal static bool TryResolve(string? token, [NotNullWhen(true)] out AcpLaunchPermissionPreset? preset) {
        preset = token switch {
            Explore => ExplorePreset,
            Edit    => EditPreset,
            _       => null
        };

        return preset is not null;
    }

    /// <summary>True when <paramref name="token"/> is a recognised preset token (never for null/blank).</summary>
    internal static bool IsKnown(string? token) => token is Explore or Edit;

    /// <summary>The daemon's authoritative ACP-permission-routed vendor tokens, sourced from the ACP
    /// descriptors' own <see cref="AcpVendorDescriptor.Vendor"/> so the policy (which rejects a preset
    /// for a non-member) and the advertisement (which offers presets for supported members) cannot
    /// drift from the launch runtime that enforces them.</summary>
    internal static readonly IReadOnlySet<string> RoutedVendors = new HashSet<string>(StringComparer.Ordinal) {
        AcpVendorDescriptors.Cursor.Vendor,
        AcpVendorDescriptors.Copilot.Vendor,
        AcpVendorDescriptors.Kiro.Vendor,
        AcpVendorDescriptors.OpenCode.Vendor,
        AcpVendorDescriptors.Gemini.Vendor,
    };
}
