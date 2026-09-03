using Capacitor.Cli.Core;
using Capacitor.Cli.Daemon.Harness.Claude;

namespace Capacitor.Cli.Daemon.Tests.Unit.Harness.Claude;

public class ClaudePermissionModePolicyTests {
    static LaunchAgentCommand Cmd(
        string     vendor   = "claude",
        LaunchKind kind     = LaunchKind.Default,
        bool       borrowed = false,
        string?    mode     = "acceptEdits"
    ) => new(
        "agent1", "prompt", "default", null, "/repo", null, null, vendor,
        Kind: kind, Borrowed: borrowed, PermissionMode: mode
    );

    [Test]
    public async Task Absent_mode_is_accepted_for_every_launch_shape() {
        await Assert.That(ClaudePermissionModePolicy.RejectionReason(Cmd(mode: null))).IsNull();
        await Assert.That(ClaudePermissionModePolicy.RejectionReason(Cmd(kind: LaunchKind.ReviewFlow, mode: null))).IsNull();
        await Assert.That(ClaudePermissionModePolicy.RejectionReason(Cmd(vendor: "codex", borrowed: true, mode: null))).IsNull();
    }

    [Test]
    [Arguments("manual")]
    [Arguments("acceptEdits")]
    [Arguments("auto")]
    [Arguments("bypassPermissions")]
    public async Task Every_offered_mode_is_accepted_on_an_interactive_claude_launch(string mode) {
        await Assert.That(ClaudePermissionModePolicy.RejectionReason(Cmd(mode: mode))).IsNull();
    }

    [Test]
    [Arguments("codex")]
    [Arguments("cursor")]
    [Arguments("pi")]
    [Arguments(null)]
    [Arguments("")]
    public async Task Mode_for_a_non_claude_vendor_is_rejected_not_thrown(string? vendor) {
        await Assert.That(ClaudePermissionModePolicy.RejectionReason(Cmd(vendor: vendor!))).StartsWith("permission_mode_wrong_vendor:");
    }

    [Test]
    [Arguments(LaunchKind.ReviewFlow)]
    [Arguments(LaunchKind.Review)]
    public async Task Mode_on_a_non_interactive_launch_is_rejected(LaunchKind kind) {
        await Assert.That(ClaudePermissionModePolicy.RejectionReason(Cmd(kind: kind))).StartsWith("permission_mode_not_overridable:");
    }

    [Test]
    public async Task Mode_on_a_borrowed_launch_is_rejected() {
        await Assert.That(ClaudePermissionModePolicy.RejectionReason(Cmd(borrowed: true))).StartsWith("permission_mode_not_overridable:");
    }

    /// Only the modes the product offers are forwarded; "plan" is a Claude token but not an offered
    /// one, so it is refused like any other unknown value rather than reshaping the session.
    [Test]
    [Arguments("yolo")]
    [Arguments("plan")]
    [Arguments("AcceptEdits")]
    public async Task Unknown_mode_token_is_rejected(string mode) {
        await Assert.That(ClaudePermissionModePolicy.RejectionReason(Cmd(mode: mode))).StartsWith("permission_mode_invalid:");
    }

    [Test]
    public async Task Advertised_vendors_is_claude_when_hosted_and_empty_otherwise() {
        await Assert.That(ClaudePermissionModePolicy.AdvertisedVendors(["claude", "codex"])).IsEquivalentTo(new[] { "claude" });
        await Assert.That(ClaudePermissionModePolicy.AdvertisedVendors(["codex", "cursor"])).IsEmpty();
        await Assert.That(ClaudePermissionModePolicy.AdvertisedVendors([])).IsEmpty();
    }
}
