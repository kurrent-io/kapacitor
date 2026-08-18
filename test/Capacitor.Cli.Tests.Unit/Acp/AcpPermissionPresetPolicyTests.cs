using Capacitor.Cli.Core;
using Capacitor.Cli.Daemon.Acp;
using Capacitor.Cli.Daemon.Services;

namespace Capacitor.Cli.Tests.Unit.Acp;

public class AcpPermissionPresetPolicyTests {
    static LaunchAgentCommand Cmd(
        string     vendor   = "cursor",
        LaunchKind kind     = LaunchKind.Default,
        bool       borrowed = false,
        string?    preset   = AcpPermissionPresets.Explore
    ) => new(
        "agent1", "prompt", "default", null, "/repo", null, null, vendor,
        Kind: kind, Borrowed: borrowed, AcpPermissionPreset: preset
    );

    [Test]
    public async Task Absent_preset_is_accepted_for_every_launch_shape() {
        await Assert.That(AcpPermissionPresetPolicy.RejectionReason(Cmd(preset: null))).IsNull();
        await Assert.That(AcpPermissionPresetPolicy.RejectionReason(Cmd(kind: LaunchKind.ReviewFlow, preset: null))).IsNull();
        await Assert.That(AcpPermissionPresetPolicy.RejectionReason(Cmd(vendor: "claude", preset: null))).IsNull();
    }

    [Test]
    [Arguments("cursor")]
    [Arguments("copilot")]
    [Arguments("kiro")]
    [Arguments("opencode")]
    [Arguments("gemini")]
    public async Task Both_presets_accepted_on_interactive_launches_for_acp_vendors(string vendor) {
        await Assert.That(AcpPermissionPresetPolicy.RejectionReason(Cmd(vendor, preset: AcpPermissionPresets.Explore))).IsNull();
        await Assert.That(AcpPermissionPresetPolicy.RejectionReason(Cmd(vendor, preset: AcpPermissionPresets.Edit))).IsNull();
    }

    [Test]
    [Arguments("claude")]
    [Arguments("codex")]
    [Arguments("antigravity")]
    [Arguments("pi")]
    public async Task Preset_for_a_non_acp_vendor_is_rejected(string vendor) {
        await Assert.That(AcpPermissionPresetPolicy.RejectionReason(Cmd(vendor))).StartsWith("acp_preset_wrong_vendor:");
    }

    [Test]
    [Arguments(LaunchKind.ReviewFlow)]
    [Arguments(LaunchKind.Review)]
    public async Task Preset_on_a_non_interactive_launch_is_rejected(LaunchKind kind) {
        await Assert.That(AcpPermissionPresetPolicy.RejectionReason(Cmd(kind: kind))).StartsWith("acp_preset_not_overridable:");
    }

    [Test]
    public async Task Preset_on_a_borrowed_launch_is_rejected() {
        await Assert.That(AcpPermissionPresetPolicy.RejectionReason(Cmd(borrowed: true))).StartsWith("acp_preset_not_overridable:");
    }

    [Test]
    public async Task Unknown_preset_token_is_rejected() {
        await Assert.That(AcpPermissionPresetPolicy.RejectionReason(Cmd(preset: "yolo"))).StartsWith("acp_preset_invalid:");
    }

    [Test]
    public async Task TryResolve_maps_tokens_and_rejects_unknown() {
        await Assert.That(AcpPermissionPresets.TryResolve("explore", out var explore)).IsTrue();
        await Assert.That(explore!.AutoApprovedKinds).IsEquivalentTo(new[] { "read", "search" });

        await Assert.That(AcpPermissionPresets.TryResolve("edit", out var edit)).IsTrue();
        await Assert.That(edit!.AutoApprovedKinds).IsEquivalentTo(new[] { "read", "search", "edit", "move", "delete" });

        await Assert.That(AcpPermissionPresets.TryResolve(null, out _)).IsFalse();
        await Assert.That(AcpPermissionPresets.TryResolve("nope", out _)).IsFalse();

        // Monotone: edit is a superset of explore.
        await Assert.That(explore.AutoApprovedKinds.All(edit.AutoApprovedKinds.Contains)).IsTrue();
    }
}
