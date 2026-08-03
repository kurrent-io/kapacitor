using System.Collections.Concurrent;
using System.Net.Sockets;
using System.Runtime.CompilerServices;
using System.Text.Json;
using Capacitor.Cli.Core;
using Capacitor.Cli.Core.LocalIpc;
using Capacitor.Cli.Daemon;
using Capacitor.Cli.Daemon.Pty;
using Capacitor.Cli.Daemon.Services;
using Capacitor.Cli.Tests.Unit.Daemon;
using Microsoft.Extensions.Logging.Abstractions;

namespace Capacitor.Cli.Tests.Unit;

/// Local-attach (Phase 1) behaviours on AgentOrchestrator. Partial of
/// AgentOrchestratorVendorTests to reuse its BuildOrchestrator + test doubles.
public partial class AgentOrchestratorVendorTests {
    sealed class NoopRestartStrategy : IRestartStrategy { public RestartOutcome Restart() => RestartOutcome.NoOp; }

    static RestartCoordinator TestCoordinator() =>
        RestartCoordinator.ForTest("test", "test", new NoopRestartStrategy());

    // Consent: a fresh, throwaway consent store/broker pair — these pre-existing LocalControlServer
    // tests don't exercise consent at all, so the wiring only needs to satisfy the ctor.
    static LaunchConsentIpc TestConsentIpc() {
        var dir = Directory.CreateTempSubdirectory("kcap-consent-ipc-").FullName;
        return new LaunchConsentIpc(
            new LaunchConsentBroker(),
            new LaunchConsentStore(dir, NullLogger.Instance),
            NullLogger<LaunchConsentIpc>.Instance);
    }

    // Status: a throwaway connection + notifier — these pre-existing LocalControlServer tests
    // don't exercise StatusSubscribe at all, so the wiring only needs to satisfy the ctor.
    static DaemonStatusIpc TestStatusIpc(DaemonConfig config, AgentOrchestrator orch) =>
        new(config, orch, new ServerConnection(config, NullLoggerFactory.Instance, NullLogger<ServerConnection>.Instance),
            new DaemonStatusNotifier());

    static DaemonConfig LauncherCfg() => new() { Name = "t", ServerUrl = "http://127.0.0.1:1" };

    static LauncherContext CtxFor(string path)
        => new("a", path, new WorktreeInfo(path, "", path, IsStandalone: true), null, "", null, null, false, false, null, null) {
            Work = WorkLocation.BorrowedCwd
        };

    [Test]
    public async Task Claude_borrowed_cwd_prepare_writes_no_repo_files() {
        var dir = Directory.CreateTempSubdirectory("kcap-inplace-");

        try {
            var launcher = new ClaudeLauncher(LauncherCfg(), NullLogger<ClaudeLauncher>.Instance);
            launcher.Prepare(CtxFor(dir.FullName));

            await Assert.That(File.Exists(Path.Combine(dir.FullName, ".mcp.json"))).IsFalse();
            await Assert.That(File.Exists(Path.Combine(dir.FullName, ".claude", "settings.local.json"))).IsFalse();
            await Assert.That(Directory.Exists(Path.Combine(dir.FullName, ".claude"))).IsFalse();
        } finally {
            Directory.Delete(dir.FullName, true);
        }
    }

    [Test]
    public async Task Claude_passthrough_forwards_user_args_verbatim() {
        var launcher = new ClaudeLauncher(LauncherCfg(), NullLogger<ClaudeLauncher>.Instance);
        var a = launcher.BuildPassthrough(CtxFor("/r"), ["--model", "opus", "fix it"]);
        await Assert.That(a.Args).IsEquivalentTo(new[] { "--model", "opus", "fix it" });
    }

    [Test]
    public async Task Codex_passthrough_injects_mandatory_flags_then_user_args() {
        var launcher = new CodexLauncher(LauncherCfg(), NullLogger<CodexLauncher>.Instance);
        var a = launcher.BuildPassthrough(CtxFor("/r"), ["-m", "gpt"]);
        await Assert.That(a.Args).Contains("--cd");
        await Assert.That(a.Args).Contains("--no-alt-screen");
        await Assert.That(a.Args[^2]).IsEqualTo("-m");
        await Assert.That(a.Args[^1]).IsEqualTo("gpt");
    }

    [Test]
    public async Task Codex_passthrough_rejects_user_duplicate_of_mandatory_flag() {
        var launcher = new CodexLauncher(LauncherCfg(), NullLogger<CodexLauncher>.Instance);
        await Assert.That(() => launcher.BuildPassthrough(CtxFor("/r"), ["--cd", "/elsewhere"]))
            .Throws<ArgumentException>();
    }

    [Test]
    public async Task Borrowed_cwd_cleanup_does_not_delete_user_dir_or_branch() {
        var (repoPath, cleanup) = CreateGitRepo();

        try {
            var server = new CaptureServerConnection();
            await using var orch = BuildOrchestrator(server, new SpyPtyProcessFactory(), new Dictionary<string, IHostedAgentLauncher>());

            // IsStandalone:true means RemoveAsync WOULD Directory.Delete this path —
            // the Work=BorrowedCwd guard must prevent that.
            var agent = new AgentInstance(
                "local-1", null, "", null, repoPath, "claude",
                new PtyHostedAgentRuntime("claude", new StubPtyProcess()), new WorktreeInfo(repoPath, "", repoPath, IsStandalone: true), new CancellationTokenSource()
            ) {
                IsPrivate = true,
                Work      = WorkLocation.BorrowedCwd
            };

            orch.RegisterAgentForTest(agent);
            await orch.CleanupAgentForTest("local-1");

            await Assert.That(Directory.Exists(repoPath)).IsTrue();
            await Assert.That(File.Exists(Path.Combine(repoPath, "README.md"))).IsTrue();
        } finally {
            cleanup();
        }
    }

    [Test]
    public async Task Owned_worktree_cleanup_still_removes_it() {
        var dir = Directory.CreateTempSubdirectory("kcap-owned-");

        try {
            var server = new CaptureServerConnection();
            await using var orch = BuildOrchestrator(server, new SpyPtyProcessFactory(), new Dictionary<string, IHostedAgentLauncher>());

            var agent = new AgentInstance(
                "owned-1", null, "", null, dir.FullName, "claude",
                new PtyHostedAgentRuntime("claude", new StubPtyProcess()), new WorktreeInfo(dir.FullName, "", dir.FullName, IsStandalone: true), new CancellationTokenSource()
            ) {
                Work = WorkLocation.OwnedWorktree
            };

            orch.RegisterAgentForTest(agent);
            await orch.CleanupAgentForTest("owned-1");

            await Assert.That(Directory.Exists(dir.FullName)).IsFalse();
        } finally {
            try { Directory.Delete(dir.FullName, true); } catch { /* already gone — that's the assertion */ }
        }
    }

    [Test]
    public async Task Private_spawn_makes_no_server_calls_and_omits_hosted_agent_env() {
        var dir = Directory.CreateTempSubdirectory("kcap-priv-");

        try {
            var server    = new TripwireServerConnection();
            var pty       = new EnvCapturingPtyFactory();
            var launchers = new Dictionary<string, IHostedAgentLauncher> { ["claude"] = new SpyHostedAgentLauncher("claude", "spy-claude") };

            await using var orch = BuildOrchestrator(server, pty, launchers);

            // Client read side: one Detach frame so the attach loop returns promptly.
            var readBuf = new MemoryStream();
            await FrameCodec.WriteAsync(readBuf, LocalFrame.Detach(), default);
            readBuf.Position = 0;
            using var client = new DuplexTestStream(readBuf, new MemoryStream());

            var spawn = FrameCodec.Spawn("claude", WorkLocation.BorrowedCwd, isPrivate: true, dir.FullName, ["--model", "opus"], 80, 24);
            await orch.HandleLocalSpawnAsync(spawn, client, default);

            // Let the fire-and-forget read loop + cleanup finish, then assert no server call landed.
            var deadline = DateTime.UtcNow.AddSeconds(5);
            while (orch.ActiveAgentCountForTest > 0 && DateTime.UtcNow < deadline) await Task.Delay(20);

            await Assert.That(server.Calls.Count).IsEqualTo(0);
            await Assert.That(pty.LastEnv!.ContainsKey("KCAP_URL")).IsTrue();            // records as a plain local session
            await Assert.That(pty.LastEnv!.ContainsKey("KCAP_AGENT_ID")).IsFalse();      // unregistered in Phase 1 → no agent_host_id tag
            await Assert.That(pty.LastEnv!.ContainsKey("KCAP_RENDERED_AGENT")).IsFalse(); // native terminal permissions
            await Assert.That(pty.LastEnv!.ContainsKey("KCAP_DAEMON_URL")).IsFalse();
        } finally {
            Directory.Delete(dir.FullName, true);
        }
    }

    [Test]
    public async Task RegisterAgentAsync_registers_public_agent_and_skips_private() {
        var server = new TripwireServerConnection();
        await using var orch = BuildOrchestrator(server, new SpyPtyProcessFactory(), new Dictionary<string, IHostedAgentLauncher>());

        var pub = new AgentInstance("pub-1", null, "", null, "/r", "claude",
            new PtyHostedAgentRuntime("claude", new StubPtyProcess()), new WorktreeInfo("/r", "", "/r"), new CancellationTokenSource()) { IsPrivate = false };
        await orch.RegisterAgentForTestAsync(pub);
        await Assert.That(server.Calls).Contains(nameof(ServerConnection.AgentRegisteredAsync));

        // Assert the private-agent skip on a fresh orchestrator + connection rather than
        // clearing the public one's Calls. RegisterAgentAsync for a public agent kicks off
        // fire-and-forget background work (the UpdateRepoPathsAsync Task.Run, gated behind
        // RepoPathStore file I/O, and AppendAgentRunEventAsync) that can land in Calls AFTER
        // a Clear() under slow I/O — a timing race that flaked CI. A pristine connection that
        // only ever sees the private registration must see zero calls, since RegisterAgentAsync
        // returns immediately for private agents without touching the server.
        var privServer = new TripwireServerConnection();
        await using var privOrch = BuildOrchestrator(privServer, new SpyPtyProcessFactory(), new Dictionary<string, IHostedAgentLauncher>());
        var priv = new AgentInstance("priv-1", null, "", null, "/r", "claude",
            new PtyHostedAgentRuntime("claude", new StubPtyProcess()), new WorktreeInfo("/r", "", "/r"), new CancellationTokenSource()) { IsPrivate = true };
        await privOrch.RegisterAgentForTestAsync(priv);
        await Assert.That(privServer.Calls.Count).IsEqualTo(0);
    }

    [Test]
    public async Task Registered_spawn_calls_server_and_sets_hosted_env() {
        var dir = Directory.CreateTempSubdirectory("kcap-reg-");

        try {
            var server    = new TripwireServerConnection();
            var pty       = new EnvCapturingPtyFactory();
            var launchers = new Dictionary<string, IHostedAgentLauncher> { ["claude"] = new SpyHostedAgentLauncher("claude", "spy-claude") };

            await using var orch = BuildOrchestrator(server, pty, launchers);

            var readBuf = new MemoryStream();
            await FrameCodec.WriteAsync(readBuf, LocalFrame.Detach(), default);
            readBuf.Position = 0;
            using var client = new DuplexTestStream(readBuf, new MemoryStream());

            var spawn = FrameCodec.Spawn("claude", WorkLocation.BorrowedCwd, isPrivate: false, dir.FullName, ["--model", "opus"], 80, 24);
            await orch.HandleLocalSpawnAsync(spawn, client, default);

            var deadline = DateTime.UtcNow.AddSeconds(5);
            while (orch.ActiveAgentCountForTest > 0 && DateTime.UtcNow < deadline) await Task.Delay(20);

            await Assert.That(server.Calls).Contains(nameof(ServerConnection.AgentRegisteredAsync));
            await Assert.That(pty.LastEnv!.ContainsKey("KCAP_URL")).IsTrue();
            await Assert.That(pty.LastEnv!.ContainsKey("KCAP_AGENT_ID")).IsTrue();
            await Assert.That(pty.LastEnv!.ContainsKey("KCAP_RENDERED_AGENT")).IsTrue();
        } finally {
            Directory.Delete(dir.FullName, true);
        }
    }

    // Consent: the owner consent gate lives in HandleLaunchAgentCore (the SERVER-driven launch
    // choke point) only. The local 0600 socket path (kcap agent start -> HandleLocalSpawnAsync)
    // never calls that method, so a deny-default gate must not stop it — that socket is the
    // owner's by construction.
    [Test]
    public async Task Local_spawn_bypasses_consent_under_deny_default() {
        var dir = Directory.CreateTempSubdirectory("kcap-consent-local-");
        var consentDir = Directory.CreateTempSubdirectory("kcap-consent-local-state-").FullName;

        try {
            var server    = new TripwireServerConnection();
            var pty       = new EnvCapturingPtyFactory();
            var launchers = new Dictionary<string, IHostedAgentLauncher> { ["claude"] = new SpyHostedAgentLauncher("claude", "spy-claude") };

            await using var orch = BuildOrchestrator(server, pty, launchers, consentGate: DenyDefaultGate(consentDir));

            var readBuf = new MemoryStream();
            await FrameCodec.WriteAsync(readBuf, LocalFrame.Detach(), default);
            readBuf.Position = 0;
            using var client = new DuplexTestStream(readBuf, new MemoryStream());

            var spawn = FrameCodec.Spawn("claude", WorkLocation.BorrowedCwd, isPrivate: false, dir.FullName, ["--model", "opus"], 80, 24);
            await orch.HandleLocalSpawnAsync(spawn, client, default);

            var deadline = DateTime.UtcNow.AddSeconds(5);
            while (orch.ActiveAgentCountForTest > 0 && DateTime.UtcNow < deadline) await Task.Delay(20);

            // Spawn succeeds exactly like Registered_spawn_calls_server_and_sets_hosted_env above —
            // same assertions, proving consent was never consulted on this path (a deny-default
            // gate would otherwise make AgentRegisteredAsync unreachable).
            await Assert.That(server.Calls).Contains(nameof(ServerConnection.AgentRegisteredAsync));
            await Assert.That(pty.LastEnv!.ContainsKey("KCAP_URL")).IsTrue();
            await Assert.That(pty.LastEnv!.ContainsKey("KCAP_AGENT_ID")).IsTrue();
            await Assert.That(pty.LastEnv!.ContainsKey("KCAP_RENDERED_AGENT")).IsTrue();
        } finally {
            Directory.Delete(dir.FullName, true);
        }
    }

    [Test]
    public async Task Reconnect_resends_stored_dims_not_the_hosted_constant() {
        var server = new TripwireServerConnection();
        await using var orch = BuildOrchestrator(server, new SpyPtyProcessFactory(), new Dictionary<string, IHostedAgentLauncher>());

        orch.RegisterAgentForTest(new AgentInstance("reg-1", null, "", null, "/r", "claude",
            new PtyHostedAgentRuntime("claude", new StubPtyProcess()), new WorktreeInfo("/r", "", "/r"), new CancellationTokenSource()) {
            IsPrivate = false, Status = "Running", CurrentCols = 73, CurrentRows = 19
        });

        await orch.ReRegisterAgentsForTestAsync();

        await Assert.That(server.LastDims).IsEqualTo((73, 19));
    }

    [Test]
    public async Task Web_resize_updates_stored_dims_then_reconnect_resends_them() {
        var server = new TripwireServerConnection();
        await using var orch = BuildOrchestrator(server, new SpyPtyProcessFactory(), new Dictionary<string, IHostedAgentLauncher>());

        var agent = new AgentInstance("reg-2", null, "", null, "/r", "claude",
            new PtyHostedAgentRuntime("claude", new StubPtyProcess()), new WorktreeInfo("/r", "", "/r"), new CancellationTokenSource()) {
            IsPrivate = false, Status = "Running", CurrentCols = 80, CurrentRows = 24
        };
        orch.RegisterAgentForTest(agent);

        orch.HandleResizeTerminalForTest(new ResizeTerminalCommand("reg-2", 51, 200));
        await Assert.That(agent.CurrentCols).IsEqualTo((ushort)51);
        await Assert.That(agent.CurrentRows).IsEqualTo((ushort)200);

        await orch.ReRegisterAgentsForTestAsync();
        await Assert.That(server.LastDims).IsEqualTo((51, 200));
    }

    [Test]
    public async Task Web_resize_min_clamps_per_dimension_with_local_client() {
        var server = new TripwireServerConnection();
        await using var orch = BuildOrchestrator(server, new SpyPtyProcessFactory(), new Dictionary<string, IHostedAgentLauncher>());

        var agent = new AgentInstance("reg-3", null, "", null, "/r", "claude",
            new PtyHostedAgentRuntime("claude", new StubPtyProcess()), new WorktreeInfo("/r", "", "/r"), new CancellationTokenSource()) {
            IsPrivate = false, Status = "Running", CurrentCols = 80, CurrentRows = 24
        };
        // A local client reports 80×24; the web viewer wants 120×40.
        agent.ClientDims[new FakeTerminalSink()] = new AgentInstance.Dim(80, 24);
        orch.RegisterAgentForTest(agent);

        orch.HandleResizeTerminalForTest(new ResizeTerminalCommand("reg-3", 120, 40));

        // Per-dimension min across local ∪ web: cols min(80,120)=80, rows min(24,40)=24.
        await Assert.That(agent.CurrentCols).IsEqualTo((ushort)80);
        await Assert.That(agent.CurrentRows).IsEqualTo((ushort)24);
        await Assert.That(server.LastDims).IsEqualTo((80, 24)); // clamped size announced back to web
    }

    [Test]
    public async Task Web_resize_shrinks_pty_below_a_larger_local_client() {
        var server = new TripwireServerConnection();
        await using var orch = BuildOrchestrator(server, new SpyPtyProcessFactory(), new Dictionary<string, IHostedAgentLauncher>());

        var agent = new AgentInstance("reg-4", null, "", null, "/r", "claude",
            new PtyHostedAgentRuntime("claude", new StubPtyProcess()), new WorktreeInfo("/r", "", "/r"), new CancellationTokenSource()) {
            IsPrivate = false, Status = "Running", CurrentCols = 200, CurrentRows = 50
        };
        agent.ClientDims[new FakeTerminalSink()] = new AgentInstance.Dim(200, 50);
        orch.RegisterAgentForTest(agent);

        orch.HandleResizeTerminalForTest(new ResizeTerminalCommand("reg-4", 100, 30));

        // The smaller web viewer wins both dimensions.
        await Assert.That(agent.CurrentCols).IsEqualTo((ushort)100);
        await Assert.That(agent.CurrentRows).IsEqualTo((ushort)30);
    }

    [Test]
    public async Task Web_resize_zero_dims_clears_web_and_grows_back_to_local() {
        var server = new TripwireServerConnection();
        await using var orch = BuildOrchestrator(server, new SpyPtyProcessFactory(), new Dictionary<string, IHostedAgentLauncher>());

        var agent = new AgentInstance("reg-5", null, "", null, "/r", "claude",
            new PtyHostedAgentRuntime("claude", new StubPtyProcess()), new WorktreeInfo("/r", "", "/r"), new CancellationTokenSource()) {
            IsPrivate = false, Status = "Running", CurrentCols = 150, CurrentRows = 40
        };
        agent.ClientDims[new FakeTerminalSink()] = new AgentInstance.Dim(150, 40);
        orch.RegisterAgentForTest(agent);

        // A small web viewer attaches and clamps the PTY down.
        orch.HandleResizeTerminalForTest(new ResizeTerminalCommand("reg-5", 80, 20));
        await Assert.That(agent.CurrentCols).IsEqualTo((ushort)80);
        await Assert.That(agent.CurrentRows).IsEqualTo((ushort)20);

        // Last web viewer leaves: the server sends (0,0); the PTY grows back to the local size.
        orch.HandleResizeTerminalForTest(new ResizeTerminalCommand("reg-5", 0, 0));
        await Assert.That(agent.WebDims).IsNull();
        await Assert.That(agent.CurrentCols).IsEqualTo((ushort)150);
        await Assert.That(agent.CurrentRows).IsEqualTo((ushort)40);
    }

    [Test]
    public async Task Web_resize_out_of_ushort_range_is_ignored() {
        var server = new TripwireServerConnection();
        await using var orch = BuildOrchestrator(server, new SpyPtyProcessFactory(), new Dictionary<string, IHostedAgentLauncher>());

        var agent = new AgentInstance("reg-6", null, "", null, "/r", "claude",
            new PtyHostedAgentRuntime("claude", new StubPtyProcess()), new WorktreeInfo("/r", "", "/r"), new CancellationTokenSource()) {
            IsPrivate = false, Status = "Running", CurrentCols = 120, CurrentRows = 40
        };
        agent.ClientDims[new FakeTerminalSink()] = new AgentInstance.Dim(120, 40);
        orch.RegisterAgentForTest(agent);

        // 70000 > ushort.MaxValue — would wrap to 4464 on a raw (ushort) cast. The guard ignores it
        // so WebDims stays null and the clamp is untouched (no poisoned web entry).
        orch.HandleResizeTerminalForTest(new ResizeTerminalCommand("reg-6", 70000, 40));

        await Assert.That(agent.WebDims).IsNull();
        await Assert.That(agent.CurrentCols).IsEqualTo((ushort)120);
        await Assert.That(agent.CurrentRows).IsEqualTo((ushort)40);
    }

    [Test]
    public async Task Private_agent_ignores_server_origin_resize_and_stop() {
        var server = new CaptureServerConnection();
        await using var orch = BuildOrchestrator(server, new SpyPtyProcessFactory(), new Dictionary<string, IHostedAgentLauncher>());

        var agent = new AgentInstance("priv-2", null, "", null, "/r", "claude",
            new PtyHostedAgentRuntime("claude", new StubPtyProcess()), new WorktreeInfo("/r", "", "/r"), new CancellationTokenSource()) {
            IsPrivate = true, Status = "Running", CurrentCols = 80, CurrentRows = 24
        };
        orch.RegisterAgentForTest(agent);

        orch.HandleResizeTerminalForTest(new ResizeTerminalCommand("priv-2", 51, 200));
        await Assert.That(agent.CurrentCols).IsEqualTo((ushort)80); // server-origin resize ignored

        await orch.HandleStopAgentForTest("priv-2");
        await Assert.That(agent.Status).IsEqualTo("Running");       // server-origin stop ignored
    }

    [Test]
    [NotInParallel]
    public async Task Registered_spawn_env_includes_daemon_bridge_url_and_preserves_api_key() {
        var dir     = Directory.CreateTempSubdirectory("kcap-reg-env-");
        var prevKey = Environment.GetEnvironmentVariable("ANTHROPIC_API_KEY");
        Environment.SetEnvironmentVariable("ANTHROPIC_API_KEY", "sk-test-key");

        try {
            var server    = new TripwireServerConnection();
            var pty       = new EnvCapturingPtyFactory();
            var launchers = new Dictionary<string, IHostedAgentLauncher> { ["claude"] = new SpyHostedAgentLauncher("claude", "spy-claude") };

            await using var orch = BuildOrchestrator(server, pty, launchers);
            await orch.PermissionBridgeForTest.StartAsync(default); // binds 127.0.0.1 + sets BaseUrl

            try {
                var readBuf = new MemoryStream();
                await FrameCodec.WriteAsync(readBuf, LocalFrame.Detach(), default);
                readBuf.Position = 0;
                using var client = new DuplexTestStream(readBuf, new MemoryStream());

                var spawn = FrameCodec.Spawn("claude", WorkLocation.BorrowedCwd, isPrivate: false, dir.FullName, [], 80, 24);
                await orch.HandleLocalSpawnAsync(spawn, client, default);

                var deadline = DateTime.UtcNow.AddSeconds(5);
                while (orch.ActiveAgentCountForTest > 0 && DateTime.UtcNow < deadline) await Task.Delay(20);

                await Assert.That(pty.LastEnv!["KCAP_DAEMON_URL"]).IsEqualTo(orch.PermissionBridgeForTest.BaseUrl);
                await Assert.That(pty.LastEnv!["ANTHROPIC_API_KEY"]).IsEqualTo("sk-test-key");
            } finally {
                await orch.PermissionBridgeForTest.StopAsync(default);
            }
        } finally {
            Environment.SetEnvironmentVariable("ANTHROPIC_API_KEY", prevKey);
            Directory.Delete(dir.FullName, true);
        }
    }

    [Test]
    public async Task Private_agents_are_excluded_from_live_agent_ids() {
        var server = new CaptureServerConnection();
        await using var orch = BuildOrchestrator(server, new SpyPtyProcessFactory(), new Dictionary<string, IHostedAgentLauncher>());

        orch.RegisterAgentForTest(new AgentInstance("pub-1", null, "", null, "/r", "claude",
            new PtyHostedAgentRuntime("claude", new StubPtyProcess()), new WorktreeInfo("/r", "", "/r"), new CancellationTokenSource()) { IsPrivate = false, Status = "Running" });
        orch.RegisterAgentForTest(new AgentInstance("priv-1", null, "", null, "/r", "claude",
            new PtyHostedAgentRuntime("claude", new StubPtyProcess()), new WorktreeInfo("/r", "", "/r"), new CancellationTokenSource()) { IsPrivate = true, Status = "Running" });

        var ids = server.GetLiveAgentIds!();

        await Assert.That(ids).Contains("pub-1");
        await Assert.That(ids).DoesNotContain("priv-1");
    }

    [Test]
    public async Task Attach_to_an_ACP_runtime_gets_an_error_frame_and_detaches_instead_of_crashing() {
        var server = new CaptureServerConnection();
        await using var orch = BuildOrchestrator(server, new SpyPtyProcessFactory(), new Dictionary<string, IHostedAgentLauncher>());

        // A cursor/ACP-backed agent: SendRawInputAsync throws NotSupportedException, exactly like
        // the real AcpHostedAgentRuntime (local attach is a PTY-only surface).
        var runtime = new NoRawInputRuntime("cursor");
        var agent = new AgentInstance(
            "acp-1", null, "", null, "/r", "cursor",
            runtime, new WorktreeInfo("/r", "", "/r", IsStandalone: true), new CancellationTokenSource()
        );
        orch.RegisterAgentForTest(agent);

        // Client sends one Stdin frame, then nothing (stream ends) — mirrors `kcap agent attach`
        // forwarding a keystroke to a runtime that can't accept raw input.
        var readBuf = new MemoryStream();
        await FrameCodec.WriteAsync(readBuf, LocalFrame.Stdin("x"u8.ToArray()), default);
        readBuf.Position = 0;
        using var client = new DuplexTestStream(readBuf, new MemoryStream());

        // Must complete (not throw) within a bounded time — the bug this guards against was an
        // unhandled NotSupportedException escaping the read loop and crashing the attach handler.
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await orch.HandleLocalAttachAsync("acp-1", client, cts.Token);

        // Replay the frames the client received off the write side of the duplex stream.
        client.WrittenStream.Position = 0;
        var frames = new List<LocalFrame>();
        while (await FrameCodec.ReadAsync(client.WrittenStream, default) is { } f) frames.Add(f);

        await Assert.That(frames.Any(f => f.Type == FrameType.Attached)).IsTrue();
        var error = frames.SingleOrDefault(f => f.Type == FrameType.Error);
        await Assert.That(error).IsNotNull();
        await Assert.That(error!.Text).Contains("does not support local attach input");

        // The agent itself is untouched — attach failure detaches the client, it doesn't stop
        // or crash the underlying runtime.
        await Assert.That(runtime.Disposed).IsFalse();
    }

    /// <summary>Minimal <see cref="IHostedAgentRuntime"/> double mirroring AcpHostedAgentRuntime's
    /// contract: no raw-input surface (throws NotSupportedException), never emits terminal output.</summary>
    sealed class NoRawInputRuntime(string vendor) : IHostedAgentRuntime {
        public bool Disposed { get; private set; }

        public string Vendor              => vendor;
        public int    Pid                 => 4242;
        public bool   HasExited           => false;
        public int?   ExitCode            => null;
        public bool   EmitsTerminalOutput => false;

#pragma warning disable CS1998
        public async IAsyncEnumerable<byte[]> ReadOutputAsync([EnumeratorCancellation] CancellationToken ct = default) {
            yield break;
        }
#pragma warning restore CS1998

        public Task SendUserInputAsync(string text)   => Task.CompletedTask;
        public Task SendSpecialKeyAsync(string key)   => Task.CompletedTask;
        public Task SendRawInputAsync(byte[]   data)  => throw new NotSupportedException("Local-attach raw input is a PTY-only surface; the ACP runtime has no equivalent channel.");
        public void Resize(ushort               cols, ushort rows) { }
        public Task RequestGracefulStopAsync()        => Task.CompletedTask;
        public Task WaitForExitAsync(TimeSpan?  timeout = null) => Task.CompletedTask;
        public Task TerminateAsync(TimeSpan?    timeout = null) => Task.CompletedTask;

        public ValueTask DisposeAsync() { Disposed = true; return default; }
    }

    [Test]
    [NotInParallel(nameof(DaemonLockPaths) + ".OverrideDirectoryForTesting")]
    public async Task Local_socket_list_round_trips_registered_agents_over_a_real_socket() {
        if (OperatingSystem.IsWindows()) return; // Unix-domain socket path

        var sockDir = Directory.CreateTempSubdirectory("kcap-sock-");
        DaemonLockPaths.OverrideDirectoryForTesting(sockDir.FullName);
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));

        LocalControlServer? listener = null;
        AgentOrchestrator?  orch     = null;

        try {
            orch = BuildOrchestrator(new CaptureServerConnection(), new SpyPtyProcessFactory(), new Dictionary<string, IHostedAgentLauncher>());
            orch.RegisterAgentForTest(new AgentInstance(
                "agent-xyz", null, "", null, "/tmp/repo", "claude",
                new PtyHostedAgentRuntime("claude", new StubPtyProcess()), new WorktreeInfo("/tmp/repo", "", "/tmp/repo"), new CancellationTokenSource()
            ) {
                IsPrivate = true, Work = WorkLocation.BorrowedCwd, Status = "Running"
            });

            var config = new DaemonConfig { Name = "test", ServerUrl = "http://127.0.0.1:1" };
            listener = new LocalControlServer(config, orch, TestCoordinator(), TestConsentIpc(), TestStatusIpc(config, orch), NullLogger<LocalControlServer>.Instance);
            await listener.StartAsync(cts.Token);

            var sockPath = LocalSocketPaths.Socket("test");
            var deadline = DateTime.UtcNow.AddSeconds(5);
            while (!File.Exists(sockPath) && DateTime.UtcNow < deadline) await Task.Delay(20, cts.Token);
            await Assert.That(File.Exists(sockPath)).IsTrue();

            using var sock = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
            await sock.ConnectAsync(new UnixDomainSocketEndPoint(sockPath), cts.Token);
            await using var stream = new NetworkStream(sock, ownsSocket: false);

            await FrameCodec.WriteAsync(stream, new LocalFrame(FrameType.List), cts.Token);
            var resp = await FrameCodec.ReadAsync(stream, cts.Token);

            await Assert.That(resp!.Type).IsEqualTo(FrameType.AgentList);
            await Assert.That(resp.Text).Contains("agent-xyz");
            await Assert.That(resp.Text).Contains("Running");
        } finally {
            if (orch is not null) await orch.DisposeAsync();
            if (listener is not null) { await listener.StopAsync(CancellationToken.None); listener.Dispose(); }
            DaemonLockPaths.OverrideDirectoryForTesting(null);
            try { Directory.Delete(sockDir.FullName, true); } catch { /* best-effort */ }
        }
    }

    /// <summary>
    /// Pins the one hop nothing else exercises: <see cref="LocalControlServer"/> decoding a raw
    /// StopV2 frame off a real socket and forwarding its force flag to the orchestrator. The codec
    /// round-trip (FrameCodecTests) and the handler (StopV2AndReadReply above) are each covered in
    /// isolation; only a real connection proves the server's frame switch wires them together.
    /// </summary>
    static async Task<LocalFrame?> StopV2OverRealSocketAsync(string daemonName, bool force, string agentId) {
        var sockDir = Directory.CreateTempSubdirectory("kcap-sock-");
        DaemonLockPaths.OverrideDirectoryForTesting(sockDir.FullName);
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));

        LocalControlServer? listener = null;
        AgentOrchestrator?  orch     = null;

        try {
            orch = BuildOrchestrator(new TripwireServerConnection(), new SpyPtyProcessFactory(), new Dictionary<string, IHostedAgentLauncher>());
            orch.SeedAgentForTest("flow-1", kind: LaunchKind.ReviewFlow, flowRunId: "flow-7f3a", flowRole: "reviewer");

            var config = new DaemonConfig { Name = daemonName, ServerUrl = "http://127.0.0.1:1" };
            listener = new LocalControlServer(config, orch, TestCoordinator(), TestConsentIpc(), TestStatusIpc(config, orch), NullLogger<LocalControlServer>.Instance);
            await listener.StartAsync(cts.Token);

            var sockPath = LocalSocketPaths.Socket(daemonName);
            var deadline = DateTime.UtcNow.AddSeconds(5);
            while (!File.Exists(sockPath) && DateTime.UtcNow < deadline) await Task.Delay(20, cts.Token);
            await Assert.That(File.Exists(sockPath)).IsTrue();

            using var sock = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
            await sock.ConnectAsync(new UnixDomainSocketEndPoint(sockPath), cts.Token);
            await using var stream = new NetworkStream(sock, ownsSocket: false);

            await FrameCodec.WriteAsync(stream, LocalFrame.StopV2(force, agentId), cts.Token);

            return await FrameCodec.ReadAsync(stream, cts.Token);
        } finally {
            if (orch is not null) await orch.DisposeAsync();
            if (listener is not null) { await listener.StopAsync(CancellationToken.None); listener.Dispose(); }
            DaemonLockPaths.OverrideDirectoryForTesting(null);
            try { Directory.Delete(sockDir.FullName, true); } catch { /* best-effort */ }
        }
    }

    [Test]
    [NotInParallel(nameof(DaemonLockPaths) + ".OverrideDirectoryForTesting")]
    public async Task Local_socket_stopv2_without_force_refuses_a_protected_agent_end_to_end() {
        if (OperatingSystem.IsWindows()) return; // Unix-domain socket path

        var resp = await StopV2OverRealSocketAsync("test-stopv2-refuse", force: false, "flow-1");

        await Assert.That(resp!.Type).IsEqualTo(FrameType.Error);
        await Assert.That(resp.Text).Contains("--force");
    }

    [Test]
    [NotInParallel(nameof(DaemonLockPaths) + ".OverrideDirectoryForTesting")]
    public async Task Local_socket_stopv2_with_force_stops_a_protected_agent_end_to_end() {
        if (OperatingSystem.IsWindows()) return; // Unix-domain socket path

        var resp = await StopV2OverRealSocketAsync("test-stopv2-force", force: true, "flow-1");

        await Assert.That(resp!.Type).IsEqualTo(FrameType.StopAck);
        await Assert.That(resp.Text).IsEqualTo("flow-1\tstopped");
    }

    [Test]
    public async Task Local_list_reports_each_agent_kind_and_flow_identity() {
        var server = new TripwireServerConnection();
        await using var orch = BuildOrchestrator(server, new SpyPtyProcessFactory(), new Dictionary<string, IHostedAgentLauncher>());
        orch.SeedAgentForTest("plain-1");
        orch.SeedAgentForTest("rev-1", kind: LaunchKind.Review);
        orch.SeedAgentForTest("flow-1", kind: LaunchKind.ReviewFlow, flowRunId: "flow-7f3a", flowRole: "reviewer");

        using var client = new DuplexTestStream(new MemoryStream(), new MemoryStream());
        await orch.HandleLocalListAsync(client, default);
        client.WrittenStream.Position = 0;
        var reply = await FrameCodec.ReadAsync(client.WrittenStream, default);

        var rows = reply!.Text.Split('\n').Select(l => l.Split('\t')).ToDictionary(p => p[0], p => p);

        await Assert.That(rows["plain-1"][3]).IsEqualTo("agent");
        await Assert.That(rows["rev-1"][3]).IsEqualTo("review");
        await Assert.That(rows["flow-1"][3]).IsEqualTo("review-flow");
        await Assert.That(rows["flow-1"][4]).IsEqualTo("flow-7f3a");
        await Assert.That(rows["flow-1"][5]).IsEqualTo("reviewer");
    }

    [Test]
    public async Task Attaching_to_a_flow_participant_is_read_only() {
        var server = new TripwireServerConnection();
        await using var orch = BuildOrchestrator(server, new SpyPtyProcessFactory(), new Dictionary<string, IHostedAgentLauncher>());
        var pty   = new RecordingPtyProcess();
        var agent = orch.SeedAgentForTest("flow-1", kind: LaunchKind.ReviewFlow, flowRunId: "flow-7f3a", flowRole: "reviewer", pty: pty);

        // Client sends input, then a resize, then detaches. None of the first two may land.
        var readBuf = new MemoryStream();
        await FrameCodec.WriteAsync(readBuf, LocalFrame.Stdin("hello"u8.ToArray()), default);
        await FrameCodec.WriteAsync(readBuf, LocalFrame.Resize(40, 10), default);
        await FrameCodec.WriteAsync(readBuf, LocalFrame.Detach(), default);
        readBuf.Position = 0;
        using var client = new DuplexTestStream(readBuf, new MemoryStream());

        await orch.HandleLocalAttachAsync("flow-1", client, default);

        client.WrittenStream.Position = 0;
        var first = await FrameCodec.ReadAsync(client.WrittenStream, default);

        await Assert.That(first!.Type).IsEqualTo(FrameType.AttachedReadOnly);
        var (_, reason, _) = FrameCodec.AttachedReadOnly(first);
        await Assert.That(reason).Contains("review-flow");
        await Assert.That(reason).Contains("reviewer");

        // The resize must not have been recorded, so the PTY is never clamped to the viewer.
        await Assert.That(agent.ClientDims).IsEmpty();

        // The stdin frame must never reach the runtime — this is the daemon-side guarantee
        // itself, not just the client-observable frame type above.
        await Assert.That(pty.Writes).IsEmpty();
    }

    [Test]
    public async Task Attaching_to_a_plain_agent_stays_read_write() {
        var server = new TripwireServerConnection();
        await using var orch = BuildOrchestrator(server, new SpyPtyProcessFactory(), new Dictionary<string, IHostedAgentLauncher>());
        var pty = new RecordingPtyProcess();
        orch.SeedAgentForTest("plain-1", pty: pty);

        var readBuf = new MemoryStream();
        await FrameCodec.WriteAsync(readBuf, LocalFrame.Stdin("hello"u8.ToArray()), default);
        await FrameCodec.WriteAsync(readBuf, LocalFrame.Detach(), default);
        readBuf.Position = 0;
        using var client = new DuplexTestStream(readBuf, new MemoryStream());

        await orch.HandleLocalAttachAsync("plain-1", client, default);

        client.WrittenStream.Position = 0;
        var first = await FrameCodec.ReadAsync(client.WrittenStream, default);
        await Assert.That(first!.Type).IsEqualTo(FrameType.Attached);

        // Mirrors the read-only test: an unprotected agent's stdin really does reach the PTY.
        await Assert.That(pty.Writes).IsEquivalentTo(new[] { "hello" });
    }

    // ── Test doubles for the local-spawn lifecycle ──────────────────────

    sealed class EnvCapturingPtyFactory : IPtyProcessFactory {
        public Dictionary<string, string>? LastEnv { get; private set; }

        public IPtyProcess Spawn(string command, string[] args, string cwd, Dictionary<string, string>? extraEnv = null, ushort cols = 120, ushort rows = 40) {
            LastEnv = extraEnv;

            return new StubPtyProcess();
        }
    }

    /// Read and write go to separate underlying streams, so a test can preload client input
    /// while the daemon's frames are captured/discarded independently (a MemoryStream can't
    /// do both at once — it has a single position).
    sealed class DuplexTestStream(Stream readSide, Stream writeSide) : Stream {
        /// <summary>The daemon's write side, for tests that need to inspect frames it sent.</summary>
        public Stream WrittenStream => writeSide;

        public override int Read(byte[] b, int o, int c) => readSide.Read(b, o, c);
        public override ValueTask<int> ReadAsync(Memory<byte> b, CancellationToken ct = default) => readSide.ReadAsync(b, ct);
        public override void Write(byte[] b, int o, int c) => writeSide.Write(b, o, c);
        public override ValueTask WriteAsync(ReadOnlyMemory<byte> b, CancellationToken ct = default) => writeSide.WriteAsync(b, ct);
        public override void Flush() => writeSide.Flush();
        public override Task FlushAsync(CancellationToken ct) => writeSide.FlushAsync(ct);
        public override bool CanRead => true; public override bool CanWrite => true; public override bool CanSeek => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => 0; set { } }
        public override long Seek(long o, SeekOrigin s) => throw new NotSupportedException();
        public override void SetLength(long v) => throw new NotSupportedException();
        protected override void Dispose(bool disposing) { if (disposing) { readSide.Dispose(); writeSide.Dispose(); } }
    }

    /// A no-op local sink used only as a stable key to seed AgentInstance.ClientDims in resize
    /// tests (the real socket attach loop isn't needed to exercise the min-clamp).
    sealed class FakeTerminalSink : ITerminalSink {
        public void TryEnqueue(byte[] chunk) { }
        public bool Detached => false;
    }

    /// Records the name of any per-agent server method invoked. A PrivateLocal agent must
    /// invoke none of them — the test asserts Calls is empty.
    sealed class TripwireServerConnection() : ServerConnection(
        new() { Name = "test", ServerUrl = "http://127.0.0.1:1" },
        NullLoggerFactory.Instance,
        NullLogger<ServerConnection>.Instance
    ) {
        public ConcurrentBag<string> Calls { get; } = [];
        public (int Cols, int Rows)? LastDims { get; private set; }

        public override Task SendTerminalDimensionsAsync(string agentId, int cols, int rows) { LastDims = (cols, rows); Calls.Add(nameof(SendTerminalDimensionsAsync)); return Task.CompletedTask; }
        public override Task LaunchFailedAsync(string agentId, string reason) { Calls.Add(nameof(LaunchFailedAsync)); return Task.CompletedTask; }
        public override Task AgentRegisteredAsync(string agentId, string? prompt, string? model, string? effort, string? repoPath, string? sandboxPolicy = null, string? approvalPolicy = null) { Calls.Add(nameof(AgentRegisteredAsync)); return Task.CompletedTask; }
        public override Task AgentStatusChangedAsync(string agentId, string status, string? sessionId) { Calls.Add(nameof(AgentStatusChangedAsync)); return Task.CompletedTask; }
        public override Task AgentUnregisteredAsync(string agentId) { Calls.Add(nameof(AgentUnregisteredAsync)); return Task.CompletedTask; }
        public override Task UpdateRepoPathsAsync() { Calls.Add(nameof(UpdateRepoPathsAsync)); return Task.CompletedTask; }
        public override Task SendTerminalOutputAsync(string agentId, string base64Data, CancellationToken ct = default) { Calls.Add(nameof(SendTerminalOutputAsync)); return Task.CompletedTask; }
        public override Task AppendAgentRunEventAsync(string agentId, object evt) { Calls.Add(nameof(AppendAgentRunEventAsync)); return Task.CompletedTask; }
        public override Task<EndAgentSessionResult> EndAgentSessionAsync(string agentId, string reason) { Calls.Add(nameof(EndAgentSessionAsync)); return Task.FromResult(new EndAgentSessionResult()); }

        public override Task<PermissionDecision> RequestPermissionAsync(
                string sessionId, string? toolName, JsonElement? toolInput, JsonElement? suggestions, CancellationToken ct = default
            ) { Calls.Add(nameof(RequestPermissionAsync)); return Task.FromResult(new PermissionDecision("deny", null, null)); }
    }

    static async Task<LocalFrame?> StopAndReadReply(AgentOrchestrator orch, string agentId) {
        using var client = new DuplexTestStream(new MemoryStream(), new MemoryStream());
        await orch.HandleLocalStopAsync(agentId, client, default);
        client.WrittenStream.Position = 0;

        return await FrameCodec.ReadAsync(client.WrittenStream, default);
    }

    [Test]
    public async Task Local_stop_stops_a_private_agent_without_touching_the_server() {
        // The server-origin path refuses private agents by design; a local stop must not,
        // or a `--private` agent could never be stopped from the CLI at all.
        var server = new TripwireServerConnection();
        await using var orch = BuildOrchestrator(server, new SpyPtyProcessFactory(), new Dictionary<string, IHostedAgentLauncher>());
        orch.SeedAgentForTest("priv-1", isPrivate: true);

        var reply = await StopAndReadReply(orch, "priv-1");

        await Assert.That(reply!.Type).IsEqualTo(FrameType.StopAck);
        await Assert.That(reply.Text).IsEqualTo("priv-1\tstopped");
        await Assert.That(orch.GetAgentForTest("priv-1")!.Status).IsEqualTo("Completed");
        await Assert.That(server.Calls.Count).IsEqualTo(0);
    }

    [Test]
    public async Task Local_stop_of_a_registered_agent_still_reports_to_the_server() {
        var server = new TripwireServerConnection();
        await using var orch = BuildOrchestrator(server, new SpyPtyProcessFactory(), new Dictionary<string, IHostedAgentLauncher>());
        orch.SeedAgentForTest("pub-1");

        var reply = await StopAndReadReply(orch, "pub-1");

        await Assert.That(reply!.Type).IsEqualTo(FrameType.StopAck);
        await Assert.That(server.Calls).Contains(nameof(ServerConnection.AgentStatusChangedAsync));
        await Assert.That(server.Calls).Contains(nameof(ServerConnection.AppendAgentRunEventAsync));
    }

    [Test]
    public async Task Local_stop_with_an_empty_id_stops_every_agent() {
        var server = new TripwireServerConnection();
        await using var orch = BuildOrchestrator(server, new SpyPtyProcessFactory(), new Dictionary<string, IHostedAgentLauncher>());
        orch.SeedAgentForTest("a-1");
        orch.SeedAgentForTest("a-2", isPrivate: true);

        var reply = await StopAndReadReply(orch, "");

        await Assert.That(reply!.Type).IsEqualTo(FrameType.StopAck);
        await Assert.That(reply.Text.Split('\n')).IsEquivalentTo(new[] { "a-1\tstopped", "a-2\tstopped" });
        await Assert.That(orch.GetAgentForTest("a-1")!.Status).IsEqualTo("Completed");
        await Assert.That(orch.GetAgentForTest("a-2")!.Status).IsEqualTo("Completed");
    }

    [Test]
    public async Task Local_stop_of_an_unknown_id_with_no_pid_record_is_an_error() {
        var server = new TripwireServerConnection();
        await using var orch = BuildOrchestrator(server, new SpyPtyProcessFactory(), new Dictionary<string, IHostedAgentLauncher>());

        var reply = await StopAndReadReply(orch, "ghost");

        await Assert.That(reply!.Type).IsEqualTo(FrameType.Error);
        await Assert.That(reply.Text).Contains("ghost");
    }

    [Test]
    public async Task Local_stop_that_cannot_be_confirmed_reports_failed() {
        var server = new TripwireServerConnection();
        await using var orch = BuildOrchestrator(server, new SpyPtyProcessFactory(), new Dictionary<string, IHostedAgentLauncher>());
        // A pty that never reports HasExited, even past TerminateAsync — StopAgentCoreAsync's
        // confirmation check must then report "failed" instead of claiming success.
        orch.SeedAgentForTest("stuck-1", pty: new NeverExitsPtyProcess());

        var reply = await StopAndReadReply(orch, "stuck-1");

        await Assert.That(reply!.Type).IsEqualTo(FrameType.StopAck);
        await Assert.That(reply.Text).IsEqualTo("stuck-1\tfailed");
    }

    [Test]
    public async Task Local_stop_reports_stopped_when_the_reap_lands_just_after_the_kill() {
        var server = new TripwireServerConnection();
        await using var orch = BuildOrchestrator(server, new SpyPtyProcessFactory(), new Dictionary<string, IHostedAgentLauncher>());
        // Models the real UnixPtyProcess.TerminateAsync: it sends SIGKILL then issues one
        // non-blocking waitpid immediately, too soon to see the reap — HasExited stays false right
        // after TerminateAsync returns and only flips true once something polls again. Without
        // StopAgentCoreAsync's post-terminate poll, this successful SIGKILL would be misreported
        // as "failed".
        orch.SeedAgentForTest("reaped-1", pty: new ReapsJustAfterKillPtyProcess());

        var reply = await StopAndReadReply(orch, "reaped-1");

        await Assert.That(reply!.Type).IsEqualTo(FrameType.StopAck);
        await Assert.That(reply.Text).IsEqualTo("reaped-1\tstopped");
    }

    static async Task<LocalFrame?> StopV2AndReadReply(AgentOrchestrator orch, bool force, string agentId) {
        using var client = new DuplexTestStream(new MemoryStream(), new MemoryStream());
        await orch.HandleLocalStopV2Async(force, agentId, client, default);
        client.WrittenStream.Position = 0;

        return await FrameCodec.ReadAsync(client.WrittenStream, default);
    }

    [Test]
    public async Task Stopping_a_flow_participant_without_force_is_refused() {
        var server = new TripwireServerConnection();
        await using var orch = BuildOrchestrator(server, new SpyPtyProcessFactory(), new Dictionary<string, IHostedAgentLauncher>());
        orch.SeedAgentForTest("flow-1", kind: LaunchKind.ReviewFlow, flowRunId: "flow-7f3a", flowRole: "reviewer");

        var reply = await StopV2AndReadReply(orch, force: false, "flow-1");

        await Assert.That(reply!.Type).IsEqualTo(FrameType.Error);
        await Assert.That(reply.Text).Contains("review-flow");
        await Assert.That(reply.Text).Contains("--force");
        await Assert.That(orch.GetAgentForTest("flow-1")!.Status).IsNotEqualTo("Completed");
    }

    [Test]
    public async Task Stopping_a_flow_participant_with_force_succeeds() {
        var server = new TripwireServerConnection();
        await using var orch = BuildOrchestrator(server, new SpyPtyProcessFactory(), new Dictionary<string, IHostedAgentLauncher>());
        orch.SeedAgentForTest("flow-1", kind: LaunchKind.ReviewFlow, flowRunId: "flow-7f3a", flowRole: "reviewer");

        var reply = await StopV2AndReadReply(orch, force: true, "flow-1");

        await Assert.That(reply!.Type).IsEqualTo(FrameType.StopAck);
        await Assert.That(reply.Text).IsEqualTo("flow-1\tstopped");
        await Assert.That(orch.GetAgentForTest("flow-1")!.Status).IsEqualTo("Completed");
    }

    [Test]
    public async Task Stopping_a_non_flow_review_agent_without_force_is_refused_with_an_accurate_message() {
        var server = new TripwireServerConnection();
        await using var orch = BuildOrchestrator(server, new SpyPtyProcessFactory(), new Dictionary<string, IHostedAgentLauncher>());
        orch.SeedAgentForTest("rev-1", kind: LaunchKind.Review);

        var reply = await StopV2AndReadReply(orch, force: false, "rev-1");

        await Assert.That(reply!.Type).IsEqualTo(FrameType.Error);
        await Assert.That(reply.Text).Contains("review agent");
        // Unlike a flow participant, a plain hosted review has no round or flow to strand —
        // the refusal must not claim one.
        await Assert.That(reply.Text).DoesNotContain("flow");
        await Assert.That(reply.Text).Contains("--force");
        await Assert.That(orch.GetAgentForTest("rev-1")!.Status).IsNotEqualTo("Completed");
    }

    [Test]
    public async Task Stopping_a_prior_incarnation_flow_survivor_without_force_is_refused_before_reaping() {
        // Not in _agents — this daemon incarnation never saw it — but its persisted PID record
        // says it was a review-flow participant. The refusal must fire off the RECORD's Kind
        // before TryStopByPidRecordAsync (and its live-process reap) ever runs.
        var server = new TripwireServerConnection();
        await using var orch = BuildOrchestrator(server, new SpyPtyProcessFactory(), new Dictionary<string, IHostedAgentLauncher>());
        orch.WritePidRecordForTest(new AgentPidRecord(
            "ghost-flow", 999_999, "", PidIdentityKind.IdentityUnavailable, "ReviewFlow", "codex",
            "flow-7f3a", "reviewer", orch.DaemonIdForTest, orch.DaemonEpochForTest, DateTimeOffset.UtcNow));

        var reply = await StopV2AndReadReply(orch, force: false, "ghost-flow");

        await Assert.That(reply!.Type).IsEqualTo(FrameType.Error);
        await Assert.That(reply.Text).Contains("review-flow");
        await Assert.That(reply.Text).Contains("--force");
        // Refused before any reap attempt — the record is untouched.
        await Assert.That(orch.PidRecordsForTest().Any(r => r.AgentId == "ghost-flow")).IsTrue();
    }

    [Test]
    public async Task Stopping_a_prior_incarnation_flow_survivor_with_force_bypasses_the_kind_gate() {
        // --force must reach TryStopByPidRecordAsync itself (kept policy-free) rather than being
        // turned back by the new gate above it.
        var server = new TripwireServerConnection();
        await using var orch = BuildOrchestrator(server, new SpyPtyProcessFactory(), new Dictionary<string, IHostedAgentLauncher>());

        using var dummy = DummyProcess.StartSleep(30);
        var pid      = dummy.Pid;
        var identity = ProcessIdentity.Capture(pid)!;
        dummy.Kill(); dummy.WaitForExit(TimeSpan.FromSeconds(5)); // confirmed dead before the reap runs

        orch.WritePidRecordForTest(new AgentPidRecord(
            "ghost-flow-2", pid, identity, PidIdentityKind.Present, "ReviewFlow", "codex",
            "flow-7f3a", "reviewer", orch.DaemonIdForTest, orch.DaemonEpochForTest, DateTimeOffset.UtcNow));

        var reply = await StopV2AndReadReply(orch, force: true, "ghost-flow-2");

        await Assert.That(reply!.Type).IsEqualTo(FrameType.StopAck);
        await Assert.That(reply.Text).IsEqualTo("ghost-flow-2\tstopped");
        await Assert.That(orch.PidRecordsForTest().Any(r => r.AgentId == "ghost-flow-2")).IsFalse();
    }

    [Test]
    public async Task Stop_all_without_force_skips_protected_agents_and_says_so() {
        var server = new TripwireServerConnection();
        await using var orch = BuildOrchestrator(server, new SpyPtyProcessFactory(), new Dictionary<string, IHostedAgentLauncher>());
        orch.SeedAgentForTest("plain-1");
        orch.SeedAgentForTest("flow-1", kind: LaunchKind.ReviewFlow, flowRunId: "flow-7f3a", flowRole: "reviewer");

        var reply = await StopV2AndReadReply(orch, force: false, "");

        var rows = reply!.Text.Split('\n').Select(l => l.Split('\t')).ToDictionary(p => p[0], p => p[1]);
        await Assert.That(rows["plain-1"]).IsEqualTo("stopped");
        await Assert.That(rows["flow-1"]).IsEqualTo("skipped");
        await Assert.That(orch.GetAgentForTest("flow-1")!.Status).IsNotEqualTo("Completed");
    }

    [Test]
    public async Task Stop_all_with_force_includes_protected_agents() {
        var server = new TripwireServerConnection();
        await using var orch = BuildOrchestrator(server, new SpyPtyProcessFactory(), new Dictionary<string, IHostedAgentLauncher>());
        orch.SeedAgentForTest("plain-1");
        orch.SeedAgentForTest("flow-1", kind: LaunchKind.ReviewFlow, flowRunId: "flow-7f3a", flowRole: "reviewer");

        var reply = await StopV2AndReadReply(orch, force: true, "");

        var rows = reply!.Text.Split('\n').Select(l => l.Split('\t')).ToDictionary(p => p[0], p => p[1]);
        await Assert.That(rows["plain-1"]).IsEqualTo("stopped");
        await Assert.That(rows["flow-1"]).IsEqualTo("stopped");
    }

    /// <summary>A pty double whose process never exits — HasExited stays false even after
    /// TerminateAsync — so a test can drive the "stop could not be confirmed" path without a
    /// real hung process.</summary>
    sealed class NeverExitsPtyProcess : IPtyProcess {
        public int  Pid       => 4343;
        public bool HasExited => false;
        public int? ExitCode  => null;

        public ValueTask DisposeAsync() => default;
        public Task WaitForExitAsync(TimeSpan? _) => Task.CompletedTask;
        public Task TerminateAsync(TimeSpan?   _) => Task.CompletedTask;

#pragma warning disable CS1998
        public async IAsyncEnumerable<byte[]> ReadOutputAsync([EnumeratorCancellation] CancellationToken _ = default) {
            yield break;
        }
#pragma warning restore CS1998

        public Task WriteAsync(string _) => Task.CompletedTask;
        public Task WriteAsync(byte[] _) => Task.CompletedTask;
        public void Resize(ushort     _, ushort __) { }
        public void SendInterrupt() { }
    }

    /// <summary>A pty double modelling <c>UnixPtyProcess.TerminateAsync</c>'s real shape: SIGKILL
    /// is sent, then a single non-blocking <c>waitpid</c> is issued immediately — too soon to
    /// observe the reap, so <see cref="HasExited"/> is still false right after
    /// <see cref="TerminateAsync"/> returns. It only flips true on a later poll, mirroring the
    /// kernel reaping the child a moment after the kill.</summary>
    sealed class ReapsJustAfterKillPtyProcess : IPtyProcess {
        bool _terminateCalled;

        public int  Pid       => 5252;
        public bool HasExited { get; private set; }
        public int? ExitCode  => HasExited ? 0 : null;

        public ValueTask DisposeAsync() => default;

        public Task TerminateAsync(TimeSpan? _) {
            _terminateCalled = true; // SIGKILL sent; the immediate non-blocking waitpid misses the reap

            return Task.CompletedTask;
        }

        public Task WaitForExitAsync(TimeSpan? _) {
            if (_terminateCalled) HasExited = true; // the reap lands during this later poll

            return Task.CompletedTask;
        }

#pragma warning disable CS1998
        public async IAsyncEnumerable<byte[]> ReadOutputAsync([EnumeratorCancellation] CancellationToken _ = default) {
            yield break;
        }
#pragma warning restore CS1998

        public Task WriteAsync(string _) => Task.CompletedTask;
        public Task WriteAsync(byte[] _) => Task.CompletedTask;
        public void Resize(ushort     _, ushort __) { }
        public void SendInterrupt() { }
    }
    [Test]
    public async Task Local_list_neutralises_delimiters_inside_a_free_form_field() {
        // Repo paths and flow roles are free-form and may legally hold a tab or newline. Emitted
        // raw they would shift the reader's columns or split the row, and the CLI keys
        // `stop --all`'s confirmation off the kind column — so a corrupted row would understate
        // the blast radius the user is agreeing to.
        var server = new TripwireServerConnection();
        await using var orch = BuildOrchestrator(server, new SpyPtyProcessFactory(), new Dictionary<string, IHostedAgentLauncher>());
        orch.SeedAgentForTest("tabby-1", kind: LaunchKind.ReviewFlow,
            flowRunId: "flow\t7f3a", flowRole: "rev\niewer");

        using var client = new DuplexTestStream(new MemoryStream(), new MemoryStream());
        await orch.HandleLocalListAsync(client, default);
        client.WrittenStream.Position = 0;
        var reply = await FrameCodec.ReadAsync(client.WrittenStream, default);

        var rows = reply!.Text.Split('\n');
        await Assert.That(rows.Length).IsEqualTo(1);

        var cols = rows[0].Split('\t');
        await Assert.That(cols.Length).IsEqualTo(6);
        await Assert.That(cols[3]).IsEqualTo("review-flow");
        await Assert.That(cols[4]).IsEqualTo("flow 7f3a");
        await Assert.That(cols[5]).IsEqualTo("rev iewer");
    }

}
