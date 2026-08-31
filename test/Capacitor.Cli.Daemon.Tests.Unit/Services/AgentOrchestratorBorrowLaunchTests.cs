using Capacitor.Cli.Core;
using Capacitor.Cli.Core.LocalIpc;
using Capacitor.Cli.Daemon.Harness.Claude;
using Capacitor.Cli.Daemon.Harness.Codex;
using Capacitor.Cli.Daemon.Services;
using Capacitor.Cli.Daemon.Tests.Unit.Pty;
using Microsoft.Extensions.Logging.Abstractions;

namespace Capacitor.Cli.Daemon.Tests.Unit.Services;

/// <summary>
/// The borrowed-launch branch in <c>AgentOrchestrator.HandleLaunchAgent</c>, and the failed-launch
/// cleanup guard alongside it — a launch failure must not undo what the borrowed path deliberately
/// left alone.
///
/// THE TOP SAFETY INVARIANT: a borrowed cwd is the user's REAL checkout. It must NEVER be
/// removed / <c>git worktree remove</c>d / branch-deleted on ANY path (normal stop, failed
/// launch, anywhere). These tests lock that in behaviourally.
///
/// Reuses the shared <see cref="AgentOrchestratorHarness"/>
/// (<c>BuildOrchestrator</c>, <c>CreateGitRepo</c>, <c>CaptureServerConnection</c>,
/// <c>SpyPtyProcessFactory</c>, <c>FixedPtyProcessFactory</c>, <c>OneChunkThenBlockPtyProcess</c>,
/// <c>SpyHostedAgentLauncher</c>).
/// </summary>
[ParallelLimiter<SubprocessLimit>]
public class AgentOrchestratorBorrowLaunchTests {
    [TempHome] public required TempHome Home { get; init; }

    // The bridge's loopback listener can still refuse a connection while coming up, so the manifest
    // is fetched with a bound rather than once.
    static async Task<string> FetchAsync(HttpClient client, string url) {
        var deadline = DateTime.UtcNow + WaitHarness.Bounded;
        while (true) {
            try { return await client.GetStringAsync(url); }
            catch (HttpRequestException) when (DateTime.UtcNow < deadline) { await Task.Delay(50); }
        }
    }

    [Test]
    public async Task Borrowed_Claude_review_flow_fails_at_runtime_boundary_without_spawning() {
        using var cwd = GitRepo.CreateWithCommit();
        var server = new CaptureServerConnection();
        var ptyFactory = new SpyPtyProcessFactory();
        var launcher = new ClaudeLauncher(
            new DaemonConfig { ClaudePath = "spy-claude", ServerUrl = "http://127.0.0.1:1" },
            Home,
            NullLogger<ClaudeLauncher>.Instance);
        await using var orch = AgentOrchestratorHarness.BuildOrchestrator(server, ptyFactory,
            new Dictionary<string, IHostedAgentLauncher> { ["claude"] = launcher });
        var cmd = new LaunchAgentCommand(
            "agent-borrowed-review", "review", "default", null, cwd, null, null,
            Vendor: "claude", Kind: LaunchKind.ReviewFlow, Borrowed: true, BorrowCwd: cwd);

        await orch.HandleLaunchAgentForTest(cmd);

        await Assert.That(server.LaunchFailedCalls).Count().IsEqualTo(1);
        await Assert.That(server.LaunchFailedCalls[0].Reason)
            .Contains("not certified for 'claude'");
        await Assert.That(ptyFactory.SpawnCalls).IsEqualTo(0);
        await Assert.That(Directory.Exists(cwd)).IsTrue();

    }

    [Test]
    public async Task Borrowed_Cursor_review_flow_runs_in_owned_dirty_snapshot_and_refreshes_between_rounds() {
        using var cwd = GitRepo.CreateWithCommit();
        LocalPermissionBridge? bridge = null;
        try {
            File.WriteAllText(Path.Combine(cwd, "README.md"), "dirty-one");
            File.WriteAllText(Path.Combine(cwd, "untracked.txt"), "untracked-one");
            var firstContext = "{\"mcpServers\":{\"first\":{}}}";
            File.WriteAllText(Path.Combine(cwd, ".mcp.json"), firstContext);
            cwd.Do("add", ".mcp.json");
            var server = new CaptureServerConnection();
            var factory = new SpyHostedAgentRuntimeFactory("cursor") {
                SupportsUnattended = true,
                SupportsBorrowedReviewFlow = true,
                BorrowedReviewRequiresIndependentSnapshot = true
            };
            await using var orch = AgentOrchestratorHarness.BuildOrchestrator(
                server, new SpyPtyProcessFactory(),
                new Dictionary<string, IHostedAgentLauncher>(),
                extraRuntimeFactories: [factory]);
            bridge = orch.PermissionBridgeForTest;
            await bridge.StartAsync(CancellationToken.None);
            var cmd = new LaunchAgentCommand(
                "agent-cursor-snapshot", "review", "default", null, cwd, null, null,
                Vendor: "cursor", Kind: LaunchKind.ReviewFlow, Borrowed: true, BorrowCwd: cwd);

            await orch.HandleLaunchAgentForTest(cmd);

            var ctx = factory.LastContext!;
            await Assert.That(ctx.Work).IsEqualTo(WorkLocation.OwnedWorktree);
            await Assert.That(ctx.Worktree.Path).IsNotEqualTo(cwd);
            await Assert.That(File.ReadAllText(Path.Combine(ctx.Worktree.Path, "README.md")))
                .IsEqualTo("dirty-one");
            await Assert.That(File.ReadAllText(Path.Combine(ctx.Worktree.Path, "untracked.txt")))
                .IsEqualTo("untracked-one");
            await Assert.That(File.Exists(Path.Combine(ctx.Worktree.Path, ".mcp.json"))).IsFalse();
            await Assert.That(ctx.ReviewContextCapabilityUrl).IsNotNull();
            // The result channel's delivery capability rides the SAME reviewer grant as the context
            // capability, so one revocation closes both. A separately-minted token could outlive the
            // first and leave a live submit path after the reviewer is gone — which is also why the
            // token count below stays at 1.
            await Assert.That(ctx.FlowResultCapabilityUrl).IsNotNull();
            await Assert.That(ctx.FlowResultCapabilityUrl!)
                .IsEqualTo(ctx.ReviewContextCapabilityUrl!.Split("/review-context")[0]);
            await Assert.That(bridge.ReviewerTokenCountForTest).IsEqualTo(1);
            await Assert.That(orch.GetAgentForTest(cmd.AgentId)!.BorrowedSnapshotSource)
                .IsEqualTo(BorrowAuthorizer.Canonicalize(cwd));

            using var client = new HttpClient();
            var firstManifest = await FetchAsync(client, ctx.ReviewContextCapabilityUrl!);
            await Assert.That(firstManifest)
                .Contains(Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(firstContext)));
            var retiredGenerationPath = ctx.Worktree.ReviewContextGeneration!.StoragePath;

            File.WriteAllText(Path.Combine(cwd, "README.md"), "dirty-two");
            var secondContext = "{\"mcpServers\":{\"second\":{}}}";
            File.WriteAllText(Path.Combine(cwd, ".mcp.json"), secondContext);
            cwd.Do("add", ".mcp.json");
            await orch.HandleSendInputForTest(new SendInputCommand(cmd.AgentId, "next", null));
            await Assert.That(File.ReadAllText(Path.Combine(ctx.Worktree.Path, "README.md")))
                .IsEqualTo("dirty-two");
            var secondManifest = await FetchAsync(client, ctx.ReviewContextCapabilityUrl!);
            await Assert.That(secondManifest)
                .Contains(Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(secondContext)));
            await Assert.That(secondManifest).DoesNotContain(
                Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(firstContext)));
            await Assert.That(Directory.Exists(retiredGenerationPath)).IsFalse();

            var sidecarRoot = ctx.Worktree.ReviewContextRoot!;
            await orch.HandleStopAgentForTest(cmd.AgentId);
            for (var i = 0; i < 100 && Directory.Exists(ctx.Worktree.Path); i++)
                await Task.Delay(20);
            await Assert.That(Directory.Exists(cwd)).IsTrue();
            await Assert.That(Directory.Exists(ctx.Worktree.Path)).IsFalse();
            await Assert.That(Directory.Exists(sidecarRoot)).IsFalse();
            await Assert.That(bridge.ReviewerTokenCountForTest).IsEqualTo(0);
        } finally {
            if (bridge is not null) await bridge.DisposeAsync();
        }
    }

    [Test, NotInParallel("LocalPermissionBridgeTests")]
    public async Task Non_review_independent_snapshot_gets_context_grant_and_refreshes() {
        using var cwd = GitRepo.CreateWithCommit();
        LocalPermissionBridge? bridge = null;
        try {
            var firstContext = "{\"mcpServers\":{\"first\":{}}}";
            File.WriteAllText(Path.Combine(cwd, ".mcp.json"), firstContext);
            cwd.Do("add", ".mcp.json");
            var server = new CaptureServerConnection();
            var factory = new SpyHostedAgentRuntimeFactory("cursor") {
                SupportsBorrowedReviewFlow = true,
                BorrowedReviewRequiresIndependentSnapshot = true
            };
            await using var orch = AgentOrchestratorHarness.BuildOrchestrator(
                server, new SpyPtyProcessFactory(),
                new Dictionary<string, IHostedAgentLauncher>(),
                extraRuntimeFactories: [factory]);
            bridge = orch.PermissionBridgeForTest;
            await bridge.StartAsync(CancellationToken.None);
            var command = new LaunchAgentCommand(
                "agent-cursor-non-review-snapshot", "work", "default", null, cwd, null, null,
                Vendor: "cursor", Kind: LaunchKind.Default, Borrowed: true, BorrowCwd: cwd);

            await orch.HandleLaunchAgentForTest(command);

            var context = factory.LastContext!;
            var runtime = factory.LastRuntime!;
            await Assert.That(server.LaunchFailedCalls).IsEmpty();
            await Assert.That(context.ReviewContextCapabilityUrl).IsNotNull();
            await Assert.That(bridge.ReviewerTokenCountForTest).IsEqualTo(1);

            var secondContext = "{\"mcpServers\":{\"second\":{}}}";
            File.WriteAllText(Path.Combine(cwd, ".mcp.json"), secondContext);
            cwd.Do("add", ".mcp.json");
            await orch.HandleSendInputForTest(new SendInputCommand(command.AgentId, "next", null));

            await Assert.That(runtime.HasExited).IsFalse();
            using var client = new HttpClient();
            var manifest = await FetchAsync(client, context.ReviewContextCapabilityUrl!);
            await Assert.That(manifest).Contains(Convert.ToBase64String(
                System.Text.Encoding.UTF8.GetBytes(secondContext)));

            await orch.HandleStopAgentForTest(command.AgentId);
            for (var i = 0; i < 100 && bridge.ReviewerTokenCountForTest != 0; i++)
                await Task.Delay(20);
            await Assert.That(bridge.ReviewerTokenCountForTest).IsEqualTo(0);
        } finally {
            if (bridge is not null) await bridge.DisposeAsync();
        }
    }

    [Test, NotInParallel("LocalPermissionBridgeTests")]
    public async Task Snapshot_launch_failure_revokes_context_grant_and_removes_sidecar() {
        using var cwd = GitRepo.CreateWithCommit();
        LocalPermissionBridge? bridge = null;
        try {
            var server = new CaptureServerConnection();
            var factory = new SpyHostedAgentRuntimeFactory("cursor") {
                SupportsUnattended = true,
                SupportsBorrowedReviewFlow = true,
                BorrowedReviewRequiresIndependentSnapshot = true,
                StartThrow = new InvalidOperationException("synthetic launch failure")
            };
            await using var orch = AgentOrchestratorHarness.BuildOrchestrator(
                server, new SpyPtyProcessFactory(),
                new Dictionary<string, IHostedAgentLauncher>(),
                extraRuntimeFactories: [factory]);
            bridge = orch.PermissionBridgeForTest;
            await bridge.StartAsync(CancellationToken.None);

            await orch.HandleLaunchAgentForTest(new LaunchAgentCommand(
                "agent-context-fail", "review", "default", null, cwd, null, null,
                Vendor: "cursor", Kind: LaunchKind.ReviewFlow,
                Borrowed: true, BorrowCwd: cwd));

            var context = factory.LastContext!;
            await Assert.That(server.LaunchFailedCalls.Count).IsEqualTo(1);
            await Assert.That(orch.GetAgentForTest("agent-context-fail")).IsNull();
            await Assert.That(bridge.ReviewerTokenCountForTest).IsEqualTo(0);
            await Assert.That(Directory.Exists(context.Worktree.SnapshotRoot!)).IsFalse();
            await Assert.That(Directory.Exists(context.Worktree.ReviewContextRoot!)).IsFalse();
        } finally {
            if (bridge is not null) await bridge.DisposeAsync();
        }
    }

    [Test, NotInParallel("LocalPermissionBridgeTests")]
    public async Task Snapshot_refresh_context_failure_terminates_reviewer_and_cleans_capability() {
        using var cwd = GitRepo.CreateWithCommit();
        LocalPermissionBridge? bridge = null;
        try {
            var server = new CaptureServerConnection();
            var factory = new SpyHostedAgentRuntimeFactory("cursor") {
                SupportsUnattended = true,
                SupportsBorrowedReviewFlow = true,
                BorrowedReviewRequiresIndependentSnapshot = true
            };
            await using var orch = AgentOrchestratorHarness.BuildOrchestrator(
                server, new SpyPtyProcessFactory(),
                new Dictionary<string, IHostedAgentLauncher>(),
                extraRuntimeFactories: [factory]);
            bridge = orch.PermissionBridgeForTest;
            await bridge.StartAsync(CancellationToken.None);
            var command = new LaunchAgentCommand(
                "agent-context-refresh-fail", "review", "default", null, cwd, null, null,
                Vendor: "cursor", Kind: LaunchKind.ReviewFlow,
                Borrowed: true, BorrowCwd: cwd);
            await orch.HandleLaunchAgentForTest(command);
            var context = factory.LastContext!;
            var runtime = factory.LastRuntime!;

            Directory.CreateDirectory(Path.Combine(cwd, ".mcp.json"));
            File.WriteAllText(Path.Combine(cwd, ".mcp.json", "child"), "unsafe");
            cwd.Do("add", ".mcp.json/child");
            await orch.HandleSendInputForTest(new SendInputCommand(command.AgentId, "next", null));

            await Assert.That(runtime.HasExited).IsTrue();
            for (var i = 0; i < 100 &&
                    (Directory.Exists(context.Worktree.SnapshotRoot!) ||
                     bridge.ReviewerTokenCountForTest != 0); i++)
                await Task.Delay(20);
            await Assert.That(bridge.ReviewerTokenCountForTest).IsEqualTo(0);
            await Assert.That(Directory.Exists(context.Worktree.SnapshotRoot!)).IsFalse();
            await Assert.That(Directory.Exists(context.Worktree.ReviewContextRoot!)).IsFalse();
        } finally {
            if (bridge is not null) await bridge.DisposeAsync();
        }
    }

    // ── Borrowed-snapshot regression net (two distinct checkouts) ─────────────────────────
    // docs/superpowers/specs/2026-07-27-ai1528-trust-by-default-borrowed-review-design.md
    //
    // TWO checkouts are mandatory. The orchestrator snapshots the borrow cwd's own canonical git
    // root, deliberately INDEPENDENT of the launch command's registered RepoPath, so a test pointing
    // both at one directory would satisfy every content assertion below while proving nothing about
    // which checkout was selected — it would pass just as happily against the stale-base behavior
    // this test exists to prevent. So: RepoPath is the DAEMON checkout, the borrow cwd is the
    // REQUESTER checkout, and the load-bearing assertion is the snapshot's SourceRepo.

    [Test]
    public async Task Borrowed_snapshot_is_built_from_the_requester_checkout_not_the_registered_repo() {
        using var daemonRepo = GitRepo.CreateWithCommit();
        using var requesterRepo = GitRepo.CreateWithCommit();
        LocalPermissionBridge? bridge = null;
        try {
            // The daemon-registered checkout is a decoy: everything in it is distinguishable from
            // the requester's, so any content leaking from it is caught by value, not by absence.
            File.WriteAllText(Path.Combine(daemonRepo, "README.md"), "daemon-checkout-decoy");
            File.WriteAllText(Path.Combine(daemonRepo, "daemon-only.txt"), "daemon-only");
            daemonRepo.Do("add", "-A");
            daemonRepo.Do("commit", "-q", "-m", "daemon decoy");

            // The requester carries all four shapes the snapshot has to get right.
            requesterRepo.Do("checkout", "-q", "-b", "feature");
            File.WriteAllText(Path.Combine(requesterRepo, "branch-only.txt"), "branch-only-committed");
            requesterRepo.Do("add", "-A");
            requesterRepo.Do("commit", "-q", "-m", "branch-only commit");
            File.WriteAllText(Path.Combine(requesterRepo, "README.md"), "requester-modified");
            File.WriteAllText(Path.Combine(requesterRepo, "untracked.txt"), "requester-untracked");
            File.WriteAllText(Path.Combine(requesterRepo, ".gitignore"), "ignored.txt\n");
            File.WriteAllText(Path.Combine(requesterRepo, "ignored.txt"), "must-not-be-snapshotted");

            var canonicalRequester = BorrowAuthorizer.Canonicalize(requesterRepo);
            var canonicalDaemon    = BorrowAuthorizer.Canonicalize(daemonRepo);

            var server  = new CaptureServerConnection();
            var factory = new SpyHostedAgentRuntimeFactory("cursor") {
                SupportsUnattended = true,
                SupportsBorrowedReviewFlow = true,
                BorrowedReviewRequiresIndependentSnapshot = true
            };
            await using var orch = AgentOrchestratorHarness.BuildOrchestrator(
                server, new SpyPtyProcessFactory(),
                new Dictionary<string, IHostedAgentLauncher>(),
                extraRuntimeFactories: [factory]);
            bridge = orch.PermissionBridgeForTest;
            await bridge.StartAsync(CancellationToken.None);

            var baseline = ContentBaseline(canonicalRequester);
            var gitState = GitState(canonicalRequester);

            var cmd = new LaunchAgentCommand(
                "agent-two-checkout", "review", "default", null,
                RepoPath: daemonRepo, null, null,
                Vendor: "cursor", Kind: LaunchKind.ReviewFlow,
                Borrowed: true, BorrowCwd: requesterRepo);

            await orch.HandleLaunchAgentForTest(cmd);

            await Assert.That(server.LaunchFailedCalls).IsEmpty();
            var ctx = factory.LastContext!;

            // (1) THE assertion that pins the fix: the snapshot was derived from the requester's git
            // root, not the registered one. Everything below is secondary to this.
            await Assert.That(ctx.Worktree.SourceRepo).IsEqualTo(canonicalRequester);
            await Assert.That(ctx.Worktree.SourceRepo).IsNotEqualTo(canonicalDaemon);
            await Assert.That(orch.GetAgentForTest(cmd.AgentId)!.BorrowedSnapshotSource)
                .IsEqualTo(canonicalRequester);

            // (2) Contents: branch-only commit, the MODIFIED tracked file, and the untracked file —
            // none of which exist in the registered checkout — and not the ignored file.
            var snapshot = ctx.Worktree.Path;
            await Assert.That(File.ReadAllText(Path.Combine(snapshot, "branch-only.txt")))
                .IsEqualTo("branch-only-committed");
            await Assert.That(File.ReadAllText(Path.Combine(snapshot, "README.md")))
                .IsEqualTo("requester-modified");
            await Assert.That(File.ReadAllText(Path.Combine(snapshot, "untracked.txt")))
                .IsEqualTo("requester-untracked");
            await Assert.That(File.Exists(Path.Combine(snapshot, "ignored.txt"))).IsFalse();
            await Assert.That(File.Exists(Path.Combine(snapshot, "daemon-only.txt"))).IsFalse();

            // (3) The snapshot root is outside the source repo (not a linked worktree under it).
            await Assert.That(snapshot).IsNotEqualTo(canonicalRequester);
            await Assert.That(snapshot.StartsWith(
                canonicalRequester.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar,
                StringComparison.Ordinal)).IsFalse();

            // Baseline check 1 of 4 — after snapshot creation.
            await AssertBaselineUnchanged(canonicalRequester, baseline, gitState, "after snapshot creation");

            // (4) Reviewer mutation stays inside the snapshot.
            File.WriteAllText(Path.Combine(snapshot, "README.md"), "reviewer-clobbered");
            File.WriteAllText(Path.Combine(snapshot, "reviewer-created.txt"), "reviewer dropping");
            File.WriteAllText(Path.Combine(snapshot, ".git", "reviewer-metadata"), "reviewer dropping");
            // The droppings must genuinely exist first, or the "they disappear" assertions below
            // would be vacuously true.
            await Assert.That(File.Exists(Path.Combine(snapshot, "reviewer-created.txt"))).IsTrue();
            await Assert.That(File.Exists(Path.Combine(snapshot, ".git", "reviewer-metadata"))).IsTrue();

            // Baseline check 2 of 4 — after reviewer mutation.
            await AssertBaselineUnchanged(canonicalRequester, baseline, gitState, "after reviewer mutation");

            // (5) Per-round refresh: the requester's new content appears, and every reviewer-only file
            // AND its git metadata disappear — asserting only that the reviewer's writes never reached
            // the requester would pass against a refresh that accumulates droppings round after round.
            File.WriteAllText(Path.Combine(requesterRepo, "README.md"), "requester-modified-again");
            var refreshedBaseline = ContentBaseline(canonicalRequester);
            var refreshedGitState = GitState(canonicalRequester);
            // The deliberate edit is the ONLY difference — so re-basing the baseline here cannot
            // launder a corruption that happened in between.
            await Assert.That(BaselineDifferences(baseline, refreshedBaseline)).IsEquivalentTo(["README.md"]);

            await orch.HandleSendInputForTest(new SendInputCommand(cmd.AgentId, "next", null));

            await Assert.That(File.ReadAllText(Path.Combine(snapshot, "README.md")))
                .IsEqualTo("requester-modified-again");
            await Assert.That(File.Exists(Path.Combine(snapshot, "reviewer-created.txt"))).IsFalse();
            await Assert.That(File.Exists(Path.Combine(snapshot, ".git", "reviewer-metadata"))).IsFalse();
            await Assert.That(File.ReadAllText(Path.Combine(snapshot, "branch-only.txt")))
                .IsEqualTo("branch-only-committed");
            await Assert.That(File.Exists(Path.Combine(snapshot, "ignored.txt"))).IsFalse();

            // Baseline check 3 of 4 — after refresh.
            await AssertBaselineUnchanged(canonicalRequester, refreshedBaseline, refreshedGitState, "after refresh");

            // (6) Stop removes the daemon-owned snapshot and leaves the requester intact.
            await orch.HandleStopAgentForTest(cmd.AgentId);
            for (var i = 0; i < 100 && Directory.Exists(snapshot); i++) await Task.Delay(20);
            await Assert.That(Directory.Exists(snapshot)).IsFalse();
            await Assert.That(Directory.Exists(canonicalRequester)).IsTrue();

            // Baseline check 4 of 4 — after stop.
            await AssertBaselineUnchanged(canonicalRequester, refreshedBaseline, refreshedGitState, "after stop");
        } finally {
            if (bridge is not null) await bridge.DisposeAsync();
        }
    }

    /// <summary>
    /// The Copilot mirror of the two-checkout snapshot test above.
    ///
    /// <para>Both other borrowed-launch cases construct <c>SpyHostedAgentRuntimeFactory("cursor")</c>,
    /// so nothing else pins that a borrowed COPILOT launch materializes an owned snapshot from the
    /// REQUESTER's checkout and refreshes it between rounds. It is listed separately from the Cursor
    /// read probe on purpose: the two catch different regressions, so discharging one must not read as
    /// discharging both.</para>
    ///
    /// <para>Two checkouts are mandatory here for the same reason as above — the orchestrator snapshots
    /// the borrow cwd's own canonical git root, independent of the launch command's registered
    /// <c>RepoPath</c>, so pointing both at one directory would satisfy every content assertion while
    /// proving nothing about which checkout was selected.</para>
    /// </summary>
    [Test]
    public async Task Borrowed_Copilot_review_snapshots_the_requester_checkout_and_refreshes_between_rounds() {
        using var daemonRepo = GitRepo.CreateWithCommit();
        using var requesterRepo = GitRepo.CreateWithCommit();
        LocalPermissionBridge? bridge = null;
        try {
            // The registered checkout is a decoy: its content is distinguishable, so a leak is caught
            // by value rather than by absence.
            File.WriteAllText(Path.Combine(daemonRepo, "README.md"), "daemon-checkout-decoy");
            File.WriteAllText(Path.Combine(daemonRepo, "daemon-only.txt"), "daemon-only");
            daemonRepo.Do("add", "-A");
            daemonRepo.Do("commit", "-q", "-m", "daemon decoy");

            // All three classes a borrowed reviewer has to be able to see.
            requesterRepo.Do("checkout", "-q", "-b", "feature");
            File.WriteAllText(Path.Combine(requesterRepo, "branch-only.txt"), "branch-only-committed");
            requesterRepo.Do("add", "-A");
            requesterRepo.Do("commit", "-q", "-m", "branch-only commit");
            File.WriteAllText(Path.Combine(requesterRepo, "README.md"), "requester-modified");
            File.WriteAllText(Path.Combine(requesterRepo, "untracked.txt"), "requester-untracked");

            var canonicalRequester = BorrowAuthorizer.Canonicalize(requesterRepo);
            var canonicalDaemon    = BorrowAuthorizer.Canonicalize(daemonRepo);

            var server  = new CaptureServerConnection();
            var factory = new SpyHostedAgentRuntimeFactory("copilot") {
                SupportsUnattended = true,
                SupportsBorrowedReviewFlow = true,
                BorrowedReviewRequiresIndependentSnapshot = true
            };
            await using var orch = AgentOrchestratorHarness.BuildOrchestrator(
                server, new SpyPtyProcessFactory(),
                new Dictionary<string, IHostedAgentLauncher>(),
                extraRuntimeFactories: [factory]);
            bridge = orch.PermissionBridgeForTest;
            await bridge.StartAsync(CancellationToken.None);

            var baseline = ContentBaseline(canonicalRequester);
            var gitState = GitState(canonicalRequester);

            var cmd = new LaunchAgentCommand(
                "agent-copilot-snapshot", "review", "default", null,
                RepoPath: daemonRepo, null, null,
                Vendor: "copilot", Kind: LaunchKind.ReviewFlow,
                Borrowed: true, BorrowCwd: requesterRepo);

            await orch.HandleLaunchAgentForTest(cmd);

            await Assert.That(server.LaunchFailedCalls).IsEmpty();
            var ctx = factory.LastContext!;

            // (1) THE assertion that pins the fix: derived from the requester's git root, not the
            // registered one. Content assertions alone pass against the stale-base behaviour.
            await Assert.That(ctx.Worktree.SourceRepo).IsEqualTo(canonicalRequester);
            await Assert.That(ctx.Worktree.SourceRepo).IsNotEqualTo(canonicalDaemon);
            await Assert.That(orch.GetAgentForTest(cmd.AgentId)!.BorrowedSnapshotSource)
                .IsEqualTo(canonicalRequester);

            // (2) The reviewer runs in a daemon-owned snapshot, not the user's checkout, and the
            // borrowed-snapshot marker is what selects the readable argv and the OS sandbox.
            await Assert.That(ctx.Work).IsEqualTo(WorkLocation.OwnedWorktree);
            await Assert.That(ctx.IsBorrowedSnapshot).IsTrue();
            await Assert.That(ctx.Worktree.Path).IsNotEqualTo(canonicalRequester);

            // (3) All three content classes present; the decoy's own file absent.
            var snapshot = ctx.Worktree.Path;
            await Assert.That(File.ReadAllText(Path.Combine(snapshot, "branch-only.txt")))
                .IsEqualTo("branch-only-committed");
            await Assert.That(File.ReadAllText(Path.Combine(snapshot, "README.md")))
                .IsEqualTo("requester-modified");
            await Assert.That(File.ReadAllText(Path.Combine(snapshot, "untracked.txt")))
                .IsEqualTo("requester-untracked");
            await Assert.That(File.Exists(Path.Combine(snapshot, "daemon-only.txt"))).IsFalse();

            await AssertBaselineUnchanged(canonicalRequester, baseline, gitState, "after snapshot creation");

            // (4) Per-round refresh brings new requester content in and reviewer droppings out.
            File.WriteAllText(Path.Combine(snapshot, "reviewer-created.txt"), "reviewer dropping");
            await Assert.That(File.Exists(Path.Combine(snapshot, "reviewer-created.txt"))).IsTrue();
            File.WriteAllText(Path.Combine(requesterRepo, "README.md"), "requester-modified-again");
            var refreshedBaseline = ContentBaseline(canonicalRequester);
            var refreshedGitState = GitState(canonicalRequester);

            await orch.HandleSendInputForTest(new SendInputCommand(cmd.AgentId, "next", null));

            await Assert.That(File.ReadAllText(Path.Combine(snapshot, "README.md")))
                .IsEqualTo("requester-modified-again");
            await Assert.That(File.Exists(Path.Combine(snapshot, "reviewer-created.txt"))).IsFalse();

            await AssertBaselineUnchanged(canonicalRequester, refreshedBaseline, refreshedGitState, "after refresh");

            // (5) Stop removes the daemon-owned snapshot AND the per-launch vendor state directory —
            // the latter holds the reviewer's whole HOME for the launch and must not outlive it — while
            // leaving the requester's checkout intact.
            var stateRoot = WorktreeManager.VendorStateRootFor(ctx.Worktree.SnapshotRoot ?? snapshot);
            Directory.CreateDirectory(Path.Combine(stateRoot, "home"));
            File.WriteAllText(Path.Combine(stateRoot, "home", "vendor-state.json"), "{}");

            await orch.HandleStopAgentForTest(cmd.AgentId);
            for (var i = 0; i < 100 && Directory.Exists(snapshot); i++) await Task.Delay(20);

            await Assert.That(Directory.Exists(snapshot)).IsFalse();
            await Assert.That(Directory.Exists(stateRoot)).IsFalse()
                .Because("the per-launch vendor state directory must not outlive the launch");
            await Assert.That(Directory.Exists(canonicalRequester)).IsTrue();

            await AssertBaselineUnchanged(canonicalRequester, refreshedBaseline, refreshedGitState, "after stop");
        } finally {
            if (bridge is not null) await bridge.DisposeAsync();
        }
    }

    // ── Borrowed launch runs in the user's cwd and creates no daemon worktree ─────────────

    [Test]
    public async Task Borrowed_launch_creates_no_worktree_and_runs_in_the_cwd() {
        using var cwd = GitRepo.CreateWithCommit();

        var server     = new CaptureServerConnection();
        // A blocking PTY keeps the agent registered so we can inspect Work/Worktree before cleanup.
        var ptyFactory = new FixedPtyProcessFactory(new OneChunkThenBlockPtyProcess());
        var claudeSpy  = new SpyHostedAgentLauncher("claude", cliPath: "spy-claude");
        var launchers  = new Dictionary<string, IHostedAgentLauncher> { ["claude"] = claudeSpy };

        // Empty allowlist ⇒ BorrowAuthorizer authorizes any local git repo (allow-all-repos).
        await using var orch = AgentOrchestratorHarness.BuildOrchestrator(server, ptyFactory, launchers);

        var before = SnapshotTree(cwd);

        var cmd = new LaunchAgentCommand(
            AgentId: "agent-borrow-1",
            Prompt: "do work",
            Model: "opus",
            Effort: null,
            RepoPath: cwd,
            Tools: null,
            AttachmentIds: ["would-be-attachment"], // set so we prove the attachment download-into-cwd is skipped
            Vendor: "claude",
            Borrowed: true,
            BorrowCwd: cwd
        );

        await orch.HandleLaunchAgentForTest(cmd);

        var canonicalCwd = BorrowAuthorizer.Canonicalize(cwd);

        // No daemon-owned worktree was created under the user's checkout...
        await Assert.That(Directory.Exists(Path.Combine(cwd, ".capacitor", "worktrees"))).IsFalse();
        // ...no attachments were downloaded into it...
        await Assert.That(Directory.Exists(Path.Combine(cwd, ".attached"))).IsFalse();
        await Assert.That(Directory.Exists(Path.Combine(canonicalCwd, ".attached"))).IsFalse();
        // ...and the cwd tree is byte-identical (no worktree add, no launch-time mirror, no attachment).
        await Assert.That(SnapshotTree(cwd)).IsEquivalentTo(before);

        // The agent runs in the user's real (canonicalized) checkout, marked as a borrowed cwd.
        var agent = orch.GetAgentForTest("agent-borrow-1");
        await Assert.That(agent).IsNotNull();
        await Assert.That(agent!.Work).IsEqualTo(WorkLocation.BorrowedCwd);
        await Assert.That(agent.Worktree.Path).IsEqualTo(canonicalCwd);

        // Clean stop (also exercises the normal-stop cleanup guard for a borrowed agent).
        await orch.HandleStopAgentForTest("agent-borrow-1");
        await Assert.That(Directory.Exists(cwd)).IsTrue();

    }

    // ── Launch-time re-authorization fails loudly, leaving the cwd untouched ──────────────

    [Test]
    public async Task Borrowed_launch_reauth_failure_fails_loudly() {
        // RepoPath is an allowed git repo (passes the early repo-allowed/exists guards); the borrow
        // cwd is a NON-git directory, which the authorizer rejects under an empty allowlist.
        using var repoPath = GitRepo.CreateWithCommit();
        using var tmpBorrow = new TempDir();
        tmpBorrow.CreateFile("user-file.txt", "precious");
        var borrowCwd = tmpBorrow.Path;

        var server     = new CaptureServerConnection();
        var ptyFactory = new SpyPtyProcessFactory();
        var claudeSpy  = new SpyHostedAgentLauncher("claude", cliPath: "spy-claude");
        var launchers  = new Dictionary<string, IHostedAgentLauncher> { ["claude"] = claudeSpy };

        await using var orch = AgentOrchestratorHarness.BuildOrchestrator(server, ptyFactory, launchers);

        var before = SnapshotTree(borrowCwd);

        var cmd = new LaunchAgentCommand(
            AgentId: "agent-borrow-auth",
            Prompt: "do work",
            Model: "opus",
            Effort: null,
            RepoPath: repoPath,
            Tools: null,
            AttachmentIds: null,
            Vendor: "claude",
            Borrowed: true,
            BorrowCwd: borrowCwd
        );

        await orch.HandleLaunchAgentForTest(cmd);

        // Fails loudly with the machine-readable prefix the server keys off.
        await Assert.That(server.LaunchFailedCalls.Count).IsEqualTo(1);
        await Assert.That(server.LaunchFailedCalls[0].AgentId).IsEqualTo("agent-borrow-auth");
        await Assert.That(server.LaunchFailedCalls[0].Reason).Contains("borrow_auth_failed");

        // No PTY ever spawned, and the user's directory is byte-identical (nothing created/removed).
        await Assert.That(ptyFactory.SpawnCalls).IsEqualTo(0);
        await Assert.That(Directory.Exists(borrowCwd)).IsTrue();
        await Assert.That(SnapshotTree(borrowCwd)).IsEquivalentTo(before);
        await Assert.That(File.ReadAllText(Path.Combine(borrowCwd, "user-file.txt"))).IsEqualTo("precious");

    }

    // ── SAFETY: a failed borrowed launch must NOT remove the user's checkout ──────────────

    [Test]
    public async Task Failed_borrowed_launch_does_not_remove_the_cwd() {
        // The borrow cwd is a *linked* git worktree — the realistic danger: `git worktree remove`
        // succeeds on a linked worktree and would silently delete the user's checkout. The launch
        // fails AFTER the borrowed worktree is assigned (runtime StartAsync throws), reaching the
        // failed-launch cleanup, which must gate the removal on OwnedWorktree rather than
        // git-worktree-removing whatever it was handed.
        var (_, linkedCwd, cleanup) = CreateLinkedWorktree();

        var server          = new CaptureServerConnection();
        var ptyFactory      = new SpyPtyProcessFactory();
        var throwingFactory = new ThrowingHostedAgentRuntimeFactory("boomvendor", "kaboom during start");

        // No launcher for the vendor; the throwing runtime factory is injected directly.
        await using var orch = AgentOrchestratorHarness.BuildOrchestrator(
            server,
            ptyFactory,
            new Dictionary<string, IHostedAgentLauncher>(),
            extraRuntimeFactories: [throwingFactory]
        );

        // Capture BEFORE the launch: Canonicalize falls back to the lexical path once the dir is
        // gone, so a post-failure recompute would mask a deletion instead of exposing it.
        var canonicalCwd = BorrowAuthorizer.Canonicalize(linkedCwd);
        var before       = SnapshotTree(linkedCwd);

        var cmd = new LaunchAgentCommand(
            AgentId: "agent-borrow-fail",
            Prompt: "do work",
            Model: "opus",
            Effort: null,
            RepoPath: linkedCwd,
            Tools: null,
            AttachmentIds: null,
            Vendor: "boomvendor",
            Borrowed: true,
            BorrowCwd: linkedCwd
        );

        await orch.HandleLaunchAgentForTest(cmd);

        // SAFETY (asserted first, clearest): the user's REAL checkout SURVIVES, byte-identical.
        // Without the OwnedWorktree gate, the failed-launch cleanup would `git worktree remove`
        // it — a linked worktree accepts that removal silently.
        await Assert.That(Directory.Exists(linkedCwd)).IsTrue();
        await Assert.That(SnapshotTree(linkedCwd)).IsEquivalentTo(before);
        // The launch failed, and the runtime had received the borrowed (canonicalized) cwd.
        await Assert.That(server.LaunchFailedCalls.Count).IsEqualTo(1);
        await Assert.That(throwingFactory.LastWorktreePath).IsEqualTo(canonicalCwd);

    }

    // ── Regression: the OWNED path still creates a worktree and removes it on failure ──────

    [Test]
    public async Task Owned_launch_still_creates_and_on_failure_removes_the_worktree() {
        using var repoPath = GitRepo.CreateWithCommit();

        var server          = new CaptureServerConnection();
        var ptyFactory      = new SpyPtyProcessFactory();
        var throwingFactory = new ThrowingHostedAgentRuntimeFactory("boomvendor", "kaboom during start");

        await using var orch = AgentOrchestratorHarness.BuildOrchestrator(
            server,
            ptyFactory,
            new Dictionary<string, IHostedAgentLauncher>(),
            allowedRepoPath: repoPath,
            extraRuntimeFactories: [throwingFactory]
        );

        var cmd = new LaunchAgentCommand(
            AgentId: "agent-owned-fail",
            Prompt: "do work",
            Model: "opus",
            Effort: null,
            RepoPath: repoPath,
            Tools: null,
            AttachmentIds: null,
            Vendor: "boomvendor",
            Borrowed: false
        );

        await orch.HandleLaunchAgentForTest(cmd);

        // The launch failed after a daemon-OWNED worktree was created...
        await Assert.That(server.LaunchFailedCalls.Count).IsEqualTo(1);
        await Assert.That(throwingFactory.LastWorktreePath).IsNotNull();
        // ...under the repo's .capacitor/worktrees...
        await Assert.That(throwingFactory.LastWorktreePath!)
            .StartsWith(Path.Combine(repoPath, ".capacitor", "worktrees"));
        // ...and the failed-launch cleanup removed it (owned behaviour unchanged).
        await Assert.That(Directory.Exists(throwingFactory.LastWorktreePath!)).IsFalse();

    }

    // ── Helpers / test doubles ─────────────────────────────────────────────────────────────

    /// <summary>
    /// A CONTENT baseline: relative path → SHA-256 of the file's bytes (plus its unix mode, where the
    /// platform has one, since the snapshot builder preserves modes). Deliberately not
    /// <see cref="SnapshotTree"/>, which records entry NAMES only and would miss in-place corruption
    /// of a file it still sees listed.
    ///
    /// <para><c>.git</c> is excluded on purpose. Building a snapshot runs read-only git plumbing
    /// (<c>bundle create</c>, <c>ls-files</c>) inside the source checkout, which may legitimately
    /// refresh index stat metadata — hashing that would make the baseline flap for a reason unrelated
    /// to the invariant. Git-state integrity is asserted separately and more meaningfully by
    /// <see cref="GitState"/> (HEAD sha + porcelain status).</para>
    /// </summary>
    static SortedDictionary<string, string> ContentBaseline(string root) {
        var baseline = new SortedDictionary<string, string>(StringComparer.Ordinal);
        foreach (var path in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories)) {
            var rel = Path.GetRelativePath(root, path).Replace(Path.DirectorySeparatorChar, '/');
            if (rel == ".git" || rel.StartsWith(".git/", StringComparison.Ordinal)) continue;

            var hash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(path)));
            var mode = OperatingSystem.IsWindows() ? "" : $" mode={File.GetUnixFileMode(path)}";
            baseline[rel] = hash + mode;
        }

        return baseline;
    }

    /// <summary>Relative paths whose content/mode differs between two baselines, including paths
    /// present in only one of them.</summary>
    static List<string> BaselineDifferences(
            SortedDictionary<string, string> before, SortedDictionary<string, string> after) =>
        before.Keys.Union(after.Keys, StringComparer.Ordinal)
            .Where(k => !before.TryGetValue(k, out var b) ||
                        !after.TryGetValue(k, out var a) ||
                        !string.Equals(b, a, StringComparison.Ordinal))
            .OrderBy(k => k, StringComparer.Ordinal)
            .ToList();

    /// <summary>HEAD sha + porcelain status — the requester's git state, asserted alongside the
    /// content baseline (which skips <c>.git</c>) so a reviewer write into the source repository's
    /// git directory could not pass unnoticed.</summary>
    static string GitState(string repo) =>
        GitRepo.At(repo).Try("rev-parse", "HEAD").Text + "\n" + GitRepo.At(repo).Try("status", "--porcelain").Text;

    static async Task AssertBaselineUnchanged(
            string root, SortedDictionary<string, string> expected, string expectedGitState, string when) {
        await Assert.That(BaselineDifferences(expected, ContentBaseline(root)))
            .IsEmpty().Because($"the requester checkout must be byte-identical {when}");
        await Assert.That(GitState(root))
            .IsEqualTo(expectedGitState).Because($"the requester's git state must be unchanged {when}");
    }

    /// <summary>Sorted list of every file-system entry (relative path) under <paramref name="root"/>,
    /// so a before/after comparison catches ANY addition or removal in the user's tree.</summary>
    static List<string> SnapshotTree(string root) =>
        Directory.EnumerateFileSystemEntries(root, "*", SearchOption.AllDirectories)
            .Select(p => Path.GetRelativePath(root, p))
            .OrderBy(p => p, StringComparer.Ordinal)
            .ToList();

    /// <summary>Creates a main git repo plus a linked worktree checked out from it, returning the
    /// linked worktree path — a realistic "borrow the user's git worktree" cwd.</summary>
    static (string mainRepo, string linkedCwd, Action cleanup) CreateLinkedWorktree() {
        // Ownership leaves with the returned cleanup, so this one is deliberately not a `using`.
        var mainRepo  = GitRepo.CreateWithCommit();
        var tmp       = new TempDir();
        var linkedCwd = tmp.PathTo("linked");

        mainRepo.AddWorktree(linkedCwd);

        return (mainRepo.Path, linkedCwd, () => {
            tmp.Dispose();
            mainRepo.Dispose();
        });
    }

    /// <summary>A runtime factory that records the worktree it was handed then throws a generic
    /// (non-<see cref="CodexHooksNotInstalledException"/>) failure, driving the launch into the
    /// main failed-launch cleanup path AFTER the worktree is assigned.</summary>
    sealed class ThrowingHostedAgentRuntimeFactory(string vendor, string message) : IHostedAgentRuntimeFactory {
    public string CliPath => "unused-by-this-double";
        public string  Vendor            { get; }              = vendor;
        public bool    SupportsUnattended                       => true;
        public string? LastWorktreePath  { get; private set; }

        public bool IsAvailable() => true;

        public Task<HostedRuntimeStart> StartAsync(RuntimeStartContext ctx, CancellationToken ct) {
            LastWorktreePath = ctx.Worktree.Path;

            throw new InvalidOperationException(message);
        }
    }
}
