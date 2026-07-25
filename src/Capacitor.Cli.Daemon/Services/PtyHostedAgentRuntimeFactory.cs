using Capacitor.Cli.Core.Commands;
using Capacitor.Cli.Daemon.Pty;
using Microsoft.Extensions.Logging;

namespace Capacitor.Cli.Daemon.Services;

/// <summary>
/// <see cref="IHostedAgentRuntimeFactory"/> for the interactive PTY-backed CLIs (Claude, Codex).
/// Moved verbatim from the body of <see cref="AgentOrchestrator.HandleLaunchAgent"/> (Task
/// 10): builds the <see cref="LauncherContext"/> (including the review-launch
/// <see cref="ReviewLaunchBuilder.BuildAsync"/> call), runs <see cref="IHostedAgentLauncher.Prepare"/>
/// then <see cref="IHostedAgentLauncher.BuildArgs"/>, assembles the daemon-injected env vars, spawns
/// the PTY via the injected <see cref="IPtyProcessFactory"/> (the same instance
/// <c>SpyPtyProcessFactory</c> asserts against in the vendor-routing tests), and wraps the result in
/// a <see cref="PtyHostedAgentRuntime"/>.
///
/// <see cref="CodexHooksNotInstalledException"/> from <c>Prepare</c> is intentionally left
/// unhandled here — it propagates to the orchestrator, which maps it to <c>LaunchFailed</c> +
/// worktree cleanup. Any other <c>Prepare</c> failure is soft-logged and swallowed so a
/// settings-overlay glitch never blocks launch (same contract as before the refactor,
/// <c>AgentOrchestrator.LogPrepareSoftFailure</c>).
/// </summary>
internal sealed partial class PtyHostedAgentRuntimeFactory(
        IHostedAgentLauncher                  launcher,
        IPtyProcessFactory                    ptyFactory,
        ILogger<PtyHostedAgentRuntimeFactory> logger
    ) : IHostedAgentRuntimeFactory {
    public string Vendor             => launcher.Vendor;
    public bool   SupportsUnattended => launcher.SupportsUnattended;
    public bool   SupportsBorrowedReviewFlow => launcher.SupportsBorrowedReviewFlow;
    public string? BorrowedReviewContainment => launcher.BorrowedReviewContainment;

    /// <summary>PTY factories delegate reviewer-model resolution to their launcher-owned policy — the
    /// launcher (Claude, Codex) owns its accepted aliases/ids and canonical/equivalence behavior.</summary>
    public IReviewerModelResolver? ReviewerModelResolver => launcher.ReviewerModelResolver;

    public bool IsAvailable() => launcher.IsAvailable();

    /// <remarks>
    /// <paramref name="ct"/> is accepted for interface parity with <see cref="IHostedAgentRuntimeFactory"/>
    /// but is not observed on this path — <c>Prepare</c>, <c>BuildArgs</c>, and the PTY spawn are all
    /// synchronous/uncancellable, matching the pre-refactor behavior this factory was extracted from
    /// (not a regression introduced here). A caller should not assume cancelling <paramref name="ct"/>
    /// aborts an in-flight PTY launch.
    /// </remarks>
    public async Task<HostedRuntimeStart> StartAsync(RuntimeStartContext ctx, CancellationToken ct) {
        var launcherCtx = new LauncherContext(
            AgentId: ctx.AgentId,
            SourceRepoPath: ctx.SourceRepoPath,
            Worktree: ctx.Worktree,
            Prompt: ctx.Prompt,
            Model: ctx.Model,
            Effort: ctx.Effort,
            Tools: ctx.Tools,
            IsReview: ctx.IsReview,
            IsReviewFlow: ctx.IsReviewFlow,
            Review: ctx.Review,
            // The review-launch MCP server command must be the kcap binary (ctx.CapacitorPath),
            // NOT the vendor CLI (launcher.CliPath) — the review agent runs `kcap mcp review` to
            // talk to the review MCP tools. See RuntimeStartContext.CapacitorPath's doc (PR #244
            // review, Fix A).
            ReviewLaunch: ctx.IsReview && ctx.Review is { } reviewArgs
                ? await ReviewLaunchBuilder.BuildAsync(ctx.Vendor, ctx.CapacitorPath, ctx.ServerUrl ?? "", reviewArgs.Owner, reviewArgs.Repo, reviewArgs.PrNumber)
                : null
        ) {
            McpAllowlist = ctx.McpAllowlist,
            Work         = ctx.Work
        };

        try {
            launcher.Prepare(launcherCtx);
        } catch (UnattendedReviewPolicyException) {
            throw;
        } catch (CodexHooksNotInstalledException) {
            // Propagate — the orchestrator maps this to LaunchFailed + worktree cleanup.
            throw;
        } catch (Exception ex) {
            LogPrepareSoftFailure(ex, ctx.AgentId);
        }

        var launchArgs    = launcher.BuildArgs(launcherCtx);
        var args          = launchArgs.Args;
        var mcpConfigPath = launchArgs.McpConfigPath;

        var env = new Dictionary<string, string> {
            ["KCAP_RENDERED_AGENT"] = "1",
            ["KCAP_AGENT_ID"]       = ctx.AgentId
        };

        // Phase B (D4 §6.4(3)): stamp the daemon-identity markers so a restarted daemon's
        // OrphanReaper env-marker scan can reap a recordless survivor of a PRIOR incarnation of THIS
        // daemon (same KCAP_DAEMON_ID, older KCAP_DAEMON_EPOCH) — and never touch another daemon's
        // child or a live agent of the current incarnation.
        if (!string.IsNullOrEmpty(ctx.DaemonId))    env["KCAP_DAEMON_ID"]    = ctx.DaemonId;
        if (!string.IsNullOrEmpty(ctx.DaemonEpoch)) env["KCAP_DAEMON_EPOCH"] = ctx.DaemonEpoch;

        if (!string.IsNullOrEmpty(ctx.ServerUrl)) {
            env["KCAP_URL"] = ctx.ServerUrl;
        }

        // Tell the spawned Claude's permission-request hook where to find this daemon's local
        // SignalR bridge. Bypasses Cloudflare's HTTP timeout on the server's
        // /hooks/permission-request long-poll. CLI falls back to KCAP_URL if this var is absent
        // (e.g. older CLI builds).
        if (!string.IsNullOrEmpty(ctx.DaemonBridgeUrl)) {
            env["KCAP_DAEMON_URL"] = ctx.DaemonBridgeUrl;
        }

        if (ctx.IsReview && ctx.Review is { } reviewEnv) {
            env["KCAP_REVIEW_PR"] = reviewEnv.PrNumber.ToString();
        }

        var pty     = ptyFactory.Spawn(launcher.CliPath, args, ctx.Worktree.Path, env, ctx.Cols, ctx.Rows);
        var runtime = new PtyHostedAgentRuntime(ctx.Vendor, pty);

        return new HostedRuntimeStart(runtime, mcpConfigPath);
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Launcher Prepare soft-failure for agent {AgentId} (continuing)")]
    partial void LogPrepareSoftFailure(Exception ex, string agentId);
}
