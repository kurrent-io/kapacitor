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
                "http://127.0.0.1:1234/0123456789abcdef0123456789abcdef"
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
    /// load-bearing half of this assertion, not decoration.
    ///
    /// <para>Pins the borrowed lane against the broadening that follows it: brokered delivery is now
    /// keyed on <c>RequiresBrokeredResultDelivery</c>, and the orchestrator derives that from a
    /// superset of borrowed-ness — a borrowed snapshot must still land here.</para></summary>
    [Test]
    public async Task Borrowed_snapshot_result_channel_uses_capability_not_ambient_credential() {
        var servers = AcpReviewFlowMcp.Build(Context() with {
            IsBorrowedSnapshot = true,
            RequiresBrokeredResultDelivery = true,
            ReviewContextCapabilityUrl =
                "http://127.0.0.1:1234/0123456789abcdef0123456789abcdef/review-context/workspace-mcp-configs",
            FlowResultCapabilityUrl =
                "http://127.0.0.1:1234/0123456789abcdef0123456789abcdef"
        }, []);

        var channel = servers.Single(server => server.Name == "kcap-flow-result");
        await Assert.That(channel.Env.Select(pair => pair.Name))
            .IsEquivalentTo(["KCAP_FLOW_CAPABILITY_URL", "KCAP_FLOW_AGENT_ID"]);
        await Assert.That(channel.Env.Single(pair => pair.Name == "KCAP_FLOW_CAPABILITY_URL").Value)
            .IsEqualTo("http://127.0.0.1:1234/0123456789abcdef0123456789abcdef");
    }

    /// <summary>
    /// The reviewer this broadening exists for: it borrows nothing (its workspace is a daemon-owned
    /// worktree) and yet its HOME is a per-launch isolated directory, so its result channel resolves
    /// PathHelpers.ConfigDir at an empty tree and cannot authenticate. Keying delivery on
    /// borrowed-ness left this launch on the KCAP_URL path, which fails at the delivery step with
    /// "Not logged in" after the reviewer has already done its work.
    ///
    /// <para>The ABSENCE of KCAP_URL is again the load-bearing half — the two are mutually exclusive
    /// so the broken ambient-credential path is not reachable as a silent fallback.</para>
    /// </summary>
    [Test]
    public async Task Home_redirected_launch_uses_capability_even_though_it_borrows_nothing() {
        var servers = AcpReviewFlowMcp.Build(Context() with {
            RequiresBrokeredResultDelivery = true,
            FlowResultCapabilityUrl =
                "http://127.0.0.1:1234/0123456789abcdef0123456789abcdef"
        }, []);

        var channel = servers.Single(server => server.Name == "kcap-flow-result");
        await Assert.That(channel.Env.Select(pair => pair.Name))
            .IsEquivalentTo(["KCAP_FLOW_CAPABILITY_URL", "KCAP_FLOW_AGENT_ID"]);
        await Assert.That(channel.Env.Single(pair => pair.Name == "KCAP_FLOW_CAPABILITY_URL").Value)
            .IsEqualTo("http://127.0.0.1:1234/0123456789abcdef0123456789abcdef");
        // Not a borrowed launch, so no review-context server either: the broadening moves delivery
        // only, and must not drag the snapshot's read capability along with it.
        await Assert.That(servers.Any(server => server.Name == "kcap-review-context")).IsFalse();
    }

    /// <summary>The reviewer whose HOME is the daemon user's own keeps the ambient-credential path:
    /// its token store resolves, and nothing about the redirected case applies. Pins that the fix is
    /// scoped rather than silently rerouting every reviewer through the daemon.</summary>
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
                RequiresBrokeredResultDelivery = true,
                ReviewContextCapabilityUrl =
                    "http://127.0.0.1:1234/0123456789abcdef0123456789abcdef/review-context/workspace-mcp-configs"
            }, []));
        await Assert.That(ex.Message).Contains("missing result capability URL");
    }

    /// <summary>The fail-closed shape survives the broadening: a HOME-redirected launch with no
    /// capability must fail loudly rather than fall back to KCAP_URL, which is precisely the path it
    /// cannot use. Non-borrowed, so it can only be reached through the broadened condition.</summary>
    [Test]
    public async Task Home_redirected_launch_without_result_capability_fails_before_launch() {
        var ex = Assert.Throws<InvalidOperationException>(() =>
            AcpReviewFlowMcp.Build(Context() with { RequiresBrokeredResultDelivery = true }, []));

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
                RequiresBrokeredResultDelivery = true,
                FlowResultCapabilityUrl =
                    "http://127.0.0.1:1234/0123456789abcdef0123456789abcdef"
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
