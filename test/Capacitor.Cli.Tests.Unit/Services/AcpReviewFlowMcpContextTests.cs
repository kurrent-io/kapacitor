using Capacitor.Cli.Daemon.Services;

namespace Capacitor.Cli.Tests.Unit.Services;

public class AcpReviewFlowMcpContextTests {
    [Test]
    public async Task Borrowed_snapshot_injects_reserved_context_server_with_only_local_env() {
        var servers = AcpReviewFlowMcp.Build(Context() with {
            IsBorrowedSnapshot = true,
            ReviewContextCapabilityUrl =
                "http://127.0.0.1:1234/0123456789abcdef0123456789abcdef/review-context/workspace-mcp-configs"
        }, ["kcap-review"]);

        var context = servers.Single(server => server.Name == "kcap-review-context");
        await Assert.That(context.Command).IsEqualTo("/usr/local/bin/kcap");
        await Assert.That(context.Args).IsEquivalentTo(["mcp", "review"]);
        await Assert.That(context.Env.Select(pair => pair.Name)).IsEquivalentTo([
            "KCAP_REVIEW_CONTEXT_MODE", "KCAP_REVIEW_CONTEXT_URL"]);
        await Assert.That(context.Env.Any(pair => pair.Name == "KCAP_URL")).IsFalse();
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
            AcpReviewFlowMcp.Build(Context() with { IsBorrowedSnapshot = true }, []));
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
