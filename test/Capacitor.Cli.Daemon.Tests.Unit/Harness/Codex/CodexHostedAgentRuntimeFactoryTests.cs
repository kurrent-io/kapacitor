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
    static readonly TimeSpan HangGuard = TimeSpan.FromSeconds(5);

    // ── Test doubles ─────────────────────────────────────────────────────────────────────────
    sealed class RecordingPtyFactory : IHostedAgentRuntimeFactory {
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

    static CodexLauncher NewLauncher() =>
        new(new DaemonConfig { CodexPath = "codex", CapacitorPath = "/opt/kcap", ServerUrl = "https://t.example" },
            NullLogger<CodexLauncher>.Instance) {
            ReadInheritedMcpServers = static () => [],
        };

    static RuntimeStartContext Ctx(bool isReviewFlow, string worktreePath) => new(
        AgentId: "agent-1", Vendor: "codex", SourceRepoPath: worktreePath,
        Worktree: new WorktreeInfo(Path: worktreePath, Branch: "wt", SourceRepo: worktreePath),
        Prompt: null, Model: null, Effort: null, Tools: null,
        IsReview: false, IsReviewFlow: isReviewFlow, Review: null,
        Cols: 80, Rows: 24, ServerUrl: "https://t.example", DaemonBridgeUrl: null, CapacitorPath: "/opt/kcap");

    static CodexHostedAgentRuntimeFactory Factory(
            RecordingPtyFactory pty, bool appServerActive, CodexAppServerSpawnFactory? spawn = null) =>
        new(NewLauncher(), pty,
            new DaemonConfig { CodexAppServerActive = appServerActive, Version = "0.146.0" },
            NullLoggerFactory.Instance, spawn);

    // ── Routing decision ─────────────────────────────────────────────────────────────────────
    [Test]
    [Arguments(false, true,  false)] // not active -> pty even for a review flow
    [Arguments(true,  false, false)] // active but interactive -> pty
    [Arguments(true,  true,  true)]  // active + review flow -> app-server
    public async Task UsesAppServer_gates_on_active_AND_review_flow(bool active, bool isReviewFlow, bool expected) {
        using var wt = new TempDir();
        var factory = Factory(new RecordingPtyFactory(), active);
        await Assert.That(factory.UsesAppServer(Ctx(isReviewFlow, wt.Path))).IsEqualTo(expected);
    }

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
    [NotInParallel("HomeEnvVarMutation")]
    public async Task Active_review_flow_produces_an_app_server_runtime_via_the_spawn_seam() {
        var originalHome = Environment.GetEnvironmentVariable("HOME");
        var originalCodexHome = Environment.GetEnvironmentVariable("CODEX_HOME");
        using var home = new TempDir();
        using var wt   = new TempDir();
        WriteWorktreeHooks(wt);
        await using var fake = new FakeCodexAppServer();

        try {
            Environment.SetEnvironmentVariable("HOME", home.Path);
            Environment.SetEnvironmentVariable("CODEX_HOME", home.PathTo(".codex"));

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
        } finally {
            Environment.SetEnvironmentVariable("HOME", originalHome);
            Environment.SetEnvironmentVariable("CODEX_HOME", originalCodexHome);
        }
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
    [NotInParallel("HomeEnvVarMutation")]
    public async Task App_server_launch_leaves_the_marker_unset_while_dormant() {
        // The activation slice has not flipped emitEnvelopeTranscript, so a real app-server launch must
        // NOT stamp the marker — otherwise the shipped reviewers (still hook-ingested) would lose the watcher.
        var originalHome = Environment.GetEnvironmentVariable("HOME");
        var originalCodexHome = Environment.GetEnvironmentVariable("CODEX_HOME");
        using var home = new TempDir();
        using var wt   = new TempDir();
        WriteWorktreeHooks(wt);
        await using var fake = new FakeCodexAppServer();
        IReadOnlyDictionary<string, string>? capturedEnv = null;

        try {
            Environment.SetEnvironmentVariable("HOME", home.Path);
            Environment.SetEnvironmentVariable("CODEX_HOME", home.PathTo(".codex"));

            CodexAppServerSpawnFactory seam = (_, _, _, _, env, _, _) => {
                capturedEnv = env;
                return Task.FromResult<(CodexAppServerConnection, IAcpProcess)>((fake.ConnectClient(), new FakeAcpProcess()));
            };
            var factory = Factory(new RecordingPtyFactory(), appServerActive: true, seam);

            var start = await factory.StartAsync(Ctx(isReviewFlow: true, wt.Path), CancellationToken.None).WaitAsync(HangGuard);
            await start.Runtime.DisposeAsync();
        } finally {
            Environment.SetEnvironmentVariable("HOME", originalHome);
            Environment.SetEnvironmentVariable("CODEX_HOME", originalCodexHome);
        }

        await Assert.That(capturedEnv).IsNotNull();
        await Assert.That(capturedEnv!.ContainsKey("KCAP_HOSTED_APPSERVER")).IsFalse();
    }

    [Test]
    [NotInParallel("HomeEnvVarMutation")]
    public async Task Missing_hooks_on_the_app_server_route_fails_closed() {
        var originalHome = Environment.GetEnvironmentVariable("HOME");
        var originalCodexHome = Environment.GetEnvironmentVariable("CODEX_HOME");
        using var home = new TempDir(); // empty — no user-scope hooks
        using var wt   = new TempDir(); // empty — no worktree hooks

        try {
            Environment.SetEnvironmentVariable("HOME", home.Path);
            Environment.SetEnvironmentVariable("CODEX_HOME", home.PathTo(".codex"));

            CodexAppServerSpawnFactory seam = (_, _, _, _, _, _, _) =>
                throw new InvalidOperationException("spawn must never be reached when hooks are missing");
            var factory = Factory(new RecordingPtyFactory(), appServerActive: true, seam);

            await Assert.ThrowsAsync<CodexHooksNotInstalledException>(
                () => factory.StartAsync(Ctx(isReviewFlow: true, wt.Path), CancellationToken.None));
        } finally {
            Environment.SetEnvironmentVariable("HOME", originalHome);
            Environment.SetEnvironmentVariable("CODEX_HOME", originalCodexHome);
        }
    }

    [Test]
    [NotInParallel("HomeEnvVarMutation")]
    public async Task Handshake_failure_disposes_the_spawned_child() {
        var originalHome = Environment.GetEnvironmentVariable("HOME");
        var originalCodexHome = Environment.GetEnvironmentVariable("CODEX_HOME");
        using var home = new TempDir();
        using var wt   = new TempDir();
        WriteWorktreeHooks(wt);
        await using var fake = new FakeCodexAppServer { ThreadId = "" }; // thread/start returns no id -> StartAsync throws

        try {
            Environment.SetEnvironmentVariable("HOME", home.Path);
            Environment.SetEnvironmentVariable("CODEX_HOME", home.PathTo(".codex"));

            var process = new FakeAcpProcess();
            CodexAppServerSpawnFactory seam = (_, _, _, _, _, _, _) =>
                Task.FromResult<(CodexAppServerConnection, IAcpProcess)>((fake.ConnectClient(), process));
            var factory = Factory(new RecordingPtyFactory(), appServerActive: true, seam);

            await Assert.ThrowsAsync<InvalidOperationException>(
                () => factory.StartAsync(Ctx(isReviewFlow: true, wt.Path), CancellationToken.None).WaitAsync(HangGuard));

            // The factory disposed the runtime on the failed handshake, so the spawned child is not leaked.
            await Assert.That(process.HasExited).IsTrue();
        } finally {
            Environment.SetEnvironmentVariable("HOME", originalHome);
            Environment.SetEnvironmentVariable("CODEX_HOME", originalCodexHome);
        }
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
