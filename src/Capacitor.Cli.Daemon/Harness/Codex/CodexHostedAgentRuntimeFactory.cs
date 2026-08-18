using System.Diagnostics;
using Capacitor.Cli.Daemon.Acp;
using Capacitor.Cli.Daemon.Services;
using Microsoft.Extensions.Logging;

namespace Capacitor.Cli.Daemon.Harness.Codex;

/// <summary>
/// The single <c>codex</c> runtime factory. It routes a launch to one of two transports and is the
/// ONLY codex entry in the vendor→factory dictionary:
/// <list type="bullet">
/// <item>a review-flow (unattended) launch, when this daemon resolved app-server as active
/// (<see cref="DaemonConfig.CodexAppServerActive"/> — operator selection + version floor, decided
/// once by <see cref="CodexTransportDecision"/>), goes to
/// <see cref="CodexAppServerHostedAgentRuntime"/>;</item>
/// <item>everything else — every interactive launch, and every launch when the transport is PTY —
/// delegates to the wrapped <see cref="PtyHostedAgentRuntimeFactory"/>, byte-identically to today.</item>
/// </list>
///
/// <para>Capability advertisement (borrowed-review containment, unattended support, model resolver)
/// is delegated to the PTY factory unchanged: the transport swaps the LAUNCH mechanism, not the
/// containment story — a reviewer's read boundary stays <c>native-tool-clamp</c> either way. The
/// launcher-policy version the server certifies against is chosen in <c>DaemonRunner</c> from the
/// same <see cref="DaemonConfig.CodexAppServerActive"/> field this factory routes on, so the
/// advertised policy and the transport used are one fact.</para>
/// </summary>
internal sealed class CodexHostedAgentRuntimeFactory : IHostedAgentRuntimeFactory {
    readonly CodexLauncher               _launcher;
    readonly IHostedAgentRuntimeFactory  _pty;
    readonly DaemonConfig                _config;
    readonly ILoggerFactory              _loggerFactory;
    readonly ILogger                     _logger;
    readonly CodexAppServerSpawnFactory  _spawnFactory;

    /// <param name="spawnFactory">Test seam only: production passes <see langword="null"/> and the
    /// real <c>codex app-server</c> child is spawned via <see cref="Process"/>. A test can substitute
    /// an in-process fake peer to drive the app-server route without a child process.</param>
    public CodexHostedAgentRuntimeFactory(
            CodexLauncher launcher, IHostedAgentRuntimeFactory ptyDelegate, DaemonConfig config,
            ILoggerFactory loggerFactory, CodexAppServerSpawnFactory? spawnFactory = null) {
        _launcher      = launcher;
        _pty           = ptyDelegate;
        _config        = config;
        _loggerFactory = loggerFactory;
        _logger        = loggerFactory.CreateLogger<CodexHostedAgentRuntimeFactory>();
        _spawnFactory  = spawnFactory ?? DefaultSpawnFactory;
    }

    public string Vendor => "codex";

    // Capability advertisement is transport-independent — delegate every member to the PTY factory.
    public bool             IsAvailable()                              => _pty.IsAvailable();
    public bool             SupportsUnattended                          => _pty.SupportsUnattended;
    public UnattendedSupport DescribeUnattendedSupport()               => _pty.DescribeUnattendedSupport();
    public bool             SupportsBorrowedReviewFlow                  => _pty.SupportsBorrowedReviewFlow;
    public bool             BorrowedReviewRequiresIndependentSnapshot   => _pty.BorrowedReviewRequiresIndependentSnapshot;
    public string?          BorrowedReviewContainment                   => _pty.BorrowedReviewContainment;
    public bool             ReviewFlowRedirectsHome                     => _pty.ReviewFlowRedirectsHome;
    public IReviewerModelResolver? ReviewerModelResolver               => _pty.ReviewerModelResolver;
    public bool             SupportsModelSelection                      => _pty.SupportsModelSelection;

    /// <summary>App-server is used for a launch only when the daemon resolved it active AND the
    /// launch is unattended (review-flow). An interactive launch always takes the PTY path — even
    /// under an app-server selection — because interactive hosting is a later phase.</summary>
    internal bool UsesAppServer(RuntimeStartContext ctx) => _config.CodexAppServerActive && ctx.IsReviewFlow;

    public Task<HostedRuntimeStart> StartAsync(RuntimeStartContext ctx, CancellationToken ct) =>
        UsesAppServer(ctx) ? StartAppServerAsync(ctx, ct) : _pty.StartAsync(ctx, ct);

    async Task<HostedRuntimeStart> StartAppServerAsync(RuntimeStartContext ctx, CancellationToken ct) {
        var launcherCtx = BuildLauncherContext(ctx);

        // Fail-closed hooks preflight + TrustWorktree config, exactly as the PTY path — a missing
        // critical hook throws CodexHooksNotInstalledException for the orchestrator's cleanup path.
        _launcher.Prepare(launcherCtx);

        var (sandbox, approval) = CodexPosturePolicy.Resolve(ctx.Work, ctx.IsReviewFlow, ctx.CodexPosture);
        var appServerArgs = _launcher.BuildAppServerLaunchArgs(launcherCtx);
        var env           = BuildEnv(ctx);

        var launch = new CodexAppServerLaunch(
            Cwd:           ctx.Worktree.Path,
            Model:         ctx.Model,
            InitialPrompt: ctx.Prompt,
            Sandbox:       sandbox,
            Approval:      approval,
            WritableRoots: [ctx.Worktree.Path],
            ClientVersion: string.IsNullOrEmpty(_config.Version) ? "0.0.0" : _config.Version);

        CodexAppServerSpawn spawn = (seed, spawnCt) =>
            _spawnFactory(_launcher.CliPath, appServerArgs, seed, ctx.Worktree.Path, env, _config, _loggerFactory);

        var runtime = new CodexAppServerHostedAgentRuntime(
            spawn, launch, ctx.ActivityClock,
            _loggerFactory.CreateLogger<CodexAppServerHostedAgentRuntime>());

        await runtime.StartAsync(ct).ConfigureAwait(false);
        return new HostedRuntimeStart(runtime, McpConfigPath: null);
    }

    static LauncherContext BuildLauncherContext(RuntimeStartContext ctx) => new(
        AgentId:        ctx.AgentId,
        SourceRepoPath: ctx.SourceRepoPath,
        Worktree:       ctx.Worktree,
        Prompt:         ctx.Prompt,
        Model:          ctx.Model,
        Effort:         ctx.Effort,
        Tools:          ctx.Tools,
        IsReview:       ctx.IsReview,
        IsReviewFlow:   ctx.IsReviewFlow,
        Review:         ctx.Review,
        // App-server review flows carry no PR-review MCP launch (ctx.IsReview is false); the
        // flow-result + allowlist servers are spliced by BuildAppServerLaunchArgs from IsReviewFlow.
        ReviewLaunch:   null) {
        McpAllowlist = ctx.McpAllowlist,
        Work         = ctx.Work,
        CodexPosture = ctx.CodexPosture,
    };

    static Dictionary<string, string> BuildEnv(RuntimeStartContext ctx) {
        var env = new Dictionary<string, string> {
            ["KCAP_RENDERED_AGENT"] = "1",
            ["KCAP_AGENT_ID"]       = ctx.AgentId,
        };
        if (!string.IsNullOrEmpty(ctx.DaemonId))        env["KCAP_DAEMON_ID"]    = ctx.DaemonId;
        if (!string.IsNullOrEmpty(ctx.DaemonEpoch))     env["KCAP_DAEMON_EPOCH"] = ctx.DaemonEpoch;
        if (!string.IsNullOrEmpty(ctx.ServerUrl))       env["KCAP_URL"]          = ctx.ServerUrl;
        if (!string.IsNullOrEmpty(ctx.DaemonBridgeUrl)) env["KCAP_DAEMON_URL"]   = ctx.DaemonBridgeUrl;
        return env;
    }

    /// <summary>The real spawn: <c>codex app-server</c> as a child process, its stdio wrapped by the
    /// shared <see cref="AcpChildProcess"/> (stderr drain + terminate) and the JSON-RPC transport.</summary>
    static Task<(CodexAppServerConnection, IAcpProcess)> DefaultSpawnFactory(
            string cliPath, IReadOnlyList<string> appServerArgs, string? hookStateSeed, string cwd,
            IReadOnlyDictionary<string, string> env, DaemonConfig config, ILoggerFactory loggerFactory) {
        var argv = new List<string> { "app-server" };
        argv.AddRange(appServerArgs);
        if (!string.IsNullOrEmpty(hookStateSeed)) {
            argv.Add("-c");
            argv.Add(hookStateSeed);
        }

        var psi = new ProcessStartInfo(cliPath, argv) {
            RedirectStandardInput  = true,
            RedirectStandardOutput = true,
            RedirectStandardError  = true,
            UseShellExecute        = false,
            WorkingDirectory       = cwd,
        };
        foreach (var (k, v) in env) psi.Environment[k] = v;

        var process = Process.Start(psi)
            ?? throw new InvalidOperationException($"Failed to start '{cliPath} {string.Join(' ', argv)}'.");
        var child = new AcpChildProcess(process, loggerFactory.CreateLogger<AcpChildProcess>(), config.DebugFrames, "codex");
        var conn  = new CodexAppServerConnection(
            process.StandardInput.BaseStream, process.StandardOutput.BaseStream,
            loggerFactory.CreateLogger<CodexAppServerConnection>(), config.DebugFrames);

        return Task.FromResult<(CodexAppServerConnection, IAcpProcess)>((conn, child));
    }
}

/// <summary>Test seam for how a <c>codex app-server</c> child (and its transport) is produced —
/// production spawns a real process; a test substitutes an in-process fake peer.</summary>
internal delegate Task<(CodexAppServerConnection Connection, IAcpProcess Process)> CodexAppServerSpawnFactory(
    string cliPath, IReadOnlyList<string> appServerArgs, string? hookStateSeed, string cwd,
    IReadOnlyDictionary<string, string> env, DaemonConfig config, ILoggerFactory loggerFactory);
