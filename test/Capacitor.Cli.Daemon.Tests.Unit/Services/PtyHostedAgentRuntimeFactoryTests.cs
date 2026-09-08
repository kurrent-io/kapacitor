using Capacitor.Cli.Core;
using Capacitor.Cli.Core.Commands;
using Capacitor.Cli.Daemon.Harness.Claude;
using Capacitor.Cli.Daemon.Pty;
using Capacitor.Cli.Daemon.Services;
using Capacitor.Cli.Daemon.Tests.Unit.Pty;
using Microsoft.Extensions.Logging.Abstractions;

namespace Capacitor.Cli.Daemon.Tests.Unit.Services;

/// <summary>
/// <see cref="RuntimeStartContext.CapacitorPath"/> must reach <see cref="ReviewLaunchBuilder.BuildAsync"/>
/// as the review-launch MCP command: the review agent runs <c>kcap mcp review</c>, and the vendor
/// CLI (<c>launcher.CliPath</c>) has no such subcommand.
/// </summary>
public class PtyHostedAgentRuntimeFactoryTests {
    [TempHome] public required TempHome Home { get; init; }
    [TempDir]  public required TempDir  Tmp  { get; init; }

    sealed class RecordingLauncher(string vendor, string cliPath) : IHostedAgentLauncher {
        public string Vendor  { get; } = vendor;
        public string CliPath { get; } = cliPath;
        public bool   SupportsUnattended { get; init; } = true;

        public LauncherContext? LastPrepareCtx { get; private set; }

        public bool IsAvailable() => true;

        public void Prepare(LauncherContext ctx) => LastPrepareCtx = ctx;

        public LaunchArgs BuildArgs(LauncherContext ctx) => new(Args: [], McpConfigPath: null);

        public LaunchArgs BuildPassthrough(LauncherContext ctx, IReadOnlyList<string> userArgs) =>
            new(Args: [.. userArgs], McpConfigPath: null);

        public void Cleanup(AgentInstance agent) { }
    }

    static RuntimeStartContext BuildReviewContext(string vendor, string capacitorPath) =>
        new(
            AgentId: "agent-review-1",
            Vendor: vendor,
            SourceRepoPath: "/repo",
            Worktree: new WorktreeInfo("/repo/.worktrees/agent-review-1", "review-branch", "/repo"),
            Prompt: null,
            Model: "opus",
            Effort: null,
            Tools: null,
            IsReview: true,
            IsReviewFlow: false,
            Review: new ReviewLaunchInfo("acme", "widgets", 42),
            Cols: 120,
            Rows: 40,
            ServerUrl: "https://srv",
            DaemonBridgeUrl: null,
            CapacitorPath: capacitorPath
        );

    /// Real directories, unlike BuildInteractiveContext's: ClaudeLauncher.Prepare writes into the
    /// worktree and the home.
    RuntimeStartContext BuildClaudeLaunchContext(string? permissionMode) {
        var repo     = Tmp.CreateDir("repo");
        var worktree = Tmp.CreateDir("wt");
        return new(
            AgentId: "agent-mode-1",
            Vendor: "claude",
            SourceRepoPath: repo,
            Worktree: new WorktreeInfo(worktree, "wt-branch", repo),
            Prompt: "build it",
            Model: "default",
            Effort: null,
            Tools: null,
            IsReview: false,
            IsReviewFlow: false,
            Review: null,
            Cols: 120,
            Rows: 40,
            ServerUrl: "",
            DaemonBridgeUrl: null,
            CapacitorPath: "kcap",
            PermissionMode: permissionMode
        );
    }

    /// The runtime-context→launcher handoff: the launcher's own tests build a LauncherContext by
    /// hand, so dropping `PermissionMode = ctx.PermissionMode` here would leave them green.
    [Test]
    public async Task Interactive_launch_hands_the_permission_mode_to_the_launcher() {
        var launcher = new RecordingLauncher("claude", cliPath: "/opt/vendor/claude");
        var factory  = new PtyHostedAgentRuntimeFactory(launcher, new NullPtyProcessFactory(), NullLogger<PtyHostedAgentRuntimeFactory>.Instance);

        var ctx   = BuildInteractiveContext("claude") with { PermissionMode = "acceptEdits" };
        var start = await factory.StartAsync(ctx, CancellationToken.None);

        try {
            await Assert.That(launcher.LastPrepareCtx!.PermissionMode).IsEqualTo("acceptEdits");
        } finally {
            await start.Runtime.DisposeAsync();
        }
    }

    [Test]
    public async Task Interactive_claude_launch_spawns_exactly_one_selected_mode_pair() {
        var config   = new DaemonConfig { ClaudePath = "claude", ServerUrl = "", CapacitorPath = "kcap" };
        var launcher = new ClaudeLauncher(config, TestHarnesses.Under(Home), NullLogger<ClaudeLauncher>.Instance);
        var pty      = new SpyPtyProcessFactory();
        var factory  = new PtyHostedAgentRuntimeFactory(launcher, pty, NullLogger<PtyHostedAgentRuntimeFactory>.Instance);

        var start = await factory.StartAsync(BuildClaudeLaunchContext("auto"), CancellationToken.None);

        try {
            var args = pty.LastArgs!;
            await Assert.That(args.Count(a => a == "--permission-mode")).IsEqualTo(1);
            await Assert.That(args[Array.IndexOf(args, "--permission-mode") + 1]).IsEqualTo("auto");
        } finally {
            await start.Runtime.DisposeAsync();
        }
    }

    [Test]
    public async Task Review_launch_builds_the_MCP_command_from_CapacitorPath_not_the_agent_CliPath() {
        var launcher   = new RecordingLauncher("claude", cliPath: "/opt/vendor/claude");
        var ptyFactory = new NullPtyProcessFactory();
        var factory    = new PtyHostedAgentRuntimeFactory(launcher, ptyFactory, NullLogger<PtyHostedAgentRuntimeFactory>.Instance);

        var ctx = BuildReviewContext("claude", capacitorPath: "/opt/kcap/kcap");

        var start = await factory.StartAsync(ctx, CancellationToken.None);

        try {
            var reviewLaunch = launcher.LastPrepareCtx!.ReviewLaunch;
            await Assert.That(reviewLaunch).IsNotNull();

            // The MCP server descriptor's Command is what the review agent actually spawns to
            // talk to the review MCP tools — it must be the kcap binary, never the vendor CLI.
            await Assert.That(reviewLaunch!.Mcp.Command).IsEqualTo("/opt/kcap/kcap");
            await Assert.That(reviewLaunch.Mcp.Command).IsNotEqualTo("/opt/vendor/claude");

            // Claude's written MCP config file (the actual artifact claude reads to find the
            // server) must also reference the kcap path, not the vendor CLI.
            await Assert.That(reviewLaunch.McpConfigPath).IsNotNull();
            var json = await File.ReadAllTextAsync(reviewLaunch.McpConfigPath!);
            await Assert.That(json).Contains("/opt/kcap/kcap");
            await Assert.That(json).DoesNotContain("/opt/vendor/claude");
        } finally {
            if (launcher.LastPrepareCtx?.ReviewLaunch?.McpConfigPath is { } path && File.Exists(path))
                File.Delete(path);
            await start.Runtime.DisposeAsync();
        }
    }

    [Test]
    public async Task Review_launch_for_codex_builds_MCP_command_from_CapacitorPath_not_the_agent_CliPath() {
        var launcher   = new RecordingLauncher("codex", cliPath: "/opt/vendor/codex");
        var ptyFactory = new NullPtyProcessFactory();
        var factory    = new PtyHostedAgentRuntimeFactory(launcher, ptyFactory, NullLogger<PtyHostedAgentRuntimeFactory>.Instance);

        var ctx = BuildReviewContext("codex", capacitorPath: "/opt/kcap/kcap");

        var start = await factory.StartAsync(ctx, CancellationToken.None);

        try {
            var reviewLaunch = launcher.LastPrepareCtx!.ReviewLaunch;
            await Assert.That(reviewLaunch).IsNotNull();
            await Assert.That(reviewLaunch!.Mcp.Command).IsEqualTo("/opt/kcap/kcap");
            await Assert.That(reviewLaunch.Mcp.Command).IsNotEqualTo("/opt/vendor/codex");
            // Codex injects the MCP server via -c overrides — no config file is written.
            await Assert.That(reviewLaunch.McpConfigPath).IsNull();
        } finally {
            await start.Runtime.DisposeAsync();
        }
    }

    /// <summary>The factory half of the posture handoff. The orchestrator-level test proves
    /// <c>LaunchAgentCommand → RuntimeStartContext</c>, but it substitutes a spy for this factory, so
    /// dropping <c>CodexPosture = ctx.CodexPosture</c> here would leave that test green while the real
    /// launcher silently received null — launching on the default posture even though registration
    /// advertised the selected pair. <see cref="RecordingLauncher.LastPrepareCtx"/> is the same
    /// LauncherContext instance BuildArgs consumes, so asserting on it pins what the launcher sees.</summary>
    [Test]
    public async Task Codex_launch_threads_the_posture_into_the_launcher_context() {
        var launcher   = new RecordingLauncher("codex", cliPath: "/opt/vendor/codex");
        var ptyFactory = new NullPtyProcessFactory();
        var factory    = new PtyHostedAgentRuntimeFactory(launcher, ptyFactory, NullLogger<PtyHostedAgentRuntimeFactory>.Instance);

        var ctx = BuildInteractiveContext("codex") with { CodexPosture = new("danger-full-access", "untrusted") };

        var start = await factory.StartAsync(ctx, CancellationToken.None);

        try {
            await Assert.That(launcher.LastPrepareCtx).IsNotNull();
            await Assert.That(launcher.LastPrepareCtx!.CodexPosture).IsNotNull();
            await Assert.That(launcher.LastPrepareCtx!.CodexPosture!.Sandbox).IsEqualTo("danger-full-access");
            await Assert.That(launcher.LastPrepareCtx!.CodexPosture!.Approval).IsEqualTo("untrusted");
        } finally {
            await start.Runtime.DisposeAsync();
        }
    }

    [Test]
    public async Task Codex_launch_without_a_posture_threads_null_into_the_launcher_context() {
        var launcher   = new RecordingLauncher("codex", cliPath: "/opt/vendor/codex");
        var ptyFactory = new NullPtyProcessFactory();
        var factory    = new PtyHostedAgentRuntimeFactory(launcher, ptyFactory, NullLogger<PtyHostedAgentRuntimeFactory>.Instance);

        var start = await factory.StartAsync(BuildInteractiveContext("codex"), CancellationToken.None);

        try {
            await Assert.That(launcher.LastPrepareCtx).IsNotNull();
            await Assert.That(launcher.LastPrepareCtx!.CodexPosture).IsNull();
        } finally {
            await start.Runtime.DisposeAsync();
        }
    }

    static RuntimeStartContext BuildInteractiveContext(string vendor) =>
        new(
            AgentId: "agent-interactive-1",
            Vendor: vendor,
            SourceRepoPath: "/repo",
            Worktree: new WorktreeInfo("/repo/.worktrees/agent-interactive-1", "wt-branch", "/repo"),
            Prompt: null,
            Model: "gpt-5.3-codex",
            Effort: null,
            Tools: null,
            IsReview: false,
            IsReviewFlow: false,
            Review: null,
            Cols: 120,
            Rows: 40,
            ServerUrl: "https://srv",
            DaemonBridgeUrl: null,
            CapacitorPath: "/opt/kcap/kcap"
        );

    sealed class NullPtyProcessFactory : IPtyProcessFactory {
        public IPtyProcess Spawn(
                string                      command,
                string[]                    args,
                string                      cwd,
                Dictionary<string, string>? extraEnv = null,
                ushort                      cols     = 120,
                ushort                      rows     = 40
            ) => new NoopPty();

        sealed class NoopPty : IPtyProcess {
            public int  Pid       => 0;
            public bool HasExited => true;
            public int? ExitCode  => 0;

            public ValueTask DisposeAsync() => default;
            public Task WaitForExitAsync(TimeSpan? timeout = null) => Task.CompletedTask;
            public Task TerminateAsync(TimeSpan?   timeout = null) => Task.CompletedTask;

#pragma warning disable CS1998
            public async IAsyncEnumerable<byte[]> ReadOutputAsync(
                    [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default) {
                yield break;
            }
#pragma warning restore CS1998

            public Task WriteAsync(string input) => Task.CompletedTask;
            public Task WriteAsync(byte[] data) => Task.CompletedTask;
            public void Resize(ushort     cols, ushort rows) { }
            public void SendInterrupt() { }
        }
    }
}
