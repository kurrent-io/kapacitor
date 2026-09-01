using System.Runtime.CompilerServices;
using Capacitor.Cli.Daemon.Acp;
using Capacitor.Cli.Daemon.Harness.Codex;
using Capacitor.Cli.Daemon.Services;
using Microsoft.Extensions.Logging.Abstractions;

namespace Capacitor.Cli.Daemon.Tests.Unit.Harness.Codex;

/// <summary>
/// The composite codex factory's ROUTING: review-flow launches go to app-server only when the daemon
/// resolved it active; every interactive launch, and all launches under the PTY default, delegate to
/// the wrapped PTY factory. The app-server route is exercised through the spawn seam (a fake peer),
/// so no child process runs; the delegation cases never touch the launcher at all.
/// </summary>
public class CodexHostedAgentRuntimeFactoryTests {
    [TempHome] public required TempHome Home { get; init; }

    static readonly TimeSpan HangGuard = TimeSpan.FromSeconds(5);

    // ── Test doubles ─────────────────────────────────────────────────────────────────────────
    sealed class RecordingPtyFactory : IHostedAgentRuntimeFactory {
    public string CliPath => "unused-by-this-double";
        public int StartCalls;
        public string Vendor => "codex";
        public bool IsAvailable() => true;
        public bool SupportsUnattended => true;
        public Task<HostedRuntimeStart> StartAsync(RuntimeStartContext ctx, CancellationToken ct) {
            StartCalls++;
            return Task.FromResult(new HostedRuntimeStart(new StubRuntime(), null));
        }
    }

    sealed class StubRuntime : IHostedAgentRuntime {
        public string Vendor => "codex";
        public int    Pid => 1;
        public bool   HasExited => false;
        public int?   ExitCode => null;
        public bool   EmitsTerminalOutput => true;
        public async IAsyncEnumerable<byte[]> ReadOutputAsync([EnumeratorCancellation] CancellationToken ct = default) {
            await Task.CompletedTask; yield break;
        }
        public Task SendUserInputAsync(string text) => Task.CompletedTask;
        public Task SendSpecialKeyAsync(string key) => Task.CompletedTask;
        public Task SendRawInputAsync(byte[] data) => Task.CompletedTask;
        public void Resize(ushort cols, ushort rows) { }
        public Task RequestGracefulStopAsync() => Task.CompletedTask;
        public Task WaitForExitAsync(TimeSpan? timeout = null) => Task.CompletedTask;
        public Task TerminateAsync(TimeSpan? timeout = null) => Task.CompletedTask;
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    sealed class FakeAcpProcess : IAcpProcess {
        public int  Pid => 7;
        public bool HasExited { get; private set; }
        public int? ExitCode => HasExited ? 0 : null;
        public Task WaitForExitAsync(TimeSpan? timeout = null) => Task.CompletedTask;
        public Task TerminateAsync(TimeSpan? timeout = null) { HasExited = true; return Task.CompletedTask; }
        public ValueTask DisposeAsync() { HasExited = true; return ValueTask.CompletedTask; }
    }

    CodexLauncher NewLauncher() =>
        new(new DaemonConfig { CodexPath = "codex", CapacitorPath = "/opt/kcap", ServerUrl = "https://t.example" },
            Home, NullLogger<CodexLauncher>.Instance) {
            ReadInheritedMcpServers = static () => [],
        };

    static RuntimeStartContext Ctx(bool isReviewFlow, string worktreePath) => new(
        AgentId: "agent-1", Vendor: "codex", SourceRepoPath: worktreePath,
        Worktree: new WorktreeInfo(Path: worktreePath, Branch: "wt", SourceRepo: worktreePath),
        Prompt: null, Model: null, Effort: null, Tools: null,
        IsReview: false, IsReviewFlow: isReviewFlow, Review: null,
        Cols: 80, Rows: 24, ServerUrl: "https://t.example", DaemonBridgeUrl: null, CapacitorPath: "/opt/kcap");

    CodexHostedAgentRuntimeFactory Factory(
            RecordingPtyFactory pty, bool appServerActive, CodexAppServerSpawnFactory? spawn = null,
            bool appServerInteractive = false) =>
        new(NewLauncher(), pty,
            new DaemonConfig {
                CodexAppServerActive      = appServerActive,
                CodexAppServerInteractive = appServerInteractive,
                Version                   = "0.146.0",
            },
            NullLoggerFactory.Instance, spawn);

    // ── Routing decision ─────────────────────────────────────────────────────────────────────
    [Test]
    [Arguments(false, true,  false)] // not active -> pty even for a review flow
    [Arguments(true,  false, false)] // active, interactive, not opted in -> pty
    [Arguments(true,  true,  true)]  // active + review flow -> app-server
    public async Task UsesAppServer_gates_on_active_AND_review_flow(bool active, bool isReviewFlow, bool expected) {
        using var wt = new TempDir();
        var factory = Factory(new RecordingPtyFactory(), active);
        await Assert.That(factory.UsesAppServer(Ctx(isReviewFlow, wt.Path))).IsEqualTo(expected);
    }

    /// <summary>The per-daemon opt-in widens app-server to interactive launches on THIS daemon only.
    /// Reviewers are unaffected by it, and it cannot switch a daemon whose transport is still PTY —
    /// which is what lets one daemon run interactive on app-server while a fleet stays on PTY.</summary>
    [Test]
    [Arguments(true,  false, true,  true)]  // opted in + active, interactive launch -> app-server
    [Arguments(true,  true,  true,  true)]  // opted in + active, review flow        -> app-server
    [Arguments(false, false, true,  false)] // NOT opted in, interactive             -> pty
    [Arguments(true,  false, false, false)] // opted in but transport still pty      -> pty
    public async Task UsesAppServer_admits_interactive_only_where_the_daemon_opted_in(
            bool interactiveOptIn, bool isReviewFlow, bool active, bool expected) {
        using var wt = new TempDir();
        var factory = Factory(new RecordingPtyFactory(), active, appServerInteractive: interactiveOptIn);
        await Assert.That(factory.UsesAppServer(Ctx(isReviewFlow, wt.Path))).IsEqualTo(expected);
    }

    /// <summary>The opt-in names INTERACTIVE, and a PR review is not that. Expressed as "not a review
    /// flow" the switch would move PR review too — a launch class the operator never opted in — so this
    /// pins the third class explicitly rather than leaving it to the negative form.</summary>
    [Test]
    public async Task UsesAppServer_leaves_a_pr_review_on_pty_even_when_the_daemon_opted_in() {
        using var wt = new TempDir();
        var factory = Factory(new RecordingPtyFactory(), appServerActive: true, appServerInteractive: true);
        var prReview = Ctx(isReviewFlow: false, wt.Path) with { IsReview = true };

        await Assert.That(factory.UsesAppServer(prReview)).IsFalse();
        // Control: the same daemon DOES take an interactive launch to app-server.
        await Assert.That(factory.UsesAppServer(Ctx(isReviewFlow: false, wt.Path))).IsTrue();
    }

    /// <summary>The env spelling is the operator's only lever, so it is pinned directly: affirmative
    /// spellings turn it on and everything else — including "0", "false" and a typo — leaves it off.
    /// Off is the safe direction; a value the parser does not know must never move a daemon.</summary>
    [Test]
    [Arguments("1", true)]
    [Arguments("true", true)]
    [Arguments("TRUE", true)]
    [Arguments("True", true)]
    [Arguments("yes", true)]
    [Arguments("on", true)]
    [Arguments("0", false)]
    [Arguments("false", false)]
    [Arguments("off", false)]
    [Arguments("no", false)]
    [Arguments("app-server", false)]
    [Arguments("", false)]
    [Arguments(null, false)]
    public async Task Interactive_opt_in_accepts_only_affirmative_spellings(string? value, bool expected) =>
        await Assert.That(CodexTransportDecision.IsInteractiveOptIn(value)).IsEqualTo(expected);

    [Test]
    public async Task Pty_transport_delegates_a_review_flow_to_the_pty_factory() {
        using var wt = new TempDir();
        var pty = new RecordingPtyFactory();
        var factory = Factory(pty, appServerActive: false);

        var start = await factory.StartAsync(Ctx(isReviewFlow: true, wt.Path), CancellationToken.None);

        await Assert.That(pty.StartCalls).IsEqualTo(1);
        await Assert.That(start.Runtime).IsTypeOf<StubRuntime>();
    }

    [Test]
    public async Task Interactive_launch_delegates_to_pty_even_when_app_server_is_active() {
        using var wt = new TempDir();
        var pty = new RecordingPtyFactory();
        var factory = Factory(pty, appServerActive: true);

        var start = await factory.StartAsync(Ctx(isReviewFlow: false, wt.Path), CancellationToken.None);

        await Assert.That(pty.StartCalls).IsEqualTo(1);
        await Assert.That(start.Runtime).IsTypeOf<StubRuntime>();
    }

    // ── App-server route (spawn seam; isolated HOME so TrustWorktree touches nothing real) ──────
    [Test]
    [NotInParallel]
    public async Task Active_review_flow_produces_an_app_server_runtime_via_the_spawn_seam() {
        using var wt   = new TempDir();
        WriteWorktreeHooks(wt);
        await using var fake = new FakeCodexAppServer();

        using var codexEnv = EnvScope.Exclusive("CODEX_HOME", Home.PathTo(".codex"));

        var pty = new RecordingPtyFactory();
        CodexAppServerSpawnFactory seam = (_, _, _, _, _, _, _) =>
            Task.FromResult<(CodexAppServerConnection, IAcpProcess)>((fake.ConnectClient(), new FakeAcpProcess()));
        var factory = Factory(pty, appServerActive: true, seam);

        var start = await factory.StartAsync(Ctx(isReviewFlow: true, wt.Path), CancellationToken.None).WaitAsync(HangGuard);

        await Assert.That(pty.StartCalls).IsEqualTo(0);
        await Assert.That(start.Runtime).IsTypeOf<CodexAppServerHostedAgentRuntime>();
        await Assert.That(start.Runtime.EmitsTerminalOutput).IsFalse();
        await Assert.That(((CodexAppServerHostedAgentRuntime) start.Runtime).ThreadId).IsEqualTo("thread-abc");
        await Assert.That(fake.ReceivedMethods).Contains("thread/start");

        await start.Runtime.DisposeAsync();
    }

    // ── Guard-1 marker (§2.5) ──────────────────────────────────────────────────────────
    [Test]
    public async Task BuildEnv_stamps_the_hosted_appserver_marker_only_when_emitting_envelopes() {
        using var wt = new TempDir();
        var ctx = Ctx(isReviewFlow: true, wt.Path);

        var off = CodexHostedAgentRuntimeFactory.BuildEnv(ctx, emitEnvelopeTranscript: false);
        var on  = CodexHostedAgentRuntimeFactory.BuildEnv(ctx, emitEnvelopeTranscript: true);

        await Assert.That(off.ContainsKey("KCAP_HOSTED_APPSERVER")).IsFalse();
        await Assert.That(on["KCAP_HOSTED_APPSERVER"]).IsEqualTo("1");
    }

    [Test]
    [NotInParallel]
    public async Task App_server_launch_is_envelope_sourced_after_activation() {
        using var wt   = new TempDir();
        WriteWorktreeHooks(wt);
        await using var fake = new FakeCodexAppServer();
        IReadOnlyDictionary<string, string>? capturedEnv = null;

        using var codexEnv = EnvScope.Exclusive("CODEX_HOME", Home.PathTo(".codex"));

        CodexAppServerSpawnFactory seam = (_, _, _, _, env, _, _) => {
            capturedEnv = env;
            return Task.FromResult<(CodexAppServerConnection, IAcpProcess)>((fake.ConnectClient(), new FakeAcpProcess()));
        };
        var factory = Factory(new RecordingPtyFactory(), appServerActive: true, seam);

        var start = await factory.StartAsync(Ctx(isReviewFlow: true, wt.Path), CancellationToken.None).WaitAsync(HangGuard);

        await Assert.That(start.Transcript).IsNotNull();
        await Assert.That(start.Runtime.RequiresSourceClaimBeforeFirstTurn).IsTrue();

        await start.Runtime.DisposeAsync();

        await Assert.That(capturedEnv).IsNotNull();
        await Assert.That(capturedEnv!["KCAP_HOSTED_APPSERVER"]).IsEqualTo("1");
    }

    [Test]
    public async Task ApplyChildEnv_clears_an_inherited_marker_when_the_overlay_does_not_emit() {
        // A daemon whose OWN environment carries the marker must not leak it to a non-emitting child —
        // that child's hook would suppress the only watcher and lose the transcript.
        var childEnv = new Dictionary<string, string?> { ["KCAP_HOSTED_APPSERVER"] = "1", ["PATH"] = "/usr/bin" };
        var overlay  = new Dictionary<string, string> { ["KCAP_AGENT_ID"] = "agent-1" }; // dormant: no marker

        CodexHostedAgentRuntimeFactory.ApplyChildEnv(childEnv, overlay);

        await Assert.That(childEnv.ContainsKey("KCAP_HOSTED_APPSERVER")).IsFalse();
        await Assert.That(childEnv["KCAP_AGENT_ID"]).IsEqualTo("agent-1");
        await Assert.That(childEnv["PATH"]).IsEqualTo("/usr/bin");
    }

    [Test]
    public async Task ApplyChildEnv_sets_the_marker_when_the_overlay_emits() {
        var childEnv = new Dictionary<string, string?>();
        var overlay  = new Dictionary<string, string> { ["KCAP_HOSTED_APPSERVER"] = "1" };

        CodexHostedAgentRuntimeFactory.ApplyChildEnv(childEnv, overlay);

        await Assert.That(childEnv["KCAP_HOSTED_APPSERVER"]).IsEqualTo("1");
    }

    [Test]
    [NotInParallel]
    public async Task Missing_hooks_on_the_app_server_route_fails_closed() {
        using var wt   = new TempDir(); // empty — no worktree hooks

        // Both hook scopes have to be genuinely absent or the fail-closed assertion proves nothing:
        // the worktree above is empty, and so is the home this points CODEX_HOME into.
        using var codexEnv = EnvScope.Exclusive("CODEX_HOME", Home.PathTo(".codex"));

        CodexAppServerSpawnFactory seam = (_, _, _, _, _, _, _) =>
            throw new InvalidOperationException("spawn must never be reached when hooks are missing");
        var factory = Factory(new RecordingPtyFactory(), appServerActive: true, seam);

        await Assert.ThrowsAsync<CodexHooksNotInstalledException>(
            () => factory.StartAsync(Ctx(isReviewFlow: true, wt.Path), CancellationToken.None));
    }

    [Test]
    [NotInParallel]
    public async Task Handshake_failure_disposes_the_spawned_child() {
        using var wt   = new TempDir();
        WriteWorktreeHooks(wt);
        await using var fake = new FakeCodexAppServer { ThreadId = "" }; // thread/start returns no id -> StartAsync throws

        using var codexEnv = EnvScope.Exclusive("CODEX_HOME", Home.PathTo(".codex"));

        var process = new FakeAcpProcess();
        CodexAppServerSpawnFactory seam = (_, _, _, _, _, _, _) =>
            Task.FromResult<(CodexAppServerConnection, IAcpProcess)>((fake.ConnectClient(), process));
        var factory = Factory(new RecordingPtyFactory(), appServerActive: true, seam);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => factory.StartAsync(Ctx(isReviewFlow: true, wt.Path), CancellationToken.None).WaitAsync(HangGuard));

        // The factory disposed the runtime on the failed handshake, so the spawned child is not leaked.
        await Assert.That(process.HasExited).IsTrue();
    }

    static void WriteWorktreeHooks(TempDir worktree) =>
        worktree.CreateFile([".codex", "hooks.json"], """
            {"hooks":{
                "SessionStart":[{"hooks":[{"type":"command","command":"kcap hook --codex"}]}],
                "Stop":[{"hooks":[{"type":"command","command":"kcap hook --codex"}]}],
                "PermissionRequest":[{"hooks":[{"type":"command","command":"kcap hook --codex"}]}]
            }}
            """);
}
