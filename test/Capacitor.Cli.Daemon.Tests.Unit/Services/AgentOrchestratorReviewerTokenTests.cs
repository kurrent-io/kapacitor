using Capacitor.Cli.Core;
using Capacitor.Cli.Core.LocalIpc;
using Capacitor.Cli.Daemon.Services;
using Capacitor.Cli.Daemon.Tests.Unit.Pty;

namespace Capacitor.Cli.Daemon.Tests.Unit.Services;

/// <summary>
/// An unattended review-flow launch mints a per-reviewer LocalPermissionBridge token (bound to its
/// read-only allowlist) and revokes it on teardown — but ONLY for Codex reviewers (Claude runs via
/// bypassPermissions and needs none). An allowlist with a non-auto-approvable server fails the launch
/// fast. Reuses <see cref="AgentOrchestratorHarness"/>. The bridge's request
/// classification is covered exhaustively by <see cref="LocalPermissionBridgeTests"/>; these assert
/// the orchestrator WIRING via <c>ReviewerTokenCountForTest</c> so they needn't do real HTTP.
/// </summary>
public class AgentOrchestratorReviewerTokenTests {
    // Starts a real bridge (binds a loopback port) → serialize with the other port-binding tests.
    [Test, NotInParallel("LocalPermissionBridgeTests")]
    public async Task ReviewFlow_codex_launch_mints_a_reviewer_token_and_revokes_it_on_cleanup() {
        var (repoPath, cleanup) = GitRepoHarness.CreateGitRepo();

        try {
            var server     = new CaptureServerConnection();
            var ptyFactory = new FixedPtyProcessFactory(new OneChunkThenBlockPtyProcess());

            await using var orch   = AgentOrchestratorHarness.BuildOrchestrator(server, ptyFactory, AgentOrchestratorHarness.Launcher("codex"), allowedRepoPath: repoPath);
            var             bridge = orch.PermissionBridgeForTest;
            await bridge.StartAsync(CancellationToken.None);

            try {
                await orch.HandleLaunchAgentForTest(new LaunchAgentCommand(
                    AgentId: "rev-1", Prompt: "review", Model: "default", Effort: null,
                    RepoPath: repoPath, Tools: null, AttachmentIds: null, Vendor: "codex",
                    Kind: LaunchKind.ReviewFlow, McpAllowlist: ["kcap-review"]));

                var agent = orch.GetAgentForTest("rev-1");
                await Assert.That(agent).IsNotNull();
                await Assert.That(agent!.ReviewerBridgeToken).IsNotNull();
                await Assert.That(agent.ReviewerBridgeToken).IsNotEqualTo(bridge.BaseUrl);   // a dedicated token
                await Assert.That(bridge.ReviewerTokenCountForTest).IsEqualTo(1);

                // Teardown revokes the token, closing the auto-approve window.
                await orch.CleanupAgentForTest("rev-1");
                await Assert.That(bridge.ReviewerTokenCountForTest).IsEqualTo(0);
            } finally {
                await bridge.DisposeAsync();
            }
        } finally {
            cleanup();
        }
    }

    // A GENERIC flow participant (arbitrary FlowRole from start_flow, not a review-flow reviewer)
    // launches through the same LaunchKind.ReviewFlow lane, so it gets the same reserved-channel
    // treatment: a dedicated bridge token is minted even with no MCP allowlist at all (the reserved
    // result channel is injected independently of the allowlist, and the bridge auto-approves its
    // unattended-safe tools on any participant token — see
    // LocalPermissionBridgeTests.Reviewer_token_with_empty_allowlist_still_auto_approves_reserved_channel_tools).
    [Test, NotInParallel("LocalPermissionBridgeTests")]
    public async Task Generic_flow_participant_codex_launch_mints_a_reviewer_token_like_a_reviewer() {
        var (repoPath, cleanup) = GitRepoHarness.CreateGitRepo();

        try {
            var server     = new CaptureServerConnection();
            var ptyFactory = new FixedPtyProcessFactory(new OneChunkThenBlockPtyProcess());

            await using var orch   = AgentOrchestratorHarness.BuildOrchestrator(server, ptyFactory, AgentOrchestratorHarness.Launcher("codex"), allowedRepoPath: repoPath);
            var             bridge = orch.PermissionBridgeForTest;
            await bridge.StartAsync(CancellationToken.None);

            try {
                await orch.HandleLaunchAgentForTest(new LaunchAgentCommand(
                    AgentId: "part-1", Prompt: "research the topic", Model: "default", Effort: null,
                    RepoPath: repoPath, Tools: null, AttachmentIds: null, Vendor: "codex",
                    Kind: LaunchKind.ReviewFlow, McpAllowlist: null,
                    FlowRunId: "flow-run-1", FlowRole: "researcher"));

                var agent = orch.GetAgentForTest("part-1");
                await Assert.That(agent).IsNotNull();
                await Assert.That(agent!.ReviewerBridgeToken).IsNotNull();
                await Assert.That(agent.ReviewerBridgeToken).IsNotEqualTo(bridge.BaseUrl);   // a dedicated token
                await Assert.That(bridge.ReviewerTokenCountForTest).IsEqualTo(1);
            } finally {
                await bridge.DisposeAsync();
            }
        } finally {
            cleanup();
        }
    }

    // Defense in depth: a non-Codex reviewer (Claude) is NOT minted a token, even for a ReviewFlow —
    // its config-lock doesn't apply, so a bare tool name wouldn't be provably a kcap tool.
    [Test, NotInParallel("LocalPermissionBridgeTests")]
    public async Task ReviewFlow_non_codex_launch_mints_no_reviewer_token() {
        var (repoPath, cleanup) = GitRepoHarness.CreateGitRepo();

        try {
            var server     = new CaptureServerConnection();
            var ptyFactory = new FixedPtyProcessFactory(new OneChunkThenBlockPtyProcess());

            await using var orch   = AgentOrchestratorHarness.BuildOrchestrator(server, ptyFactory, AgentOrchestratorHarness.Launcher("claude"), allowedRepoPath: repoPath);
            var             bridge = orch.PermissionBridgeForTest;
            await bridge.StartAsync(CancellationToken.None);

            try {
                await orch.HandleLaunchAgentForTest(new LaunchAgentCommand(
                    AgentId: "rev-claude", Prompt: "review", Model: "opus", Effort: null,
                    RepoPath: repoPath, Tools: null, AttachmentIds: null, Vendor: "claude",
                    Kind: LaunchKind.ReviewFlow, McpAllowlist: ["kcap-review"]));

                var agent = orch.GetAgentForTest("rev-claude");
                await Assert.That(agent).IsNotNull();
                await Assert.That(agent!.ReviewerBridgeToken).IsNull();
                await Assert.That(bridge.ReviewerTokenCountForTest).IsEqualTo(0);
            } finally {
                await bridge.DisposeAsync();
            }
        } finally {
            cleanup();
        }
    }

    // No bridge started (no port bind): a Default launch never mints a reviewer token regardless.
    [Test]
    public async Task Default_launch_uses_the_shared_token_no_reviewer_token() {
        var (repoPath, cleanup) = GitRepoHarness.CreateGitRepo();

        try {
            var server     = new CaptureServerConnection();
            var ptyFactory = new FixedPtyProcessFactory(new OneChunkThenBlockPtyProcess());

            await using var orch = AgentOrchestratorHarness.BuildOrchestrator(server, ptyFactory, AgentOrchestratorHarness.Launcher("claude"), allowedRepoPath: repoPath);

            await orch.HandleLaunchAgentForTest(new LaunchAgentCommand(
                AgentId: "def-1", Prompt: "work", Model: "opus", Effort: null,
                RepoPath: repoPath, Tools: null, AttachmentIds: null, Vendor: "claude"));

            var agent = orch.GetAgentForTest("def-1");
            await Assert.That(agent).IsNotNull();
            await Assert.That(agent!.ReviewerBridgeToken).IsNull();
        } finally {
            cleanup();
        }
    }

    [Test, NotInParallel("LocalPermissionBridgeTests")]
    public async Task ReviewFlow_codex_launch_with_non_auto_approvable_allowlist_fails_fast() {
        var (repoPath, cleanup) = GitRepoHarness.CreateGitRepo();

        try {
            var server     = new CaptureServerConnection();
            var ptyFactory = new SpyPtyProcessFactory();

            await using var orch   = AgentOrchestratorHarness.BuildOrchestrator(server, ptyFactory, AgentOrchestratorHarness.Launcher("codex"), allowedRepoPath: repoPath);
            var             bridge = orch.PermissionBridgeForTest;
            await bridge.StartAsync(CancellationToken.None);   // BaseUrl must be non-null so the mint/validate runs

            try {
                await orch.HandleLaunchAgentForTest(new LaunchAgentCommand(
                    AgentId: "rev-bad", Prompt: "review", Model: "default", Effort: null,
                    RepoPath: repoPath, Tools: null, AttachmentIds: null, Vendor: "codex",
                    Kind: LaunchKind.ReviewFlow, McpAllowlist: ["kcap-memory"]));   // write server → not auto-approvable

                await Assert.That(server.LaunchFailedCalls.Count).IsEqualTo(1);
                await Assert.That(server.LaunchFailedCalls[0].AgentId).IsEqualTo("rev-bad");
                await Assert.That(server.LaunchFailedCalls[0].Reason).Contains("not auto-approvable");
                // Failed fast: no PTY spawned, no agent registered, no reviewer token left behind.
                await Assert.That(ptyFactory.SpawnCalls).IsEqualTo(0);
                await Assert.That(orch.GetAgentForTest("rev-bad")).IsNull();
                await Assert.That(bridge.ReviewerTokenCountForTest).IsEqualTo(0);
            } finally {
                await bridge.DisposeAsync();
            }
        } finally {
            cleanup();
        }
    }

    /// <summary>
    /// A runtime that declares <c>ReviewFlowRedirectsHome</c> gets the daemon-brokered delivery
    /// capability even though it borrows NOTHING — the case a borrowed-ness predicate cannot reach.
    /// Its result channel resolves the token store from HOME, and this launch's HOME is a per-launch
    /// isolated directory, so the ambient-credential path fails at delivery with "Not logged in"
    /// after the reviewer has already done the work.
    ///
    /// <para>The URL identity assertion is the load-bearing half: the capability must be the reviewer
    /// GRANT's own URL, so revoking the reviewer closes the submit path in the same operation.</para>
    /// </summary>
    [Test, NotInParallel("LocalPermissionBridgeTests")]
    public async Task ReviewFlow_home_redirecting_runtime_is_minted_a_delivery_capability_without_borrowing() {
        var (repoPath, cleanup) = GitRepoHarness.CreateGitRepo();

        try {
            var server  = new CaptureServerConnection();
            var factory = new SpyHostedAgentRuntimeFactory("antigravity") {
                SupportsUnattended      = true,
                ReviewFlowRedirectsHome = true
            };

            await using var orch = AgentOrchestratorHarness.BuildOrchestrator(
                server, new SpyPtyProcessFactory(), new Dictionary<string, IHostedAgentLauncher>(),
                allowedRepoPath: repoPath, extraRuntimeFactories: [factory]);
            var bridge = orch.PermissionBridgeForTest;
            await bridge.StartAsync(CancellationToken.None);

            try {
                await orch.HandleLaunchAgentForTest(new LaunchAgentCommand(
                    AgentId: "rev-agy", Prompt: "review", Model: "default", Effort: null,
                    RepoPath: repoPath, Tools: null, AttachmentIds: null, Vendor: "antigravity",
                    Kind: LaunchKind.ReviewFlow, McpAllowlist: ["kcap-review"]));

                var ctx = factory.LastContext;
                await Assert.That(ctx).IsNotNull();
                // Borrows nothing — this is exactly the launch shape #488's predicate misses.
                await Assert.That(ctx!.IsBorrowedSnapshot).IsFalse();
                await Assert.That(ctx.Work).IsEqualTo(WorkLocation.OwnedWorktree);

                await Assert.That(ctx.RequiresBrokeredResultDelivery).IsTrue();
                await Assert.That(ctx.FlowResultCapabilityUrl).IsNotNull();

                var agent = orch.GetAgentForTest("rev-agy");
                await Assert.That(agent!.ReviewerBridgeToken).IsEqualTo(ctx.FlowResultCapabilityUrl);
                await Assert.That(bridge.ReviewerTokenCountForTest).IsEqualTo(1);

                // The grant carries a SUBMIT FORWARDER, not merely a URL. A grant minted without one
                // 404s that endpoint by design, so a not-404 is the only thing that distinguishes a
                // capability that can deliver from a URL that can only fail. It is not a 200 either:
                // this orchestrator's ServerUrl is an unreachable loopback port, so the forwarder
                // runs and faults — which is the bridge's 500, and is proof it ran at all.
                using var client = new HttpClient();
                var submit = await client.PostAsync($"{ctx.FlowResultCapabilityUrl}/flow-result",
                    new StringContent("{\"kind\":\"clean\"}", System.Text.Encoding.UTF8, "application/json"));
                await Assert.That(submit.StatusCode).IsNotEqualTo(System.Net.HttpStatusCode.NotFound);

                // The capability dies with the reviewer: one revocation closes the submit path.
                await orch.CleanupAgentForTest("rev-agy");
                await Assert.That(bridge.ReviewerTokenCountForTest).IsEqualTo(0);
            } finally {
                await bridge.DisposeAsync();
            }
        } finally {
            cleanup();
        }
    }

    /// <summary>The negative control for the broadening: a runtime that does NOT redirect HOME keeps
    /// the ambient-credential path and is minted nothing. Same vendor-neutral spy as above, so the
    /// only difference between the two is the declaration itself.</summary>
    [Test, NotInParallel("LocalPermissionBridgeTests")]
    public async Task ReviewFlow_runtime_that_keeps_its_home_is_minted_no_delivery_capability() {
        var (repoPath, cleanup) = GitRepoHarness.CreateGitRepo();

        try {
            var server  = new CaptureServerConnection();
            var factory = new SpyHostedAgentRuntimeFactory("antigravity") { SupportsUnattended = true };

            await using var orch = AgentOrchestratorHarness.BuildOrchestrator(
                server, new SpyPtyProcessFactory(), new Dictionary<string, IHostedAgentLauncher>(),
                allowedRepoPath: repoPath, extraRuntimeFactories: [factory]);
            var bridge = orch.PermissionBridgeForTest;
            await bridge.StartAsync(CancellationToken.None);

            try {
                await orch.HandleLaunchAgentForTest(new LaunchAgentCommand(
                    AgentId: "rev-plain", Prompt: "review", Model: "default", Effort: null,
                    RepoPath: repoPath, Tools: null, AttachmentIds: null, Vendor: "antigravity",
                    Kind: LaunchKind.ReviewFlow, McpAllowlist: ["kcap-review"]));

                var ctx = factory.LastContext;
                await Assert.That(ctx).IsNotNull();
                await Assert.That(ctx!.RequiresBrokeredResultDelivery).IsFalse();
                await Assert.That(ctx.FlowResultCapabilityUrl).IsNull();
                await Assert.That(bridge.ReviewerTokenCountForTest).IsEqualTo(0);
            } finally {
                await bridge.DisposeAsync();
            }
        } finally {
            cleanup();
        }
    }

    // Secrecy: the reviewer token rides in KCAP_DAEMON_URL, so it must be in PtyEnvScrub's scrub
    // list — otherwise it could leak into a recorded/child env another hosted agent reads.
    [Test]
    public async Task Reviewer_token_env_var_KCAP_DAEMON_URL_is_scrubbed() {
        await Assert.That(Capacitor.Cli.Daemon.Pty.PtyEnvScrub.HostedAgentVars).Contains("KCAP_DAEMON_URL");
    }
}
