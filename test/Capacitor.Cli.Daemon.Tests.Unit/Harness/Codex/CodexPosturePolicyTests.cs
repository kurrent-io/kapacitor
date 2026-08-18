using Capacitor.Cli.Core;
using Capacitor.Cli.Core.LocalIpc;
using Capacitor.Cli.Daemon.Harness.Codex;

namespace Capacitor.Cli.Daemon.Tests.Unit.Harness.Codex;

public class CodexPosturePolicyTests {
    static LaunchAgentCommand Cmd(
        string              vendor   = "codex",
        LaunchKind          kind     = LaunchKind.Default,
        bool                borrowed = false,
        CodexLaunchPosture? posture  = null
    ) => new(
        "agent1", "prompt", "default", null, "/repo", null, null, vendor,
        Kind: kind, Borrowed: borrowed, CodexPosture: posture
    );

    // === Absent block: every launch shape stays acceptable, unchanged ===

    [Test]
    public async Task Absent_block_is_accepted_for_every_launch_shape() {
        await Assert.That(CodexPosturePolicy.RejectionReason(Cmd())).IsNull();
        await Assert.That(CodexPosturePolicy.RejectionReason(Cmd(kind: LaunchKind.ReviewFlow))).IsNull();
        await Assert.That(CodexPosturePolicy.RejectionReason(Cmd(kind: LaunchKind.Review))).IsNull();
        await Assert.That(CodexPosturePolicy.RejectionReason(Cmd(borrowed: true))).IsNull();
        await Assert.That(CodexPosturePolicy.RejectionReason(Cmd(vendor: "claude"))).IsNull();
    }

    // === The selectable case: every supported sandbox x approval combination ===

    [Test]
    [Arguments("read-only", "untrusted")]
    [Arguments("read-only", "on-request")]
    [Arguments("read-only", "never")]
    [Arguments("workspace-write", "untrusted")]
    [Arguments("workspace-write", "on-request")]
    [Arguments("workspace-write", "never")]
    [Arguments("danger-full-access", "untrusted")]
    [Arguments("danger-full-access", "on-request")]
    [Arguments("danger-full-access", "never")]
    public async Task All_nine_combinations_are_accepted_on_interactive_owned_launches(string sandbox, string approval) {
        await Assert.That(CodexPosturePolicy.RejectionReason(Cmd(posture: new(sandbox, approval)))).IsNull();
    }

    // === Fail-closed eligibility: the containment invariants ===

    [Test]
    public async Task Posture_on_review_flow_fails_closed_with_coded_reason() {
        var reason = CodexPosturePolicy.RejectionReason(
            Cmd(kind: LaunchKind.ReviewFlow, posture: new("workspace-write", "on-request")));

        await Assert.That(reason).StartsWith("codex_posture_not_overridable:");
        await Assert.That(reason).Contains("review-flow");
    }

    [Test]
    public async Task Posture_on_pr_review_fails_closed_with_coded_reason() {
        var reason = CodexPosturePolicy.RejectionReason(
            Cmd(kind: LaunchKind.Review, posture: new("read-only", "on-request")));

        await Assert.That(reason).StartsWith("codex_posture_not_overridable:");
        await Assert.That(reason).Contains("PR-review");
    }

    [Test]
    public async Task Posture_on_borrowed_launch_fails_closed_with_coded_reason() {
        var reason = CodexPosturePolicy.RejectionReason(
            Cmd(borrowed: true, posture: new("read-only", "never")));

        await Assert.That(reason).StartsWith("codex_posture_not_overridable:");
        await Assert.That(reason).Contains("borrowed");
    }

    [Test]
    public async Task Posture_on_non_codex_vendor_fails_closed_with_coded_reason() {
        var reason = CodexPosturePolicy.RejectionReason(
            Cmd(vendor: "claude", posture: new("read-only", "never")));

        await Assert.That(reason).StartsWith("codex_posture_wrong_vendor:");
    }

    /// <summary>LaunchKind crosses the wire as a number, so a malformed or future value deserializes
    /// as an unknown enum member. Eligibility is positive (only Default is interactive) precisely so
    /// such a value cannot slip past "not ReviewFlow and not Review" and be honoured as interactive —
    /// which would also skip the bridge-defeating warning, since that predicate requires Default.</summary>
    [Test]
    public async Task Posture_on_an_unknown_launch_kind_fails_closed() {
        var reason = CodexPosturePolicy.RejectionReason(
            Cmd(kind: (LaunchKind)99, posture: new("danger-full-access", "never")));

        await Assert.That(reason).StartsWith("codex_posture_not_overridable:");
    }

    /// A borrowed review-flow launch violates BOTH invariants; the reason names one of them
    /// rather than reporting nothing.
    [Test]
    public async Task Posture_on_borrowed_review_flow_still_fails_closed() {
        var reason = CodexPosturePolicy.RejectionReason(
            Cmd(kind: LaunchKind.ReviewFlow, borrowed: true, posture: new("workspace-write", "never")));

        await Assert.That(reason).StartsWith("codex_posture_not_overridable:");
    }

    // === Token validation ===

    [Test]
    [Arguments("full-access", "on-request")]       // unknown sandbox
    [Arguments("Read-Only", "on-request")]         // mixed case: rejected, never normalized
    [Arguments("read-only", "Never")]              // mixed case on the approval axis
    [Arguments("workspace-write", "on-failure")]   // deprecated upstream
    [Arguments("workspace-write", "always")]       // unknown approval
    [Arguments("", "on-request")]                  // partial: empty sandbox
    [Arguments("read-only", "")]                   // partial: empty approval
    [Arguments("  ", "on-request")]                // partial: whitespace sandbox
    public async Task Invalid_tokens_and_partial_blocks_are_rejected(string sandbox, string approval) {
        var reason = CodexPosturePolicy.RejectionReason(Cmd(posture: new(sandbox, approval)));

        await Assert.That(reason).StartsWith("codex_posture_invalid:");
    }

    [Test]
    public async Task On_failure_rejection_names_the_upstream_deprecation() {
        var reason = CodexPosturePolicy.RejectionReason(Cmd(posture: new("workspace-write", "on-failure")));

        await Assert.That(reason).Contains("deprecated");
    }

    // === Effective resolution ===

    [Test]
    public async Task Resolve_returns_selected_pair_when_posture_present() {
        var pair = CodexPosturePolicy.Resolve(WorkLocation.OwnedWorktree, false, new("read-only", "never"));

        await Assert.That(pair).IsEqualTo(("read-only", "never"));
    }

    [Test]
    public async Task Resolve_returns_derived_pairs_when_posture_absent() {
        await Assert.That(CodexPosturePolicy.Resolve(WorkLocation.OwnedWorktree, false, null))
            .IsEqualTo(("workspace-write", "on-request"));
        await Assert.That(CodexPosturePolicy.Resolve(WorkLocation.OwnedWorktree, true, null))
            .IsEqualTo(("workspace-write", "never"));
        await Assert.That(CodexPosturePolicy.Resolve(WorkLocation.BorrowedCwd, true, null))
            .IsEqualTo(("read-only", "never"));
        await Assert.That(CodexPosturePolicy.Resolve(WorkLocation.BorrowedCwd, false, null))
            .IsEqualTo(("read-only", "on-request"));
    }
}
