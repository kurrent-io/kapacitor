using Capacitor.Cli.Daemon.Services;

namespace Capacitor.Cli.Tests.Unit.Services;

public class AcpReviewFlowMcpContextTests {
    [Test]
    public async Task Borrowed_snapshot_injects_reserved_context_server_with_only_local_env() {
        var servers = AcpReviewFlowMcp.Build(Context() with {
            IsBorrowedSnapshot = true,
            ReviewContextCapabilityUrl =
                "http://127.0.0.1:1234/0123456789abcdef0123456789abcdef/review-context/workspace-mcp-configs",
            // A borrowed launch now requires BOTH capabilities, so this must be present to
            // reach the context assertions at all.
            FlowResultCapabilityUrl =
                "http://127.0.0.1:1234/0123456789abcdef0123456789abcdef/flow-result"
        }, ["kcap-review"]);

        var context = servers.Single(server => server.Name == "kcap-review-context");
        await Assert.That(context.Command).IsEqualTo("/usr/local/bin/kcap");
        await Assert.That(context.Args).IsEquivalentTo(["mcp", "review"]);
        await Assert.That(context.Env.Select(pair => pair.Name)).IsEquivalentTo([
            "KCAP_REVIEW_CONTEXT_MODE", "KCAP_REVIEW_CONTEXT_URL"]);
        await Assert.That(context.Env.Any(pair => pair.Name == "KCAP_URL")).IsFalse();
    }

    /// <summary>A borrowed reviewer's HOME is redirected to a per-launch state dir, so the
    /// result channel cannot load the token store — it must be handed a daemon-minted capability
    /// instead. KCAP_URL is the thing that drives the ambient-credential path, so its ABSENCE is the
    /// load-bearing half of this assertion, not decoration.</summary>
    [Test]
    public async Task Borrowed_snapshot_result_channel_uses_capability_not_ambient_credential() {
        var servers = AcpReviewFlowMcp.Build(Context() with {
            IsBorrowedSnapshot = true,
            ReviewContextCapabilityUrl =
                "http://127.0.0.1:1234/0123456789abcdef0123456789abcdef/review-context/workspace-mcp-configs",
            FlowResultCapabilityUrl =
                "http://127.0.0.1:1234/0123456789abcdef0123456789abcdef/flow-result"
        }, []);

        var channel = servers.Single(server => server.Name == "kcap-flow-result");
        await Assert.That(channel.Env.Select(pair => pair.Name))
            .IsEquivalentTo(["KCAP_FLOW_RESULT_URL", "KCAP_FLOW_AGENT_ID"]);
        await Assert.That(channel.Env.Single(pair => pair.Name == "KCAP_FLOW_RESULT_URL").Value)
            .IsEqualTo("http://127.0.0.1:1234/0123456789abcdef0123456789abcdef/flow-result");
    }

    /// <summary>The non-borrowed launch keeps the ambient-credential path: its HOME is the real one,
    /// the token store resolves, and nothing about the sandboxed case applies. Pins that the fix is scoped to
    /// the sandboxed case rather than silently rerouting every reviewer.</summary>
    [Test]
    public async Task Direct_review_result_channel_keeps_server_url() {
        var servers = AcpReviewFlowMcp.Build(Context(), []);

        var channel = servers.Single(server => server.Name == "kcap-flow-result");
        await Assert.That(channel.Env.Select(pair => pair.Name))
            .IsEquivalentTo(["KCAP_URL", "KCAP_FLOW_AGENT_ID"]);
    }

    /// <summary>Same fail-before-launch shape as the context capability: a borrowed launch whose
    /// result channel has no capability can never report, and that failure is otherwise silent —
    /// the reviewer runs to completion and its answer is discarded at the delivery step.</summary>
    [Test]
    public async Task Snapshot_without_result_capability_fails_before_launch() {
        var ex = Assert.Throws<InvalidOperationException>(() =>
            AcpReviewFlowMcp.Build(Context() with {
                IsBorrowedSnapshot = true,
                ReviewContextCapabilityUrl =
                    "http://127.0.0.1:1234/0123456789abcdef0123456789abcdef/review-context/workspace-mcp-configs"
            }, []));
        await Assert.That(ex.Message).Contains("missing result capability URL");
    }

    [Test]
    public async Task Direct_review_does_not_inject_context_server() {
        var servers = AcpReviewFlowMcp.Build(Context() with {
            ReviewContextCapabilityUrl =
                "http://127.0.0.1:1234/0123456789abcdef0123456789abcdef/review-context/workspace-mcp-configs"
        }, []);
        await Assert.That(servers.Any(server => server.Name == "kcap-review-context")).IsFalse();
    }

    [Test]
    public async Task Snapshot_without_context_capability_fails_before_launch() {
        var ex = Assert.Throws<InvalidOperationException>(() =>
            AcpReviewFlowMcp.Build(Context() with {
                IsBorrowedSnapshot = true,
                // Supplied so the result-capability guard passes and this test still
                // exercises the CONTEXT guard it was written for, rather than the new one.
                FlowResultCapabilityUrl =
                    "http://127.0.0.1:1234/0123456789abcdef0123456789abcdef/flow-result"
            }, []));
        await Assert.That(ex.Message).Contains("missing capability URL");
    }

    static RuntimeStartContext Context() => new(
        AgentId: "agent", Vendor: "cursor", SourceRepoPath: "/repo",
        Worktree: new WorktreeInfo("/snapshot", "", "/repo", true, SnapshotRoot: "/snapshot"),
        Prompt: "review", Model: null, Effort: null, Tools: null,
        IsReview: false, IsReviewFlow: true, Review: null,
        Cols: 80, Rows: 24, ServerUrl: "http://kcap.test",
        DaemonBridgeUrl: null, CapacitorPath: "/usr/local/bin/kcap");
}
