using Capacitor.Cli.Daemon.Harness.Codex;

namespace Capacitor.Cli.Daemon.Tests.Unit.Harness.Codex;

public class CodexAppServerPostureTests {
    [Test]
    public async Task ReadOnly_renders_type_only_no_writable_roots() {
        var json = CodexAppServerPosture.RenderSandboxPolicy("read-only", ["/tmp/wt"]).ToJsonString();
        await Assert.That(json).IsEqualTo("{\"type\":\"readOnly\"}");
    }

    [Test]
    public async Task WorkspaceWrite_renders_writable_roots() {
        var json = CodexAppServerPosture.RenderSandboxPolicy("workspace-write", ["/tmp/wt"]).ToJsonString();
        await Assert.That(json).IsEqualTo("{\"type\":\"workspaceWrite\",\"writableRoots\":[\"/tmp/wt\"]}");
    }

    [Test]
    public async Task WorkspaceWrite_renders_multiple_roots_in_order() {
        var json = CodexAppServerPosture.RenderSandboxPolicy("workspace-write", ["/a", "/b"]).ToJsonString();
        await Assert.That(json).IsEqualTo("{\"type\":\"workspaceWrite\",\"writableRoots\":[\"/a\",\"/b\"]}");
    }

    [Test]
    public async Task WorkspaceWrite_with_no_roots_emits_empty_array() {
        var json = CodexAppServerPosture.RenderSandboxPolicy("workspace-write", []).ToJsonString();
        await Assert.That(json).IsEqualTo("{\"type\":\"workspaceWrite\",\"writableRoots\":[]}");
    }

    [Test]
    public async Task DangerFullAccess_renders_type_only() {
        var json = CodexAppServerPosture.RenderSandboxPolicy("danger-full-access", []).ToJsonString();
        await Assert.That(json).IsEqualTo("{\"type\":\"dangerFullAccess\"}");
    }

    [Test]
    [Arguments("full")]
    [Arguments("read_only")]
    [Arguments("")]
    public async Task Unknown_sandbox_throws(string sandbox) {
        await Assert.That(() => CodexAppServerPosture.RenderSandboxPolicy(sandbox, []))
            .Throws<ArgumentOutOfRangeException>();
    }

    [Test]
    [Arguments("never")]
    [Arguments("on-request")]
    [Arguments("untrusted")]
    public async Task Known_approval_returns_verbatim(string approval) {
        await Assert.That(CodexAppServerPosture.RenderApprovalPolicy(approval)).IsEqualTo(approval);
    }

    [Test]
    [Arguments("on-failure")]   // deprecated in Codex; must not slip through
    [Arguments("auto_review")]
    [Arguments("")]
    public async Task Unknown_approval_throws(string approval) {
        await Assert.That(() => CodexAppServerPosture.RenderApprovalPolicy(approval))
            .Throws<ArgumentOutOfRangeException>();
    }

    [Test]
    public async Task Approvals_reviewer_is_pinned_to_user() {
        // The Guardian containment pin: approvals always route to the client, never Codex's
        // own auto_review subagent.
        await Assert.That(CodexAppServerPosture.ApprovalsReviewer).IsEqualTo("user");
    }
}
