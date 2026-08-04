using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Threading.Channels;
using Capacitor.Cli.Core;
using Capacitor.Cli.Daemon;
using Capacitor.Cli.Daemon.Pty;
using Capacitor.Cli.Daemon.Services;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Capacitor.Cli.Tests.Unit;

/// <summary>
/// Covers the vendor-routing logic in <see cref="AgentOrchestrator.HandleLaunchAgent"/>
/// added in Task 14. Verifies that:
///   • Unknown vendors short-circuit with LaunchFailed before any worktree work.
///   • Claude/Codex commands route to the matching <see cref="IHostedAgentLauncher"/>.
///   • Review launches for Codex are rejected (v1 limitation).
///   • <see cref="CodexHooksNotInstalledException"/> from Prepare surfaces as a
///     LaunchFailed with the exception's message and no PTY ever spawns.
/// </summary>
public partial class AgentOrchestratorVendorTests {
    static (string repoPath, Action cleanup) CreateGitRepo() {
        var repoPath = Path.Combine(Path.GetTempPath(), "kcap-orch-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(repoPath);

        Git(repoPath, "init", "-q");
        Git(repoPath, "config", "user.email", "test@example.com");
        Git(repoPath, "config", "user.name", "Test");
        File.WriteAllText(Path.Combine(repoPath, "README.md"), "test");
        Git(repoPath, "add", "-A");
        Git(repoPath, "commit", "-q", "-m", "initial");

        return (repoPath, () => {
            try { Directory.Delete(repoPath, true); } catch {
                /* best-effort */
            }
        });
    }

    static void Git(string cwd, params string[] args) {
        var psi = new ProcessStartInfo("git", args) {
            WorkingDirectory       = cwd,
            RedirectStandardOutput = true,
            RedirectStandardError  = true
        };
        using var proc = Process.Start(psi)!;
        proc.WaitForExit();

        if (proc.ExitCode != 0) {
            throw new InvalidOperationException($"git {string.Join(' ', args)} failed: {proc.StandardError.ReadToEnd()}");
        }
    }

    static AgentOrchestrator BuildOrchestrator(
            ServerConnection                                    server,
            IPtyProcessFactory                                  ptyFactory,
            IReadOnlyDictionary<string, IHostedAgentLauncher>   launchers,
            string?                                             allowedRepoPath        = null,
            IEnumerable<IHostedAgentRuntimeFactory>?            extraRuntimeFactories  = null,
            Action<DaemonConfig>?                               configure              = null,
            // Defaults to NullLogger; a test that asserts on the orchestrator's own diagnostics
            // (e.g. the graceful-stop timeout warning) passes a capturing logger instead.
            ILogger<AgentOrchestrator>?                         logger                 = null,
            // Consent: defaults to an allow-all gate over the factory's own temp state dir so every
            // pre-existing test (none of which know about consent) keeps passing unchanged. A test
            // exercising a deny/prompt policy passes its own gate (e.g. built with a Deny-default
            // LaunchConsentStore) instead.
            LaunchConsentGate?                                  consentGate            = null,
            // §3.3: defaults to the fixed never-cancels StubHostLifetime every pre-existing test relies
            // on. A test that needs to simulate shutdown firing mid-launch (e.g. a gate OCE parked on a
            // consent prompt) passes its own lifetime with a controllable ApplicationStopping token.
            IHostApplicationLifetime?                           lifetime               = null,
            // §3.3: leaves the sequenced processor unpublished, so a test can drive the pre-settlement
            // inline arm and the publication barrier. Production never has that window.
            bool                                                deferProcessorPublication = false
        ) {
        var config = new DaemonConfig {
            Name                = "test",
            ServerUrl           = "http://127.0.0.1:1",
            ClaudePath          = "claude",
            MaxConcurrentAgents = 5,
            WorktreeRoot        = Path.Combine(Path.GetTempPath(), "kcap-orch-wt-" + Guid.NewGuid().ToString("N")[..8]),
            // Phase B (D4): isolate the PID-record store to a temp dir so tests never touch the
            // real ~/.config/kcap/daemons.
            StateDir            = Path.Combine(Path.GetTempPath(), "kcap-orch-state-" + Guid.NewGuid().ToString("N")[..8])
        };

        if (allowedRepoPath is not null) {
            config.AllowedRepoPaths = [allowedRepoPath];
        }

        configure?.Invoke(config); // Phase B: let a test tweak the config (e.g. reviewer TTL bounds)

        var worktreeManager  = new WorktreeManager(config, NullLogger<WorktreeManager>.Instance);
        var repoMatcher      = new RepoMatcher(config, NullLogger<RepoMatcher>.Instance);
        var httpFactory      = new StubHttpClientFactory();
        var permissionBridge = new LocalPermissionBridge(server, NullLogger<LocalPermissionBridge>.Instance);

        // Mirror DaemonRunner's DI wiring: one PtyHostedAgentRuntimeFactory per registered launcher,
        // all sharing the same (spied) IPtyProcessFactory so SpyPtyProcessFactory's
        // SpawnCalls/LastCommand assertions stay valid through the runtime-selection seam.
        // extraRuntimeFactories lets a test inject a non-PTY factory (e.g. a fake "cursor" ACP
        // factory) without disturbing every other call site of this helper.
        IReadOnlyDictionary<string, IHostedAgentRuntimeFactory> runtimeFactories = launchers.Values
            .Select(l => (IHostedAgentRuntimeFactory) new PtyHostedAgentRuntimeFactory(l, ptyFactory, NullLogger<PtyHostedAgentRuntimeFactory>.Instance))
            .Concat(extraRuntimeFactories ?? [])
            .ToDictionary(f => f.Vendor);

        consentGate ??= new LaunchConsentGate(
            new LaunchConsentStore(config.StateDir!, NullLogger.Instance),
            new LaunchConsentDecisionLog(config.StateDir!, NullLogger.Instance),
            prompter: null, TimeProvider.System, NullLogger<LaunchConsentGate>.Instance);

        return new AgentOrchestrator(
            config,
            server,
            worktreeManager,
            repoMatcher,
            ptyFactory,
            httpFactory,
            permissionBridge,
            launchers,
            runtimeFactories,
            lifetime ?? new StubHostLifetime(),
            logger ?? NullLogger<AgentOrchestrator>.Instance,
            consentGate,
            deferProcessorPublication
        );
    }

    sealed class StubHostLifetime : IHostApplicationLifetime {
        public CancellationToken ApplicationStarted  => CancellationToken.None;
        public CancellationToken ApplicationStopping => CancellationToken.None;
        public CancellationToken ApplicationStopped  => CancellationToken.None;
        public void StopApplication() { }
    }

    // re-registration is awaited inside RegisterDaemon before readiness is restored.
    // A transient per-agent failure must be retried (not swallowed on first try), so the agent's
    // ownership is restored before the daemon flips ready — narrowing the "ready despite reregister
    // failure" window qodo flagged.
    [Test]
    public async Task ReRegister_retries_a_transient_per_agent_failure_then_succeeds() {
        var server = new CaptureServerConnection { AgentRegisteredFailTimes = 1 };

        await using var orch = BuildOrchestrator(server, new SpyPtyProcessFactory(), new Dictionary<string, IHostedAgentLauncher>());

        orch.RegisterAgentForTest(new AgentInstance(
            "agent-rereg", null, "", null, "/tmp", "claude",
            new PtyHostedAgentRuntime("claude", new StubPtyProcess()), new WorktreeInfo("/tmp", "", "/tmp", IsStandalone: true), new CancellationTokenSource()
        ));

        // The orchestrator wires ReRegisterAgentsHook in its ctor; invoking it runs the same
        // path RegisterDaemon awaits on reconnect.
        await server.ReRegisterAgentsHook!();

        // First attempt threw a transient failure; the bounded retry succeeded on the second.
        await Assert.That(server.AgentRegisteredCallCount).IsEqualTo(2);
    }

    [Test]
    public async Task Launch_with_unknown_vendor_emits_launch_failed_and_does_not_spawn_pty() {
        var server     = new CaptureServerConnection();
        var ptyFactory = new SpyPtyProcessFactory();
        var launchers  = new Dictionary<string, IHostedAgentLauncher>();

        await using var orch = BuildOrchestrator(server, ptyFactory, launchers);

        var cmd = new LaunchAgentCommand(
            AgentId: "agent-bogus",
            Prompt: "hi",
            Model: "opus",
            Effort: null,
            RepoPath: "/tmp/does-not-matter",
            Tools: null,
            AttachmentIds: null,
            Vendor: "bogus"
        );

        await orch.HandleLaunchAgentForTest(cmd);

        await Assert.That(server.LaunchFailedCalls.Count).IsEqualTo(1);
        await Assert.That(server.LaunchFailedCalls[0].AgentId).IsEqualTo("agent-bogus");
        await Assert.That(server.LaunchFailedCalls[0].Reason).Contains("Unknown vendor");
        await Assert.That(ptyFactory.SpawnCalls).IsEqualTo(0);
    }

    // ── Caller-selected Codex posture: fail-closed pre-flight guard ──────────────────────────
    // Every case below uses a repo path that is NOT allowed and does NOT exist, so the assertions
    // are position-sensitive: the posture guard must run BEFORE the repo-path checks (and therefore
    // before any worktree work). Move the guard later and the reason becomes "Repo path …" and these
    // tests fail — which is exactly the regression they exist to catch.

    static LaunchAgentCommand PostureCmd(
            string              agentId,
            CodexLaunchPosture? posture,
            string              vendor   = "codex",
            LaunchKind          kind     = LaunchKind.Default,
            bool                borrowed = false
        ) => new(
            AgentId: agentId,
            Prompt: "hi",
            Model: "default",
            Effort: null,
            RepoPath: "/tmp/kcap-posture-guard-nonexistent",
            Tools: null,
            AttachmentIds: null,
            Vendor: vendor,
            Kind: kind,
            Borrowed: borrowed,
            CodexPosture: posture
        );

    static AgentOrchestrator BuildPostureOrchestrator(CaptureServerConnection server, SpyPtyProcessFactory ptyFactory) =>
        BuildOrchestrator(
            server, ptyFactory, new Dictionary<string, IHostedAgentLauncher>(),
            extraRuntimeFactories: [
                new SpyHostedAgentRuntimeFactory("codex")  { SupportsUnattended = true, SupportsBorrowedReviewFlow = true },
                new SpyHostedAgentRuntimeFactory("claude") { SupportsUnattended = true }
            ]);

    [Test]
    public async Task Posture_on_review_flow_launch_is_rejected_before_any_worktree_work() {
        var server     = new CaptureServerConnection();
        var ptyFactory = new SpyPtyProcessFactory();

        await using var orch = BuildPostureOrchestrator(server, ptyFactory);

        await orch.HandleLaunchAgentForTest(
            PostureCmd("agent-posture-flow", new("workspace-write", "on-request"), kind: LaunchKind.ReviewFlow));

        await Assert.That(server.LaunchFailedCalls.Count).IsEqualTo(1);
        await Assert.That(server.LaunchFailedCalls[0].AgentId).IsEqualTo("agent-posture-flow");
        await Assert.That(server.LaunchFailedCalls[0].Reason).StartsWith("codex_posture_not_overridable:");
        await Assert.That(ptyFactory.SpawnCalls).IsEqualTo(0);
    }

    [Test]
    public async Task Posture_on_borrowed_launch_is_rejected_before_any_worktree_work() {
        var server     = new CaptureServerConnection();
        var ptyFactory = new SpyPtyProcessFactory();

        await using var orch = BuildPostureOrchestrator(server, ptyFactory);

        await orch.HandleLaunchAgentForTest(
            PostureCmd("agent-posture-borrowed", new("read-only", "never"), borrowed: true));

        await Assert.That(server.LaunchFailedCalls.Count).IsEqualTo(1);
        await Assert.That(server.LaunchFailedCalls[0].Reason).StartsWith("codex_posture_not_overridable:");
        await Assert.That(ptyFactory.SpawnCalls).IsEqualTo(0);
    }

    [Test]
    public async Task Posture_on_a_non_codex_launch_is_rejected() {
        var server     = new CaptureServerConnection();
        var ptyFactory = new SpyPtyProcessFactory();

        await using var orch = BuildPostureOrchestrator(server, ptyFactory);

        await orch.HandleLaunchAgentForTest(
            PostureCmd("agent-posture-claude", new("read-only", "never"), vendor: "claude"));

        await Assert.That(server.LaunchFailedCalls.Count).IsEqualTo(1);
        await Assert.That(server.LaunchFailedCalls[0].Reason).StartsWith("codex_posture_wrong_vendor:");
        await Assert.That(ptyFactory.SpawnCalls).IsEqualTo(0);
    }

    [Test]
    public async Task Posture_with_an_invalid_token_is_rejected() {
        var server     = new CaptureServerConnection();
        var ptyFactory = new SpyPtyProcessFactory();

        await using var orch = BuildPostureOrchestrator(server, ptyFactory);

        await orch.HandleLaunchAgentForTest(
            PostureCmd("agent-posture-bad-token", new("workspace-write", "on-failure")));

        await Assert.That(server.LaunchFailedCalls.Count).IsEqualTo(1);
        await Assert.That(server.LaunchFailedCalls[0].Reason).StartsWith("codex_posture_invalid:");
        await Assert.That(ptyFactory.SpawnCalls).IsEqualTo(0);
    }

    [Test]
    public async Task A_valid_interactive_posture_passes_the_guard() {
        // This launch still fails downstream (the repo path is deliberately bogus), but it must get
        // PAST the posture guard — proving the guard rejects only ineligible/malformed blocks.
        var server     = new CaptureServerConnection();
        var ptyFactory = new SpyPtyProcessFactory();

        await using var orch = BuildPostureOrchestrator(server, ptyFactory);

        await orch.HandleLaunchAgentForTest(
            PostureCmd("agent-posture-ok", new("read-only", "never")));

        await Assert.That(server.LaunchFailedCalls.Any(c => c.Reason.StartsWith("codex_posture_"))).IsFalse();
    }

    // ── Applied-posture echo on registration ────────────────────────────────────────────────
    // The echo is stamped on the AgentInstance so the initial registration AND every reconnect
    // re-registration report the same pair. It exists only for an interactive Codex launch on a
    // daemon-owned worktree; every other launch shape reports nulls, which is what lets a consumer
    // render it without any launch-kind discriminator.

    async Task<(CaptureServerConnection Server, SpyHostedAgentRuntimeFactory Codex)> LaunchForEchoAsync(
            string repoPath,
            string agentId,
            CodexLaunchPosture? posture,
            LaunchKind kind = LaunchKind.Default,
            bool borrowed = false,
            string vendor = "codex",
            ILogger<AgentOrchestrator>? logger = null) {
        var server     = new CaptureServerConnection();
        var ptyFactory = new SpyPtyProcessFactory();
        var codexSpy   = new SpyHostedAgentRuntimeFactory("codex") {
            EmitsTerminalOutput = false, SupportsUnattended = true, SupportsBorrowedReviewFlow = true
        };
        var claudeSpy = new SpyHostedAgentRuntimeFactory("claude") {
            EmitsTerminalOutput = false, SupportsUnattended = true
        };

        // No explicit allowlist: an empty AllowedRepoPaths allows every local git repo, which is what
        // the borrow tests rely on too — a textual allowlist entry would not match the canonicalized
        // (symlink-resolved) cwd the borrow authorizer compares against.
        await using var orch = BuildOrchestrator(
            server, ptyFactory, new Dictionary<string, IHostedAgentLauncher>(),
            extraRuntimeFactories: [codexSpy, claudeSpy], logger: logger);
        var startsReviewerBridge = kind == LaunchKind.ReviewFlow && vendor == "codex";
        if (startsReviewerBridge)
            await orch.PermissionBridgeForTest.StartAsync(CancellationToken.None);
        try {
            await orch.HandleLaunchAgentForTest(new LaunchAgentCommand(
                AgentId: agentId,
                Prompt: "do a thing",
                Model: "default",
                Effort: null,
                RepoPath: repoPath,
                Tools: null,
                AttachmentIds: null,
                Vendor: vendor,
                Kind: kind,
                Borrowed: borrowed,
                BorrowCwd: borrowed ? repoPath : null,
                CodexPosture: posture));
        } finally {
            if (startsReviewerBridge)
                await orch.PermissionBridgeForTest.DisposeAsync();
        }

        return (server, codexSpy);
    }

    [Test]
    public async Task Interactive_codex_launch_echoes_the_selected_posture_on_registration() {
        var (repoPath, cleanup) = CreateGitRepo();

        try {
            var (server, _) = await LaunchForEchoAsync(repoPath, "agent-echo-selected", new("read-only", "never"));

            await Assert.That(server.AgentRegisteredPostures).Contains(("agent-echo-selected", "read-only", "never"));
        } finally {
            cleanup();
        }
    }

    /// <summary>The command→runtime handoff, which no other test covers: the echo is computed from
    /// `cmd` and the launcher tests build a LauncherContext by hand, so dropping
    /// `CodexPosture: cmd.CodexPosture` from the RuntimeStartContext construction would leave every
    /// other posture test green while the agent silently launched on the old defaults — with
    /// registration still advertising the selected pair.</summary>
    [Test]
    public async Task Interactive_codex_launch_threads_the_posture_into_the_runtime_start_context() {
        var (repoPath, cleanup) = CreateGitRepo();

        try {
            var (_, codexSpy) = await LaunchForEchoAsync(repoPath, "agent-thread", new("danger-full-access", "untrusted"));

            await Assert.That(codexSpy.LastContext).IsNotNull();
            await Assert.That(codexSpy.LastContext!.CodexPosture).IsNotNull();
            await Assert.That(codexSpy.LastContext!.CodexPosture!.Sandbox).IsEqualTo("danger-full-access");
            await Assert.That(codexSpy.LastContext!.CodexPosture!.Approval).IsEqualTo("untrusted");
        } finally {
            cleanup();
        }
    }

    [Test]
    public async Task Interactive_codex_launch_without_a_posture_threads_null() {
        var (repoPath, cleanup) = CreateGitRepo();

        try {
            var (_, codexSpy) = await LaunchForEchoAsync(repoPath, "agent-thread-null", posture: null);

            await Assert.That(codexSpy.LastContext).IsNotNull();
            await Assert.That(codexSpy.LastContext!.CodexPosture).IsNull();
        } finally {
            cleanup();
        }
    }

    /// <summary>A snapshot-backed borrow maps to WorkLocation.OwnedWorktree, so `work` alone would
    /// wrongly qualify it as interactive and echo a posture the caller never chose. Guards the
    /// `!cmd.Borrowed` arm of the echo predicate.</summary>
    [Test, NotInParallel("LocalPermissionBridgeTests")]
    public async Task Snapshot_borrowed_launch_echoes_no_posture() {
        var (repoPath, cleanup) = CreateGitRepo();

        try {
            var server     = new CaptureServerConnection();
            var ptyFactory = new SpyPtyProcessFactory();
            var codexSpy   = new SpyHostedAgentRuntimeFactory("codex") {
                EmitsTerminalOutput = false,
                SupportsUnattended = true,
                SupportsBorrowedReviewFlow = true,
                BorrowedReviewRequiresIndependentSnapshot = true
            };

            await using var orch = BuildOrchestrator(
                server, ptyFactory, new Dictionary<string, IHostedAgentLauncher>(),
                extraRuntimeFactories: [codexSpy]);
            var bridge = orch.PermissionBridgeForTest;
            await bridge.StartAsync(CancellationToken.None);
            try {
                await orch.HandleLaunchAgentForTest(new LaunchAgentCommand(
                    AgentId: "agent-snapshot-borrow",
                    Prompt: "hi",
                    Model: "default",
                    Effort: null,
                    RepoPath: repoPath,
                    Tools: null,
                    AttachmentIds: null,
                    Vendor: "codex",
                    Kind: LaunchKind.Default,
                    Borrowed: true,
                    BorrowCwd: repoPath));

                // The launch must actually REACH registration — otherwise an unrelated early failure
                // would satisfy a "no non-null echo exists" assertion without ever exercising the
                // predicate under test. Require exactly one registration, and require it to be null/null.
                var registrations = server.AgentRegisteredPostures
                    .Where(p => p.AgentId == "agent-snapshot-borrow")
                    .ToList();

                await Assert.That(registrations).Count().IsEqualTo(1);
                await Assert.That(registrations[0].Sandbox).IsNull();
                await Assert.That(registrations[0].Approval).IsNull();
                // The runtime really started, so the snapshot-borrow path ran end to end.
                await Assert.That(codexSpy.StartCalls).IsEqualTo(1);
            } finally {
                await bridge.DisposeAsync();
            }
        } finally {
            cleanup();
        }
    }

    [Test]
    public async Task Interactive_codex_launch_without_a_posture_echoes_the_derived_pair() {
        var (repoPath, cleanup) = CreateGitRepo();

        try {
            var (server, _) = await LaunchForEchoAsync(repoPath, "agent-echo-derived", posture: null);

            await Assert.That(server.AgentRegisteredPostures)
                .Contains(("agent-echo-derived", "workspace-write", "on-request"));
        } finally {
            cleanup();
        }
    }

    [Test, NotInParallel("LocalPermissionBridgeTests")]
    public async Task Review_flow_launch_echoes_no_posture() {
        // A reviewer's `never` is the containment invariant, not a selection — reporting it would
        // make every reviewer look like a user-chosen bridge-defeating launch.
        var (repoPath, cleanup) = CreateGitRepo();

        try {
            var (server, _) = await LaunchForEchoAsync(
                repoPath, "agent-echo-flow", posture: null, kind: LaunchKind.ReviewFlow);

            await Assert.That(server.AgentRegisteredPostures).Contains(("agent-echo-flow", null, null));
        } finally {
            cleanup();
        }
    }

    [Test]
    public async Task Borrowed_default_launch_echoes_no_posture() {
        // `work` is resolved from cmd.Borrowed independently of Kind, so a posture-LESS borrowed
        // Default command is accepted — and must still echo nothing rather than a derived pair.
        var (repoPath, cleanup) = CreateGitRepo();

        try {
            var (server, _) = await LaunchForEchoAsync(
                repoPath, "agent-echo-borrowed", posture: null, borrowed: true);

            await Assert.That(server.AgentRegisteredPostures).Contains(("agent-echo-borrowed", null, null));
        } finally {
            cleanup();
        }
    }

    [Test]
    public async Task Non_codex_launch_echoes_no_posture() {
        var (repoPath, cleanup) = CreateGitRepo();

        try {
            var (server, _) = await LaunchForEchoAsync(
                repoPath, "agent-echo-claude", posture: null, vendor: "claude");

            await Assert.That(server.AgentRegisteredPostures).Contains(("agent-echo-claude", null, null));
        } finally {
            cleanup();
        }
    }

    [Test]
    public async Task Reregistration_resends_the_same_applied_posture() {
        // A server restart wipes the in-memory echo; the reconnect path rebuilds it from the
        // AgentInstance, so the pair must survive rather than silently becoming null.
        var server     = new CaptureServerConnection();
        var ptyFactory = new SpyPtyProcessFactory();

        await using var orch = BuildOrchestrator(server, ptyFactory, new Dictionary<string, IHostedAgentLauncher>());

        orch.RegisterAgentForTest(new AgentInstance(
            "agent-rereg-posture", null, "", null, "/tmp", "codex",
            new PtyHostedAgentRuntime("codex", new StubPtyProcess()),
            new WorktreeInfo("/tmp", "", "/tmp", IsStandalone: true), new CancellationTokenSource()
        ) {
            SandboxPolicy = "danger-full-access", ApprovalPolicy = "never"
        });

        await server.ReRegisterAgentsHook!();

        await Assert.That(server.AgentRegisteredPostures)
            .Contains(("agent-rereg-posture", "danger-full-access", "never"));
    }

    [Test]
    [Arguments("workspace-write", "never")]
    [Arguments("danger-full-access", "on-request")]
    public async Task Bridge_defeating_posture_logs_a_warning(string sandbox, string approval) {
        var (repoPath, cleanup) = CreateGitRepo();

        try {
            var logger = new CapturingLogger<AgentOrchestrator>();

            await LaunchForEchoAsync(repoPath, "agent-warn", new(sandbox, approval), logger: logger);

            // Matched on the posture warning's own wording — an unrelated warning that merely names
            // the agent (e.g. the terminal-dimensions send failure this harness always provokes)
            // must not be able to satisfy this.
            var postureWarnings = logger.Entries
                .Where(e => e.Level == LogLevel.Warning
                         && e.Message.Contains("agent-warn")
                         && e.Message.Contains($"sandbox={sandbox}")
                         && e.Message.Contains($"approval={approval}"))
                .ToList();
            await Assert.That(postureWarnings).IsNotEmpty();
        } finally {
            cleanup();
        }
    }

    [Test]
    public async Task A_prompting_posture_logs_no_bridge_warning() {
        var (repoPath, cleanup) = CreateGitRepo();

        try {
            var logger = new CapturingLogger<AgentOrchestrator>();

            await LaunchForEchoAsync(repoPath, "agent-no-warn", new("read-only", "untrusted"), logger: logger);

            // Scoped to the posture warning's wording: this harness emits unrelated warnings (no real
            // server to accept terminal dimensions), so a bare "any warning" assertion would be false.
            var postureWarnings = logger.Entries
                .Where(e => e.Level == LogLevel.Warning && e.Message.Contains("sandbox=read-only"))
                .ToList();
            await Assert.That(postureWarnings).IsEmpty();
        } finally {
            cleanup();
        }
    }

    sealed class CapturingLogger<T> : ILogger<T> {
        public List<(LogLevel Level, string Message)> Entries { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
                LogLevel level, EventId eventId, TState state, Exception? ex,
                Func<TState, Exception?, string> formatter) {
            lock (Entries) Entries.Add((level, formatter(state, ex)));
        }
    }

    // Qodo review on #234: a null Vendor (SignalR boundary — non-null annotation not enforced) must
    // emit LaunchFailed, NOT throw ArgumentNullException from the dictionary lookup (which SafeInvoke
    // would swallow, dropping the launch silently).
    [Test]
    public async Task Launch_with_null_vendor_emits_launch_failed_and_does_not_throw() {
        var server     = new CaptureServerConnection();
        var ptyFactory = new SpyPtyProcessFactory();
        var launchers  = new Dictionary<string, IHostedAgentLauncher>();

        await using var orch = BuildOrchestrator(server, ptyFactory, launchers);

        var cmd = new LaunchAgentCommand(
            AgentId: "agent-null-vendor",
            Prompt: "hi",
            Model: "opus",
            Effort: null,
            RepoPath: "/tmp/does-not-matter",
            Tools: null,
            AttachmentIds: null,
            Vendor: null!
        );

        await orch.HandleLaunchAgentForTest(cmd);

        await Assert.That(server.LaunchFailedCalls.Count).IsEqualTo(1);
        await Assert.That(server.LaunchFailedCalls[0].AgentId).IsEqualTo("agent-null-vendor");
        await Assert.That(server.LaunchFailedCalls[0].Reason).Contains("Unknown vendor");
        await Assert.That(ptyFactory.SpawnCalls).IsEqualTo(0);
    }

    // the orchestrator's unattended-launch guard (UnattendedLaunchPolicy.RejectionReason)
    // must actually be wired into HandleLaunchAgent — reject a review-flow launch whose vendor
    // can't run unattended, and do it before any worktree/PTY side effects.
    [Test]
    public async Task Unattended_review_flow_launch_is_rejected_for_vendor_without_unattended_support() {
        var server     = new CaptureServerConnection();
        var ptyFactory = new SpyPtyProcessFactory();
        var claudeSpy  = new SpyHostedAgentLauncher("claude", cliPath: "spy-claude") { SupportsUnattended = false };

        var launchers = new Dictionary<string, IHostedAgentLauncher> { ["claude"] = claudeSpy };

        await using var orch = BuildOrchestrator(server, ptyFactory, launchers);

        var cmd = new LaunchAgentCommand(
            AgentId: "agent-unattended",
            Prompt: "review this",
            Model: "opus",
            Effort: null,
            RepoPath: "/tmp/does-not-matter",
            Tools: null,
            AttachmentIds: null,
            Vendor: "claude",
            Kind: LaunchKind.ReviewFlow
        );

        await orch.HandleLaunchAgentForTest(cmd);

        await Assert.That(server.LaunchFailedCalls.Count).IsEqualTo(1);
        await Assert.That(server.LaunchFailedCalls[0].AgentId).IsEqualTo("agent-unattended");
        await Assert.That(server.LaunchFailedCalls[0].Reason).Contains("unattended");

        // Rejected before any worktree/PTY side effects.
        await Assert.That(claudeSpy.PrepareCalls).IsEqualTo(0);
        await Assert.That(claudeSpy.BuildArgsCalls).IsEqualTo(0);
        await Assert.That(ptyFactory.SpawnCalls).IsEqualTo(0);
    }

    [Test]
    public async Task Certified_review_flow_reprobes_before_worktree_or_process_spawn() {
        var server = new CaptureServerConnection();
        var ptyFactory = new SpyPtyProcessFactory();
        var claudeSpy = new SpyHostedAgentLauncher("claude", cliPath: "spy-claude") {
            SupportsUnattended = true
        };
        var launchers = new Dictionary<string, IHostedAgentLauncher> { ["claude"] = claudeSpy };

        await using var orch = BuildOrchestrator(server, ptyFactory, launchers,
            configure: config => config.ClaudePath = "/definitely/missing/claude");
        var cmd = new LaunchAgentCommand(
            AgentId: "agent-certification",
            Prompt: "review this",
            Model: "default",
            Effort: null,
            RepoPath: "/tmp/does-not-matter",
            Tools: null,
            AttachmentIds: null,
            Vendor: "claude",
            Kind: LaunchKind.ReviewFlow,
            ReviewerCertification: new ReviewerCertificationRequirement(
                "claude", ">=1.0.0", DaemonRunner.ClaudeLauncherPolicyVersion, "revision-2",
                "connection-1", "1.0.0"));

        await orch.HandleLaunchAgentForTest(cmd);

        await Assert.That(server.LaunchFailedCalls).Count().IsEqualTo(1);
        await Assert.That(server.LaunchFailedCalls[0].Reason).Contains("reviewer_certification_changed");
        await Assert.That(claudeSpy.PrepareCalls).IsEqualTo(0);
        await Assert.That(ptyFactory.SpawnCalls).IsEqualTo(0);
    }

    [Test]
    public async Task Certified_review_flow_rejects_stale_connection_incarnation_before_spawn() {
        var server = new CaptureServerConnection { ConnectionIdForTest = "connection-new" };
        var ptyFactory = new SpyPtyProcessFactory();
        var claudeSpy = new SpyHostedAgentLauncher("claude", cliPath: "spy-claude") {
            SupportsUnattended = true
        };
        await using var orch = BuildOrchestrator(server, ptyFactory,
            new Dictionary<string, IHostedAgentLauncher> { ["claude"] = claudeSpy });
        var cmd = new LaunchAgentCommand(
            "agent-stale-connection", "review", "default", null, "/tmp/unused", null, null,
            Vendor: "claude", Kind: LaunchKind.ReviewFlow,
            ReviewerCertification: new ReviewerCertificationRequirement(
                "claude", ">=1.0.0", DaemonRunner.ClaudeLauncherPolicyVersion, "revision-2",
                "connection-old", "1.0.0"));

        await orch.HandleLaunchAgentForTest(cmd);

        await Assert.That(server.LaunchFailedCalls).Count().IsEqualTo(1);
        await Assert.That(server.LaunchFailedCalls[0].Reason).Contains("reviewer_certification_changed");
        await Assert.That(claudeSpy.PrepareCalls).IsEqualTo(0);
        await Assert.That(ptyFactory.SpawnCalls).IsEqualTo(0);
    }

    [Test]
    public async Task Launch_with_vendor_claude_calls_claude_launcher() {
        var (repoPath, cleanup) = CreateGitRepo();

        try {
            var server     = new CaptureServerConnection();
            var ptyFactory = new SpyPtyProcessFactory();
            var claudeSpy  = new SpyHostedAgentLauncher("claude", cliPath: "spy-claude");
            var codexSpy   = new SpyHostedAgentLauncher("codex", cliPath: "spy-codex");

            var launchers = new Dictionary<string, IHostedAgentLauncher> {
                ["claude"] = claudeSpy,
                ["codex"]  = codexSpy
            };

            await using var orch = BuildOrchestrator(server, ptyFactory, launchers, allowedRepoPath: repoPath);

            var cmd = new LaunchAgentCommand(
                AgentId: "agent-c1",
                Prompt: "do work",
                Model: "opus",
                Effort: null,
                RepoPath: repoPath,
                Tools: null,
                AttachmentIds: null,
                Vendor: "claude"
            );

            await orch.HandleLaunchAgentForTest(cmd);

            await Assert.That(claudeSpy.BuildArgsCalls).IsEqualTo(1);
            await Assert.That(claudeSpy.PrepareCalls).IsEqualTo(1);
            await Assert.That(codexSpy.BuildArgsCalls).IsEqualTo(0);
            await Assert.That(codexSpy.PrepareCalls).IsEqualTo(0);

            // PTY spawn must have used the claude launcher's CLI path.
            await Assert.That(ptyFactory.SpawnCalls).IsEqualTo(1);
            await Assert.That(ptyFactory.LastCommand).IsEqualTo("spy-claude");
        } finally {
            cleanup();
        }
    }

    [Test]
    public async Task Launch_with_vendor_codex_calls_codex_launcher() {
        var (repoPath, cleanup) = CreateGitRepo();

        try {
            var server     = new CaptureServerConnection();
            var ptyFactory = new SpyPtyProcessFactory();
            var claudeSpy  = new SpyHostedAgentLauncher("claude", cliPath: "spy-claude");
            var codexSpy   = new SpyHostedAgentLauncher("codex", cliPath: "spy-codex");

            var launchers = new Dictionary<string, IHostedAgentLauncher> {
                ["claude"] = claudeSpy,
                ["codex"]  = codexSpy
            };

            await using var orch = BuildOrchestrator(server, ptyFactory, launchers, allowedRepoPath: repoPath);

            var cmd = new LaunchAgentCommand(
                AgentId: "agent-x1",
                Prompt: "do work",
                Model: "gpt-5",
                Effort: null,
                RepoPath: repoPath,
                Tools: null,
                AttachmentIds: null,
                Vendor: "codex"
            );

            await orch.HandleLaunchAgentForTest(cmd);

            await Assert.That(codexSpy.BuildArgsCalls).IsEqualTo(1);
            await Assert.That(codexSpy.PrepareCalls).IsEqualTo(1);
            await Assert.That(claudeSpy.BuildArgsCalls).IsEqualTo(0);
            await Assert.That(claudeSpy.PrepareCalls).IsEqualTo(0);

            await Assert.That(ptyFactory.SpawnCalls).IsEqualTo(1);
            await Assert.That(ptyFactory.LastCommand).IsEqualTo("spy-codex");
        } finally {
            cleanup();
        }
    }

    // Task 10: a "cursor" launch must route to its registered IHostedAgentRuntimeFactory
    // (the ACP seam) rather than falling through to a PTY launcher/factory. This is a pure unit
    // test — SpyHostedAgentRuntimeFactory never spawns a real cursor-agent process.
    [Test]
    public async Task Launch_with_vendor_cursor_routes_to_the_acp_runtime_factory_not_pty() {
        var (repoPath, cleanup) = CreateGitRepo();

        try {
            var server      = new CaptureServerConnection();
            var ptyFactory  = new SpyPtyProcessFactory();
            var claudeSpy   = new SpyHostedAgentLauncher("claude", cliPath: "spy-claude");
            var cursorSpy   = new SpyHostedAgentRuntimeFactory("cursor");

            var launchers = new Dictionary<string, IHostedAgentLauncher> { ["claude"] = claudeSpy };

            await using var orch = BuildOrchestrator(
                server,
                ptyFactory,
                launchers,
                allowedRepoPath: repoPath,
                extraRuntimeFactories: [cursorSpy]
            );

            var cmd = new LaunchAgentCommand(
                AgentId: "agent-cursor1",
                Prompt: "do work",
                Model: "auto",
                Effort: null,
                RepoPath: repoPath,
                Tools: null,
                AttachmentIds: null,
                Vendor: "cursor"
            );

            await orch.HandleLaunchAgentForTest(cmd);

            await Assert.That(cursorSpy.StartCalls).IsEqualTo(1);
            await Assert.That(cursorSpy.LastAgentId).IsEqualTo("agent-cursor1");

            // Must NOT have gone through the PTY path at all.
            await Assert.That(ptyFactory.SpawnCalls).IsEqualTo(0);
            await Assert.That(claudeSpy.PrepareCalls).IsEqualTo(0);
            await Assert.That(claudeSpy.BuildArgsCalls).IsEqualTo(0);
        } finally {
            cleanup();
        }
    }

    // Fix B/E (PR #244 review, BLOCKER): a runtime whose ReadOutputAsync never yields a byte
    // (ACP/cursor) must NOT wait for the orchestrator's on-first-chunk Starting→Running flip — that
    // flip lives in ReadAgentOutputAsync and only fires on an output CHUNK, which never arrives for
    // such a runtime. Before the fix this left the agent stuck in "Starting" (eventually auto-
    // stopped by the heartbeat's stuck-Starting timeout) and, worse, the runtime's ReadOutputAsync
    // completing immediately made FinalizeAgentRunAsync run right after launch and misclassify the
    // still-live agent as a startup failure. This test proves: (1) the agent flips to "Running"
    // synchronously within HandleLaunchAgent, before any output; (2) it is NOT finalized-as-Failed
    // while still live (the fake's ReadOutputAsync stays open exactly like the real ACP runtime).
    [Test]
    public async Task Launch_of_a_no_terminal_output_runtime_flips_to_Running_immediately_and_is_not_finalized_as_failed() {
        var (repoPath, cleanup) = CreateGitRepo();

        try {
            var server     = new CaptureServerConnection();
            var ptyFactory = new SpyPtyProcessFactory();
            var cursorSpy  = new SpyHostedAgentRuntimeFactory("cursor") { EmitsTerminalOutput = false };

            var launchers = new Dictionary<string, IHostedAgentLauncher>();

            await using var orch = BuildOrchestrator(
                server,
                ptyFactory,
                launchers,
                allowedRepoPath: repoPath,
                extraRuntimeFactories: [cursorSpy]
            );

            var cmd = new LaunchAgentCommand(
                AgentId: "agent-acp-live",
                Prompt: "do work",
                Model: "auto",
                Effort: null,
                RepoPath: repoPath,
                Tools: null,
                AttachmentIds: null,
                Vendor: "cursor"
            );

            await orch.HandleLaunchAgentForTest(cmd);

            // Flipped to Running synchronously within the launch call itself — no output chunk
            // was ever produced (the fake's ReadOutputAsync blocks on ExitGate, exactly like the
            // real ACP runtime blocks on process-exit/ct).
            await Assert.That(server.StatusChangedCalls).Contains(("agent-acp-live", "Running"));

            // Give the (fire-and-forget) read loop / finalize path a moment to run if it were
            // (incorrectly) going to — before the fix, ReadOutputAsync completing immediately
            // would have driven FinalizeAgentRunAsync to run right here and mark the agent Failed.
            await Task.Delay(200);

            await Assert.That(server.StatusChangedCalls).DoesNotContain(("agent-acp-live", "Failed"));
            await Assert.That(server.StatusChangedCalls).DoesNotContain(("agent-acp-live", "Completed"));
            await Assert.That(server.LaunchFailedCalls.Count).IsEqualTo(0);

            // Cleanly finish the still-live agent so the harness doesn't leak an ExitGate-blocked
            // read loop past the test.
            await orch.HandleStopAgentForTest("agent-acp-live");
        } finally {
            cleanup();
        }
    }

    // Companion test: the existing PTY on-first-chunk Starting→Running flip must remain exactly
    // unchanged for a runtime with EmitsTerminalOutput=true — the new immediate-flip branch in
    // HandleLaunchAgent must not fire for it.
    [Test]
    public async Task Launch_of_a_terminal_output_runtime_does_not_flip_to_Running_before_the_first_output_chunk() {
        var (repoPath, cleanup) = CreateGitRepo();

        try {
            var server     = new CaptureServerConnection();
            var ptyFactory = new SpyPtyProcessFactory();
            var cursorSpy  = new SpyHostedAgentRuntimeFactory("cursor") { EmitsTerminalOutput = true };

            var launchers = new Dictionary<string, IHostedAgentLauncher>();

            await using var orch = BuildOrchestrator(
                server,
                ptyFactory,
                launchers,
                allowedRepoPath: repoPath,
                extraRuntimeFactories: [cursorSpy]
            );

            var cmd = new LaunchAgentCommand(
                AgentId: "agent-terminal-live",
                Prompt: "do work",
                Model: "auto",
                Effort: null,
                RepoPath: repoPath,
                Tools: null,
                AttachmentIds: null,
                Vendor: "cursor"
            );

            await orch.HandleLaunchAgentForTest(cmd);

            // No output chunk was ever produced (ExitGate is still open) — must NOT have flipped
            // to Running yet, preserving the existing PTY on-first-chunk contract exactly.
            await Task.Delay(100);
            await Assert.That(server.StatusChangedCalls).DoesNotContain(("agent-terminal-live", "Running"));

            await orch.HandleStopAgentForTest("agent-terminal-live");
        } finally {
            cleanup();
        }
    }

    [Test]
    public async Task Launch_review_kind_with_vendor_codex_is_accepted_and_reaches_review_validation() {
        // A git repo with NO origin remote: a Codex review now passes the vendor
        // gate (which used to reject it) and fails later at origin validation —
        // the SAME point a Claude review would. Proves the gate is lifted.
        var (repoPath, cleanup) = CreateGitRepo();

        try {
            var server     = new CaptureServerConnection();
            var ptyFactory = new SpyPtyProcessFactory();
            var codexSpy   = new SpyHostedAgentLauncher("codex", cliPath: "spy-codex");

            var launchers = new Dictionary<string, IHostedAgentLauncher> { ["codex"] = codexSpy };

            await using var orch = BuildOrchestrator(server, ptyFactory, launchers, allowedRepoPath: repoPath);

            var cmd = new LaunchAgentCommand(
                AgentId: "agent-r1",
                Prompt: null,
                Model: "gpt-5",
                Effort: null,
                RepoPath: repoPath,
                Tools: null,
                AttachmentIds: null,
                Vendor: "codex",
                Kind: LaunchKind.Review,
                Review: new ReviewLaunchInfo("acme", "widgets", 42)
            );

            await orch.HandleLaunchAgentForTest(cmd);

            await Assert.That(server.LaunchFailedCalls.Count).IsEqualTo(1);
            // The old Codex-specific rejection is gone...
            await Assert.That(server.LaunchFailedCalls[0].Reason).DoesNotContain("PR review for Codex");
            // ...and it failed at the shared origin check instead.
            await Assert.That(server.LaunchFailedCalls[0].Reason).Contains("origin");
            await Assert.That(ptyFactory.SpawnCalls).IsEqualTo(0);
        } finally {
            cleanup();
        }
    }

    [Test]
    public async Task Codex_hooks_not_installed_exception_during_prepare_yields_actionable_launch_failed() {
        var (repoPath, cleanup) = CreateGitRepo();

        try {
            var server     = new CaptureServerConnection();
            var ptyFactory = new SpyPtyProcessFactory();

            var codexSpy = new SpyHostedAgentLauncher("codex", cliPath: "spy-codex") {
                PrepareThrow = new CodexHooksNotInstalledException("Run plugin install --codex")
            };

            var launchers = new Dictionary<string, IHostedAgentLauncher> {
                ["codex"] = codexSpy
            };

            await using var orch = BuildOrchestrator(server, ptyFactory, launchers, allowedRepoPath: repoPath);

            var cmd = new LaunchAgentCommand(
                AgentId: "agent-h1",
                Prompt: "go",
                Model: "gpt-5",
                Effort: null,
                RepoPath: repoPath,
                Tools: null,
                AttachmentIds: null,
                Vendor: "codex"
            );

            await orch.HandleLaunchAgentForTest(cmd);

            await Assert.That(server.LaunchFailedCalls.Count).IsEqualTo(1);
            await Assert.That(server.LaunchFailedCalls[0].AgentId).IsEqualTo("agent-h1");
            await Assert.That(server.LaunchFailedCalls[0].Reason).IsEqualTo("Run plugin install --codex");
            await Assert.That(codexSpy.PrepareCalls).IsEqualTo(1);
            await Assert.That(codexSpy.BuildArgsCalls).IsEqualTo(0);
            await Assert.That(ptyFactory.SpawnCalls).IsEqualTo(0);
        } finally {
            cleanup();
        }
    }

    [Test]
    public async Task Stopping_an_agent_releases_a_read_loop_blocked_on_a_full_terminal_queue() {
        var (repoPath, cleanup) = CreateGitRepo();

        try {
            // The send blocks (full/down queue) until its ct cancels; the PTY keeps the
            // stream open so the read loop is genuinely parked inside the blocked send.
            var sendEntered   = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var sendUnblocked = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

            var server     = new CaptureServerConnection { SendEntered = sendEntered, SendUnblocked = sendUnblocked };
            var ptyFactory = new FixedPtyProcessFactory(new OneChunkThenBlockPtyProcess());
            var claudeSpy  = new SpyHostedAgentLauncher("claude", cliPath: "spy-claude");

            var launchers = new Dictionary<string, IHostedAgentLauncher> { ["claude"] = claudeSpy };

            await using var orch = BuildOrchestrator(server, ptyFactory, launchers, allowedRepoPath: repoPath);

            await orch.HandleLaunchAgentForTest(new LaunchAgentCommand(
                AgentId: "agent-bp",
                Prompt: "go",
                Model: "opus",
                Effort: null,
                RepoPath: repoPath,
                Tools: null,
                AttachmentIds: null,
                Vendor: "claude"
            ));

            // Wait until the read loop has produced a chunk and is parked in the blocked send.
            await sendEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));

            // Stopping the agent cancels ReadCts. The blocked enqueue MUST be released by
            // that cancellation; otherwise the read loop (and its finally-block cleanup)
            // stalls until daemon shutdown. Before the fix the enqueue awaited the
            // daemon-lifetime token instead, so this never completes.
            await orch.HandleStopAgentForTest("agent-bp");

            await sendUnblocked.Task.WaitAsync(TimeSpan.FromSeconds(5));
        } finally {
            cleanup();
        }
    }

    [Test]
    public async Task Stopping_an_agent_terminates_promptly_even_when_end_session_is_blocked() {
        // Option B: EndAgentSession is the post-exit backstop and retries across SignalR
        // reconnects, so it can block while the connection recovers. A user-initiated stop
        // must NOT wait on it — HandleStopAgent terminates the process, and the read-loop's
        // finalize backstop ends the session afterwards. With EndAgentSession blocked,
        // termination must still happen promptly (before the fix HandleStopAgent awaited
        // its own EndAgentSession call and never reached TerminateAsync).
        var (repoPath, cleanup) = CreateGitRepo();
        using var endSessionBlock = new CancellationTokenSource();

        try {
            var terminated = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

            var server     = new CaptureServerConnection { EndSessionBlockUntil = endSessionBlock };
            var ptyFactory = new FixedPtyProcessFactory(new TerminateSignalingPtyProcess(terminated));
            var launchers  = new Dictionary<string, IHostedAgentLauncher> { ["claude"] = new SpyHostedAgentLauncher("claude", cliPath: "spy-claude") };

            await using var orch = BuildOrchestrator(server, ptyFactory, launchers, allowedRepoPath: repoPath);

            await orch.HandleLaunchAgentForTest(new LaunchAgentCommand(
                AgentId: "agent-stop",
                Prompt: "go",
                Model: "opus",
                Effort: null,
                RepoPath: repoPath,
                Tools: null,
                AttachmentIds: null,
                Vendor: "claude"
            ));

            // Fire-and-forget: before the fix, HandleStopAgent awaits the blocked
            // EndAgentSession and never reaches termination, so we must not await it here.
            _ = orch.HandleStopAgentForTest("agent-stop");

            // The process must be terminated even though EndAgentSession is still blocked.
            await terminated.Task.WaitAsync(TimeSpan.FromSeconds(5));
        } finally {
            endSessionBlock.Cancel(); // release the finalize backstop's blocked end-session
            cleanup();
        }
    }

    [Test]
    public async Task Cleanup_runs_even_when_end_session_never_recovers() {
        // Qodo: EndAgentSession now retries across reconnects, so it can block for a whole
        // outage. FinalizeAgentRunAsync must not stall local cleanup on it — it waits only
        // up to EndAgentSessionBudget, then proceeds to CleanupAgentAsync (which unregisters
        // the agent) while the retry continues in the background. Here end-session never
        // recovers, yet cleanup must still run.
        var (repoPath, cleanup) = CreateGitRepo();
        using var neverRecovers = new CancellationTokenSource();

        try {
            var unregistered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

            var server = new CaptureServerConnection {
                EndSessionBlockUntil = neverRecovers,                  // end-session blocks for the whole test
                OnAgentUnregistered  = () => unregistered.TrySetResult() // fires when cleanup completes
            };
            var ptyFactory = new FixedPtyProcessFactory(new ImmediateExitPtyProcess());
            var launchers  = new Dictionary<string, IHostedAgentLauncher> { ["claude"] = new SpyHostedAgentLauncher("claude", cliPath: "spy-claude") };

            await using var orch = BuildOrchestrator(server, ptyFactory, launchers, allowedRepoPath: repoPath);
            orch.EndAgentSessionBudget = TimeSpan.FromMilliseconds(250); // don't wait the real 30s in a test

            await orch.HandleLaunchAgentForTest(new LaunchAgentCommand(
                AgentId: "agent-x",
                Prompt: "go",
                Model: "opus",
                Effort: null,
                RepoPath: repoPath,
                Tools: null,
                AttachmentIds: null,
                Vendor: "claude"
            ));

            // The PTY exits immediately → the read loop ends → FinalizeAgentRunAsync runs.
            // End-session blocks (never recovers), but cleanup must still run after the budget.
            await unregistered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        } finally {
            neverRecovers.Cancel(); // release the background end-session task
            cleanup();
        }
    }

    // ══ Task 8: reviewer-model preflight RPC + explicit resolved-model report ══════════════

    /// <summary>Builds an orchestrator whose only runtime factories are the given resolver-carrying spies
    /// (empty launchers, no git repo) — enough to drive the ResolveReviewerModel preflight handler in
    /// isolation from any launch.</summary>
    static AgentOrchestrator BuildPreflightOrchestrator(
            CaptureServerConnection server, SpyPtyProcessFactory ptyFactory,
            params SpyHostedAgentRuntimeFactory[] factories) =>
        BuildOrchestrator(
            server, ptyFactory, new Dictionary<string, IHostedAgentLauncher>(),
            extraRuntimeFactories: factories);

    static SpyHostedAgentRuntimeFactory ResolverFactory(string vendor, IReviewerModelResolver resolver) =>
        new(vendor) { SupportsUnattended = true, ReviewerModelResolver = resolver };

    static ReviewerModelResolveRequestV1 Req(
            string vendor, string model, string requestId = "attempt-1",
            string expectedPolicy = ReviewerModelResolvers.RpcProtocolVersion) =>
        new(requestId, vendor, model, expectedPolicy);

    [Test]
    public async Task Preflight_accepts_a_known_model_and_echoes_correlation_and_protocol_version() {
        var server     = new CaptureServerConnection();
        var ptyFactory = new SpyPtyProcessFactory();
        var claudeSpy  = ResolverFactory("claude", ClaudeReviewerModelResolver.Instance);
        var codexSpy   = ResolverFactory("codex", CodexReviewerModelResolver.Instance);

        await using var orch = BuildPreflightOrchestrator(server, ptyFactory, claudeSpy, codexSpy);

        var resp = orch.HandleResolveReviewerModelForTest(Req("claude", "sonnet", requestId: "corr-42"));

        // Correlation + vendor echoed verbatim; PolicyVersion is the RPC PROTOCOL version (so the
        // server's policy-echo guard passes and a protocol drift is detectable — NOT the per-vendor
        // resolver policy version).
        await Assert.That(resp.RequestId).IsEqualTo("corr-42");
        await Assert.That(resp.Vendor).IsEqualTo("claude");
        await Assert.That(resp.PolicyVersion).IsEqualTo(ReviewerModelResolvers.RpcProtocolVersion);
        await Assert.That(resp.Disposition).IsEqualTo("accepted");
        await Assert.That(resp.CanonicalRequestedModel).IsEqualTo("sonnet");
        await Assert.That(resp.LaunchModel).IsEqualTo("sonnet");
        await Assert.That(resp.EquivalenceKey).IsEqualTo("claude/sonnet");
        await Assert.That(resp.RecognizedVendor).IsNull();
    }

    [Test]
    public async Task Preflight_returns_the_daemon_protocol_version_even_when_the_request_expects_a_different_one() {
        // The daemon returns ITS OWN protocol version, not an echo of the request's expectation — so a
        // server on a newer/older RPC protocol version detects the mismatch and fails closed.
        var server     = new CaptureServerConnection();
        var ptyFactory = new SpyPtyProcessFactory();
        var claudeSpy  = ResolverFactory("claude", ClaudeReviewerModelResolver.Instance);

        await using var orch = BuildPreflightOrchestrator(server, ptyFactory, claudeSpy);

        var resp = orch.HandleResolveReviewerModelForTest(
            Req("claude", "sonnet", expectedPolicy: "reviewer_model_resolve_v999"));

        await Assert.That(resp.PolicyVersion).IsEqualTo(ReviewerModelResolvers.RpcProtocolVersion);
        await Assert.That(resp.PolicyVersion).IsNotEqualTo("reviewer_model_resolve_v999");
    }

    [Test]
    public async Task Preflight_maps_a_cross_vendor_model_to_unavailable_with_recognized_vendor() {
        // The user picked codex as the reviewer vendor but asked for a Claude model. The selected
        // resolver rejects; the OTHER advertised resolver recognizes it → unavailable + RecognizedVendor.
        var server     = new CaptureServerConnection();
        var ptyFactory = new SpyPtyProcessFactory();
        var claudeSpy  = ResolverFactory("claude", ClaudeReviewerModelResolver.Instance);
        var codexSpy   = ResolverFactory("codex", CodexReviewerModelResolver.Instance);

        await using var orch = BuildPreflightOrchestrator(server, ptyFactory, claudeSpy, codexSpy);

        var resp = orch.HandleResolveReviewerModelForTest(Req("codex", "sonnet"));

        await Assert.That(resp.Disposition).IsEqualTo("unavailable");
        await Assert.That(resp.RecognizedVendor).IsEqualTo("claude");
    }

    [Test]
    public async Task Preflight_maps_an_unrecognized_model_to_plain_unavailable() {
        var server     = new CaptureServerConnection();
        var ptyFactory = new SpyPtyProcessFactory();
        var claudeSpy  = ResolverFactory("claude", ClaudeReviewerModelResolver.Instance);
        var codexSpy   = ResolverFactory("codex", CodexReviewerModelResolver.Instance);

        await using var orch = BuildPreflightOrchestrator(server, ptyFactory, claudeSpy, codexSpy);

        var resp = orch.HandleResolveReviewerModelForTest(Req("claude", "nobody-knows-this-model"));

        await Assert.That(resp.Disposition).IsEqualTo("unavailable");
        await Assert.That(resp.RecognizedVendor).IsNull();
    }

    [Test]
    public async Task Preflight_maps_a_malformed_model_to_invalid_with_bounded_diagnostic_code() {
        var server     = new CaptureServerConnection();
        var ptyFactory = new SpyPtyProcessFactory();
        var claudeSpy  = ResolverFactory("claude", ClaudeReviewerModelResolver.Instance);

        await using var orch = BuildPreflightOrchestrator(server, ptyFactory, claudeSpy);

        var resp = orch.HandleResolveReviewerModelForTest(Req("claude", "has a space"));

        await Assert.That(resp.Disposition).IsEqualTo("invalid");
        await Assert.That(resp.DiagnosticCode).IsEqualTo("malformed_model_id");
    }

    [Test]
    public async Task Preflight_bounds_rejects_an_overlong_model_id_as_invalid() {
        var server     = new CaptureServerConnection();
        var ptyFactory = new SpyPtyProcessFactory();
        var claudeSpy  = ResolverFactory("claude", ClaudeReviewerModelResolver.Instance);

        await using var orch = BuildPreflightOrchestrator(server, ptyFactory, claudeSpy);

        var resp = orch.HandleResolveReviewerModelForTest(Req("claude", new string('x', 500)));

        await Assert.That(resp.Disposition).IsEqualTo("invalid");
    }

    [Test]
    public async Task Preflight_for_a_vendor_without_a_resolver_returns_unavailable_old_daemon_shape() {
        // A vendor advertised for unattended but with NO reviewer-model resolver (an old daemon build /
        // an ACP vendor). The selected vendor can't resolve → unavailable, and nothing breaks.
        var server        = new CaptureServerConnection();
        var ptyFactory    = new SpyPtyProcessFactory();
        var noResolverSpy = new SpyHostedAgentRuntimeFactory("cursor") { SupportsUnattended = true };

        await using var orch = BuildPreflightOrchestrator(server, ptyFactory, noResolverSpy);

        var resp = orch.HandleResolveReviewerModelForTest(Req("cursor", "sonnet"));

        await Assert.That(resp.Disposition).IsEqualTo("unavailable");
        await Assert.That(resp.RequestId).IsEqualTo("attempt-1");
        await Assert.That(resp.Vendor).IsEqualTo("cursor");
    }

    [Test]
    public async Task Preflight_selected_acceptance_wins_over_another_recognizing_vendor() {
        // Both fake vendors recognize "shared"; the SELECTED vendor's resolution must win, not the
        // ordinal-first one — the multi-provider selected-acceptance case.
        var server     = new CaptureServerConnection();
        var ptyFactory = new SpyPtyProcessFactory();
        var aardvark   = ResolverFactory("aardvark", new PreflightFakeResolver("aardvark", "shared"));
        var zebra      = ResolverFactory("zebra", new PreflightFakeResolver("zebra", "shared"));

        await using var orch = BuildPreflightOrchestrator(server, ptyFactory, aardvark, zebra);

        var resp = orch.HandleResolveReviewerModelForTest(Req("zebra", "shared"));

        await Assert.That(resp.Disposition).IsEqualTo("accepted");
        await Assert.That(resp.EquivalenceKey).IsEqualTo("zebra/shared");
    }

    [Test]
    public async Task Preflight_has_no_launch_side_effects() {
        // Pure resolution: no PTY spawn, no worktree, no LaunchFailed, no AgentRegistered.
        var server     = new CaptureServerConnection();
        var ptyFactory = new SpyPtyProcessFactory();
        var claudeSpy  = ResolverFactory("claude", ClaudeReviewerModelResolver.Instance);

        await using var orch = BuildPreflightOrchestrator(server, ptyFactory, claudeSpy);

        _ = orch.HandleResolveReviewerModelForTest(Req("claude", "sonnet"));

        await Assert.That(ptyFactory.SpawnCalls).IsEqualTo(0);
        await Assert.That(claudeSpy.StartCalls).IsEqualTo(0);
        await Assert.That(server.LaunchFailedCalls).IsEmpty();
        await Assert.That(server.AgentRegisteredCallCount).IsEqualTo(0);
        await Assert.That(orch.ActiveAgentCountForTest).IsEqualTo(0);
    }

    /// <summary>Minimal per-vendor resolver for the preflight side-effect / selected-acceptance tests —
    /// accepts a fixed model id with a vendor-scoped anchor, unavailable otherwise.</summary>
    sealed class PreflightFakeResolver(string vendor, params string[] recognized) : IReviewerModelResolver {
        public string Vendor        => vendor;
        public string PolicyVersion => $"{vendor}-fake-v1";

        public ReviewerModelResolution Resolve(string requestedModel) =>
            recognized.Contains(requestedModel, StringComparer.Ordinal)
                ? new(ReviewerModelDisposition.Accept,
                    CanonicalRequestedModel: requestedModel, LaunchModel: requestedModel,
                    EquivalenceKey: $"{vendor}/{requestedModel}")
                : new(ReviewerModelDisposition.Unavailable);
    }

    // ── Explicit resolved-model report (builder) ────────────────────────────────────────────

    [Test]
    public async Task Report_for_codex_uses_the_verbatim_launch_model_and_a_resolver_derived_key() {
        var block = new ExplicitReviewerModelLaunch(
            LaunchAttemptId: "attempt-9", LaunchModel: "gpt-5-codex",
            PolicyVersion: "codex-reviewer-model-v1", EquivalenceKey: "codex/gpt-5-codex");

        var report = AgentOrchestrator.BuildExplicitReviewerModelReportForTest(
            "agent-7", "codex", block, CodexReviewerModelResolver.Instance);

        await Assert.That(report).IsNotNull();
        await Assert.That(report!.AgentId).IsEqualTo("agent-7");
        await Assert.That(report.LaunchAttemptId).IsEqualTo("attempt-9");
        await Assert.That(report.Vendor).IsEqualTo("codex");
        // Codex HARD RULE: the reported concrete model is the VERBATIM launch slug — never a
        // date-suffixed metadata model that would drift the slug-level key.
        await Assert.That(report.ResolvedModel).IsEqualTo("gpt-5-codex");
        // Key DERIVED from the concrete model via the resolver, and it equals the server-pinned key.
        await Assert.That(report.EquivalenceKey).IsEqualTo("codex/gpt-5-codex");
        await Assert.That(report.EquivalenceKey).IsEqualTo(block.EquivalenceKey);
        // Report PolicyVersion is the per-vendor resolver policy version (not the RPC protocol version).
        await Assert.That(report.PolicyVersion).IsEqualTo("codex-reviewer-model-v1");
    }

    [Test]
    public async Task Report_for_claude_family_key_matches_the_pinned_key_from_the_launch_model() {
        var block = new ExplicitReviewerModelLaunch(
            LaunchAttemptId: "attempt-3", LaunchModel: "sonnet",
            PolicyVersion: "claude-reviewer-model-v1", EquivalenceKey: "claude/sonnet");

        var report = AgentOrchestrator.BuildExplicitReviewerModelReportForTest(
            "agent-2", "claude", block, ClaudeReviewerModelResolver.Instance);

        await Assert.That(report).IsNotNull();
        await Assert.That(report!.ResolvedModel).IsEqualTo("sonnet");
        await Assert.That(report.EquivalenceKey).IsEqualTo("claude/sonnet");
        await Assert.That(report.EquivalenceKey).IsEqualTo(block.EquivalenceKey);
        await Assert.That(report.PolicyVersion).IsEqualTo("claude-reviewer-model-v1");
    }

    [Test]
    public async Task Report_with_no_resolver_is_null_fails_closed() {
        var block = new ExplicitReviewerModelLaunch("a", "sonnet", "claude-reviewer-model-v1", "claude/sonnet");

        var report = AgentOrchestrator.BuildExplicitReviewerModelReportForTest("agent-1", "claude", block, resolver: null);

        await Assert.That(report).IsNull();
    }

    [Test]
    public async Task Report_with_an_unresolvable_launch_model_is_null_fails_closed() {
        // A launch model the resolver no longer accepts can't derive a meaningful key → no report.
        var block = new ExplicitReviewerModelLaunch("a", "not-a-codex-model", "codex-reviewer-model-v1", "codex/x");

        var report = AgentOrchestrator.BuildExplicitReviewerModelReportForTest(
            "agent-1", "codex", block, CodexReviewerModelResolver.Instance);

        await Assert.That(report).IsNull();
    }

    // ── Backward-compat matrix: which report channel a launch uses ──────────────────────────

    /// <summary>
    /// A vendor that cannot APPLY a model must not have one REPORTED for it.
    ///
    /// <para>Found by code review on the Kiro hosted-agent change. The orchestrator computes one
    /// <c>effectiveModel</c> and uses it twice: as <c>RuntimeStartContext.Model</c> — where a no-op
    /// selector silently discards it — and as the <c>AgentInstance</c> model published via
    /// <c>AgentRegisteredAsync</c>, which drives the live model chip and <c>hosted_agent_started</c>
    /// analytics. So the vendor ran its default while the dashboard claimed the requested model was
    /// live: exactly the requested-vs-running mismatch the no-op selector was chosen to avoid.</para>
    ///
    /// <para>This is the paired assertion — the launch still SUCCEEDS (refusing would make such a
    /// vendor unlaunchable from any caller that always sends a model) but reports no model.</para>
    /// </summary>
    [Test]
    public async Task Interactive_launch_for_a_vendor_that_cannot_select_a_model_reports_no_model_and_still_launches() {
        var (repoPath, cleanup) = CreateGitRepo();

        try {
            var server     = new CaptureServerConnection();
            var ptyFactory = new SpyPtyProcessFactory();
            var kiroSpy    = new SpyHostedAgentRuntimeFactory("kiro") {
                EmitsTerminalOutput    = false,
                SupportsModelSelection = false
            };

            await using var orch = BuildOrchestrator(
                server, ptyFactory, new Dictionary<string, IHostedAgentLauncher>(),
                allowedRepoPath: repoPath, extraRuntimeFactories: [kiroSpy]);

            await orch.HandleLaunchAgentForTest(new LaunchAgentCommand(
                AgentId: "agent-kiro-model",
                Prompt: "do the thing",
                Model: "claude-opus-4-8",   // requested, but this vendor cannot apply it
                Effort: null,
                RepoPath: repoPath,
                Tools: null,
                AttachmentIds: null,
                Vendor: "kiro"));

            // Not refused.
            await Assert.That(server.LaunchFailedCalls).IsEmpty();
            await Assert.That(kiroSpy.StartCalls).IsEqualTo(1);

            // The model is cleared on BOTH paths — the reported one and the runtime context — so
            // nothing downstream can resurrect it.
            await Assert.That(server.AgentRegisteredCalls).Contains(("agent-kiro-model", (string?)null));
            await Assert.That(kiroSpy.LastContext).IsNotNull();
            await Assert.That(kiroSpy.LastContext!.Model).IsNull();
        } finally {
            cleanup();
        }
    }

    /// <summary>
    /// The request-level sibling of the capability-level test above: this vendor CAN select models,
    /// but THIS request did not take (no availableModels match, or the agent rejected the config
    /// option — the selector is best-effort and the vendor's default runs). The runtime's transcript
    /// carries the confirmed outcome (<c>ResolvedModel == null</c>), and the registration must report
    /// that — never the unresolved request the dashboard and analytics would otherwise claim is live.
    /// </summary>
    [Test]
    public async Task Acp_launch_whose_requested_model_did_not_resolve_registers_no_model() {
        var (repoPath, cleanup) = CreateGitRepo();

        try {
            var server        = new CaptureServerConnection();
            var ptyFactory    = new SpyPtyProcessFactory();
            var cursorFactory = new SpyAcpHostedAgentRuntimeFactory { ResolvedModel = null };

            await using var orch = BuildOrchestrator(
                server, ptyFactory, new Dictionary<string, IHostedAgentLauncher>(),
                allowedRepoPath: repoPath, extraRuntimeFactories: [cursorFactory]);
            orch.AcpFinalDrainBudget = TimeSpan.FromMilliseconds(200);

            await orch.HandleLaunchAgentForTest(new LaunchAgentCommand(
                AgentId: "agent-acp-unresolved",
                Prompt: "do the thing",
                Model: "claude-opus-4-8",
                Effort: null,
                RepoPath: repoPath,
                Tools: null,
                AttachmentIds: null,
                Vendor: "cursor"));

            // Not refused, and the REQUEST still reached the runtime (selection stays best-effort
            // there) — this test is about what gets reported, not what gets attempted.
            await Assert.That(server.LaunchFailedCalls).IsEmpty();
            await Assert.That(cursorFactory.StartCalls).IsEqualTo(1);
            await Assert.That(cursorFactory.LastContext!.Model).IsEqualTo("claude-opus-4-8");

            await Assert.That(server.AgentRegisteredCalls).Contains(("agent-acp-unresolved", (string?)null));

            await orch.HandleStopAgentForTest("agent-acp-unresolved");
        } finally {
            cleanup();
        }
    }

    /// <summary>Paired positive: when the handshake CONFIRMS the applied model, the registration
    /// reports the confirmed id — for ACP that is the vendor's own (possibly parameterized) form,
    /// not necessarily the requested string. Proves the fix forwards the confirmation rather than
    /// blanking every ACP launch's model.</summary>
    [Test]
    public async Task Acp_launch_whose_requested_model_resolved_registers_the_confirmed_id() {
        var (repoPath, cleanup) = CreateGitRepo();

        try {
            var server        = new CaptureServerConnection();
            var ptyFactory    = new SpyPtyProcessFactory();
            var cursorFactory = new SpyAcpHostedAgentRuntimeFactory {
                ResolvedModel = "claude-opus-4-8[thinking=true,context=200k]"
            };

            await using var orch = BuildOrchestrator(
                server, ptyFactory, new Dictionary<string, IHostedAgentLauncher>(),
                allowedRepoPath: repoPath, extraRuntimeFactories: [cursorFactory]);
            orch.AcpFinalDrainBudget = TimeSpan.FromMilliseconds(200);

            await orch.HandleLaunchAgentForTest(new LaunchAgentCommand(
                AgentId: "agent-acp-resolved",
                Prompt: "do the thing",
                Model: "claude-opus-4-8",
                Effort: null,
                RepoPath: repoPath,
                Tools: null,
                AttachmentIds: null,
                Vendor: "cursor"));

            await Assert.That(server.LaunchFailedCalls).IsEmpty();
            await Assert.That(server.AgentRegisteredCalls)
                .Contains(("agent-acp-resolved", (string?)"claude-opus-4-8[thinking=true,context=200k]"));

            await orch.HandleStopAgentForTest("agent-acp-resolved");
        } finally {
            cleanup();
        }
    }

    /// <summary>The other half of <see cref="ModelSelectionLaunchPolicy"/>: a PINNED reviewer model is
    /// different in kind from an interactive request. A review round's authority depends on which model
    /// produced it, so silently reviewing with the vendor default — even with truthful metadata — is
    /// worse than not reviewing. Reject, before any worktree or process side effects.</summary>
    [Test]
    public async Task Explicit_reviewer_model_is_rejected_for_a_vendor_that_cannot_select_a_model() {
        var server     = new CaptureServerConnection();
        var ptyFactory = new SpyPtyProcessFactory();
        var kiroSpy    = new SpyHostedAgentRuntimeFactory("kiro") {
            SupportsUnattended     = true,
            SupportsModelSelection = false
        };

        await using var orch = BuildOrchestrator(
            server, ptyFactory, new Dictionary<string, IHostedAgentLauncher>(),
            extraRuntimeFactories: [kiroSpy]);

        await orch.HandleLaunchAgentForTest(new LaunchAgentCommand(
            AgentId: "agent-kiro-pinned",
            Prompt: "review this",
            Model: "opus",
            Effort: null,
            RepoPath: "/tmp/does-not-matter",
            Tools: null,
            AttachmentIds: null,
            Vendor: "kiro",
            ExplicitReviewerModel: new ExplicitReviewerModelLaunch(
                LaunchAttemptId: "attempt-kiro", LaunchModel: "claude-opus-4-8",
                PolicyVersion: "kiro-reviewer-model-v1", EquivalenceKey: "kiro/opus")));

        await Assert.That(server.LaunchFailedCalls).Count().IsEqualTo(1);
        await Assert.That(server.LaunchFailedCalls[0].AgentId).IsEqualTo("agent-kiro-pinned");
        // Names the model it refused to fake, so the failure is actionable rather than generic.
        await Assert.That(server.LaunchFailedCalls[0].Reason).Contains("claude-opus-4-8");

        // Rejected before the runtime was ever started.
        await Assert.That(kiroSpy.StartCalls).IsEqualTo(0);
        await Assert.That(server.AgentRegisteredCalls).IsEmpty();
    }

    [Test]
    public async Task NewNew_explicit_model_launch_reports_on_the_v3_channel_and_launches_verbatim_model() {
        var (repoPath, cleanup) = CreateGitRepo();

        try {
            var server     = new CaptureServerConnection();
            var ptyFactory = new SpyPtyProcessFactory();
            var claudeSpy  = new SpyHostedAgentRuntimeFactory("claude") {
                EmitsTerminalOutput   = false,
                SupportsUnattended    = true,
                ReviewerModelResolver = ClaudeReviewerModelResolver.Instance
            };

            await using var orch = BuildOrchestrator(
                server, ptyFactory, new Dictionary<string, IHostedAgentLauncher>(),
                allowedRepoPath: repoPath, extraRuntimeFactories: [claudeSpy]);

            var cmd = new LaunchAgentCommand(
                AgentId: "agent-v3",
                Prompt: "review this",
                Model: "opus",                       // dispatched value; the block's LaunchModel wins
                Effort: null,
                RepoPath: repoPath,
                Tools: null,
                AttachmentIds: null,
                Vendor: "claude",
                ExplicitReviewerModel: new ExplicitReviewerModelLaunch(
                    LaunchAttemptId: "attempt-v3", LaunchModel: "sonnet",
                    PolicyVersion: "claude-reviewer-model-v1", EquivalenceKey: "claude/sonnet"));

            await orch.HandleLaunchAgentForTest(cmd);

            // The launch threaded the EXACT block LaunchModel through the runtime start context (never
            // recanonicalized, and overriding the dispatched cmd.Model).
            await Assert.That(claudeSpy.LastContext).IsNotNull();
            await Assert.That(claudeSpy.LastContext!.Model).IsEqualTo("sonnet");

            // The registered AgentInstance ALSO carries the pinned LaunchModel — not the dispatched
            // cmd.Model ("opus") — so RegisterAgentAsync / AgentRunStarted / every reconnect
            // re-registration report the model the process actually runs.
            await Assert.That(server.AgentRegisteredCalls).Contains(("agent-v3", "sonnet"));

            // Reported on the v3 channel with the derived key; the legacy channel was NOT used.
            await server.ExplicitReviewerModelReportSignal.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5));
            await Assert.That(server.ExplicitReviewerModelReports).Count().IsEqualTo(1);
            var report = server.ExplicitReviewerModelReports[0];
            await Assert.That(report.AgentId).IsEqualTo("agent-v3");
            await Assert.That(report.LaunchAttemptId).IsEqualTo("attempt-v3");
            await Assert.That(report.ResolvedModel).IsEqualTo("sonnet");
            await Assert.That(report.EquivalenceKey).IsEqualTo("claude/sonnet");
            await Assert.That(server.ReportAgentResolvedModelCalls).IsEmpty();

            await orch.HandleStopAgentForTest("agent-v3");
        } finally {
            cleanup();
        }
    }

    [Test]
    public async Task NewDaemon_oldServer_legacy_codex_launch_uses_the_unchanged_ReportAgentResolvedModel() {
        var (repoPath, cleanup) = CreateGitRepo();

        try {
            var server     = new CaptureServerConnection();
            var ptyFactory = new SpyPtyProcessFactory();
            var codexSpy   = new SpyHostedAgentRuntimeFactory("codex") { EmitsTerminalOutput = false };

            await using var orch = BuildOrchestrator(
                server, ptyFactory, new Dictionary<string, IHostedAgentLauncher>(),
                allowedRepoPath: repoPath, extraRuntimeFactories: [codexSpy]);

            var cmd = new LaunchAgentCommand(
                AgentId: "agent-legacy",
                Prompt: "go",
                Model: "gpt-5-codex",
                Effort: null,
                RepoPath: repoPath,
                Tools: null,
                AttachmentIds: null,
                Vendor: "codex"                       // no ExplicitReviewerModel block → legacy path
            );

            await orch.HandleLaunchAgentForTest(cmd);

            // Legacy channel used (name/arity/behavior unchanged); the v3 channel was NOT used.
            await Assert.That(server.ReportAgentResolvedModelCalls).Contains(("agent-legacy", "gpt-5-codex"));
            await Assert.That(server.ExplicitReviewerModelReports).IsEmpty();

            await orch.HandleStopAgentForTest("agent-legacy");
        } finally {
            cleanup();
        }
    }

    // ══ Owner consent gate wired into the server launch choke point ══════════════════════

    static LaunchConsentGate DenyDefaultGate(string dir) {
        var store = new LaunchConsentStore(dir, NullLogger.Instance);
        store.TryReplace(new LaunchConsentPolicy(LaunchConsentDefault.Deny, 5, []), out _);
        return new LaunchConsentGate(store, new LaunchConsentDecisionLog(dir, NullLogger.Instance),
            prompter: null, TimeProvider.System, NullLogger<LaunchConsentGate>.Instance);
    }

    [Test]
    public async Task Server_launch_denied_under_deny_default_sends_coded_launch_failed() {
        var dir        = Directory.CreateTempSubdirectory("kcap-consent-deny-").FullName;
        var server     = new CaptureServerConnection();
        var ptyFactory = new SpyPtyProcessFactory();
        var claudeSpy  = new SpyHostedAgentLauncher("claude", cliPath: "spy-claude");

        var launchers = new Dictionary<string, IHostedAgentLauncher> { ["claude"] = claudeSpy };

        await using var orch = BuildOrchestrator(server, ptyFactory, launchers, consentGate: DenyDefaultGate(dir));

        var cmd = new LaunchAgentCommand(
            AgentId: "agent-consent-deny",
            Prompt: "do work",
            Model: "opus",
            Effort: null,
            RepoPath: "/tmp/does-not-matter",
            Tools: null,
            AttachmentIds: null,
            Vendor: "claude"
        );

        await orch.HandleLaunchAgentForTest(cmd);

        await Assert.That(server.LaunchFailedCalls.Count).IsEqualTo(1);
        await Assert.That(server.LaunchFailedCalls[0].AgentId).IsEqualTo("agent-consent-deny");
        await Assert.That(server.LaunchFailedCalls[0].Reason).StartsWith(LaunchConsentGate.DeniedReasonPrefix + ":");

        // Denied before any worktree/PTY side effects — the vendor path never runs.
        await Assert.That(claudeSpy.PrepareCalls).IsEqualTo(0);
        await Assert.That(claudeSpy.BuildArgsCalls).IsEqualTo(0);
        await Assert.That(ptyFactory.SpawnCalls).IsEqualTo(0);
    }

    [Test]
    public async Task Owner_launch_proceeds_under_deny_default() {
        var (repoPath, cleanup) = CreateGitRepo();
        var dir = Directory.CreateTempSubdirectory("kcap-consent-owner-").FullName;

        try {
            var server     = new CaptureServerConnection();
            var ptyFactory = new SpyPtyProcessFactory();
            var claudeSpy  = new SpyHostedAgentLauncher("claude", cliPath: "spy-claude");

            var launchers = new Dictionary<string, IHostedAgentLauncher> { ["claude"] = claudeSpy };

            await using var orch = BuildOrchestrator(
                server, ptyFactory, launchers, allowedRepoPath: repoPath, consentGate: DenyDefaultGate(dir));

            var cmd = new LaunchAgentCommand(
                AgentId: "agent-consent-owner",
                Prompt: "do work",
                Model: "opus",
                Effort: null,
                RepoPath: repoPath,
                Tools: null,
                AttachmentIds: null,
                Vendor: "claude",
                RequesterUserId: "user_owner",
                RequesterIsOwner: true
            );

            await orch.HandleLaunchAgentForTest(cmd);

            // No consent-coded denial — the owner bypasses the deny-default policy entirely, and
            // the launch reaches the normal claude vendor path (same assertions as
            // Launch_with_vendor_claude_calls_claude_launcher above — deliberately NOT asserting
            // LaunchFailedCalls is empty: a StubPtyProcess that never produces output can trip the
            // unrelated startup-failure heuristic in the fire-and-forget finalize path, exactly as
            // it can for that neighboring test, which doesn't assert on it either).
            await Assert.That(claudeSpy.BuildArgsCalls).IsEqualTo(1);
            await Assert.That(claudeSpy.PrepareCalls).IsEqualTo(1);
            await Assert.That(ptyFactory.SpawnCalls).IsEqualTo(1);
            await Assert.That(ptyFactory.LastCommand).IsEqualTo("spy-claude");
        } finally {
            cleanup();
        }
    }

    // ── Test doubles ─────────────────────────────────────────────────────

    /// <summary>PTY that has already exited and produces no output, so the read loop ends
    /// immediately and FinalizeAgentRunAsync runs right after launch.</summary>
    sealed class ImmediateExitPtyProcess : IPtyProcess {
        public int  Pid       => 4244;
        public bool HasExited => true;
        public int? ExitCode  => 0;

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

    /// <summary>
    /// PTY that reports already-exited (so HandleStopAgent's graceful window is instant)
    /// and signals when TerminateAsync runs. ReadOutputAsync blocks until ReadCts cancels,
    /// keeping the read loop alive until the stop.
    /// </summary>
    sealed class TerminateSignalingPtyProcess(TaskCompletionSource terminated) : IPtyProcess {
        public int  Pid       => 4243;
        public bool HasExited => true;
        public int? ExitCode  => 0;

        public ValueTask DisposeAsync() => default;
        public Task WaitForExitAsync(TimeSpan? _) => Task.CompletedTask;

        public Task TerminateAsync(TimeSpan? _) {
            terminated.TrySetResult();

            return Task.CompletedTask;
        }

        public async IAsyncEnumerable<byte[]> ReadOutputAsync([EnumeratorCancellation] CancellationToken ct = default) {
            try { await Task.Delay(Timeout.InfiniteTimeSpan, ct); } catch (OperationCanceledException) {
                /* released on stop */
            }

            yield break;
        }

        public Task WriteAsync(string _) => Task.CompletedTask;
        public Task WriteAsync(byte[] _) => Task.CompletedTask;
        public void Resize(ushort     _, ushort __) { }
        public void SendInterrupt() { }
    }

    sealed class SpyHostedAgentLauncher(string vendor, string cliPath) : IHostedAgentLauncher {
        public string Vendor             { get; } = vendor;
        public string CliPath            { get; } = cliPath;
        public bool   SupportsUnattended { get; init; } = true;

        public int        PrepareCalls   { get; private set; }
        public int        BuildArgsCalls { get; private set; }
        public int        CleanupCalls   { get; private set; }
        public Exception? PrepareThrow   { get; init; }

        public bool IsAvailable() => true;

        public void Prepare(LauncherContext ctx) {
            PrepareCalls++;

            if (PrepareThrow is not null) throw PrepareThrow;
        }

        public LaunchArgs BuildArgs(LauncherContext ctx) {
            BuildArgsCalls++;

            return new LaunchArgs(Args: [], McpConfigPath: null);
        }

        public LaunchArgs BuildPassthrough(LauncherContext ctx, IReadOnlyList<string> userArgs) {
            BuildArgsCalls++;

            return new LaunchArgs(Args: [.. userArgs], McpConfigPath: null);
        }

        public void Cleanup(AgentInstance agent) {
            CleanupCalls++;
        }
    }

    /// <summary>
    /// Test double for the Task 10 runtime-selection seam: records that <see cref="StartAsync"/>
    /// was called (and with what agent id) and returns a no-op <see cref="FakeHostedAgentRuntime"/>,
    /// without ever spawning a real process — proves the orchestrator routes a launch to the correct
    /// <see cref="IHostedAgentRuntimeFactory"/> by vendor.
    /// </summary>
    sealed class SpyHostedAgentRuntimeFactory(string vendor) : IHostedAgentRuntimeFactory {
        public string Vendor             { get; } = vendor;
        public bool   SupportsUnattended { get; init; }
        public bool   SupportsBorrowedReviewFlow { get; init; }
        public bool   BorrowedReviewRequiresIndependentSnapshot { get; init; }

        /// <summary>Task 8: lets a test give this factory a reviewer-model resolver so the
        /// orchestrator's ResolveReviewerModel preflight handler can resolve against it.</summary>
        public IReviewerModelResolver? ReviewerModelResolver { get; init; }

        /// <summary>Defaults to <c>true</c>, matching the interface default and every pre-existing
        /// runtime. A test sets <c>false</c> to stand in for a vendor whose model-selection hook is a
        /// no-op (Kiro today), exercising <see cref="ModelSelectionLaunchPolicy"/>.</summary>
        public bool SupportsModelSelection { get; init; } = true;

        /// <summary>Threaded onto the <see cref="FakeHostedAgentRuntime"/> this factory returns —
        /// defaults to <c>false</c> (ACP-shaped) matching this factory's original "cursor" use, but
        /// settable so a test can exercise the PTY-shaped (<c>true</c>) lifecycle branch too.</summary>
        public bool EmitsTerminalOutput { get; init; }

        public int     StartCalls  { get; private set; }
        public string? LastAgentId { get; private set; }
        public RuntimeStartContext? LastContext { get; private set; }
        public Exception? StartThrow { get; init; }

        public FakeHostedAgentRuntime? LastRuntime { get; private set; }

        public bool IsAvailable() => true;

        public Task<HostedRuntimeStart> StartAsync(RuntimeStartContext ctx, CancellationToken ct) {
            StartCalls++;
            LastAgentId = ctx.AgentId;
            LastContext = ctx;
            if (StartThrow is not null) throw StartThrow;

            var runtime = new FakeHostedAgentRuntime(vendor, EmitsTerminalOutput);
            LastRuntime = runtime;

            return Task.FromResult(new HostedRuntimeStart(runtime, McpConfigPath: null));
        }
    }

    /// <summary>No-op <see cref="IHostedAgentRuntime"/> returned by <see cref="SpyHostedAgentRuntimeFactory"/>
    /// so the orchestrator's post-launch RegisterAgentAsync/ReadAgentOutputAsync run against a
    /// harmless stand-in instead of a real PTY or ACP connection. <see cref="ReadOutputAsync"/>
    /// blocks until <see cref="ExitGate"/> is released (or <c>ct</c> cancels) rather than completing
    /// immediately, mirroring the real ACP runtime's "stay open until the process exits" contract —
    /// this lets Fix B/E tests observe orchestrator state (e.g. Status flips to "Running")
    /// WHILE the agent is still live, before ever driving it to completion.</summary>
    sealed class FakeHostedAgentRuntime(string vendor, bool emitsTerminalOutput) : IHostedAgentRuntime {
        public string Vendor              => vendor;
        public int    Pid                 => 0;
        public bool   HasExited           => ExitGate.Task.IsCompleted;
        public int?   ExitCode            => 0;
        public bool   EmitsTerminalOutput => emitsTerminalOutput;

        /// <summary>Released by a test to simulate the hosted process exiting.</summary>
        public TaskCompletionSource ExitGate { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async IAsyncEnumerable<byte[]> ReadOutputAsync([EnumeratorCancellation] CancellationToken ct = default) {
            var ctTcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            await using var reg = ct.Register(() => ctTcs.TrySetResult());
            await Task.WhenAny(ExitGate.Task, ctTcs.Task).ConfigureAwait(false);

            yield break;
        }

        public Task SendUserInputAsync(string    text) => Task.CompletedTask;
        public Task SendSpecialKeyAsync(string    key) => Task.CompletedTask;
        public Task SendRawInputAsync(byte[]      data) => Task.CompletedTask;
        public void Resize(ushort                 cols, ushort rows) { }
        public Task RequestGracefulStopAsync() => Task.CompletedTask;
        public Task WaitForExitAsync(TimeSpan?    timeout = null) => Task.CompletedTask;

        public Task TerminateAsync(TimeSpan? timeout = null) {
            ExitGate.TrySetResult();

            return Task.CompletedTask;
        }

        public ValueTask DisposeAsync() => default;
    }

    /// <summary>Returns a caller-supplied PTY process so a test can control its output behaviour.</summary>
    sealed class FixedPtyProcessFactory(IPtyProcess process) : IPtyProcessFactory {
        public IPtyProcess Spawn(
                string                      command,
                string[]                    args,
                string                      cwd,
                Dictionary<string, string>? extraEnv = null,
                ushort                      cols     = 120,
                ushort                      rows     = 40
            ) => process;
    }

    /// <summary>
    /// Emits one chunk (so the read loop calls SendTerminalOutputAsync) then keeps the
    /// stream open by awaiting the read token — the loop parks in the blocked send until
    /// the agent is stopped. HasExited is true so HandleStopAgent's graceful path is quick.
    /// </summary>
    sealed class OneChunkThenBlockPtyProcess : IPtyProcess {
        public int  Pid       => 4242;
        public bool HasExited => true;
        public int? ExitCode  => 0;

        public ValueTask DisposeAsync() => default;
        public Task WaitForExitAsync(TimeSpan? _) => Task.CompletedTask;
        public Task TerminateAsync(TimeSpan?   _) => Task.CompletedTask;

        public async IAsyncEnumerable<byte[]> ReadOutputAsync([EnumeratorCancellation] CancellationToken ct = default) {
            yield return "x"u8.ToArray();

            await Task.Delay(Timeout.InfiniteTimeSpan, ct);

            yield break;
        }

        public Task WriteAsync(string _) => Task.CompletedTask;
        public Task WriteAsync(byte[] _) => Task.CompletedTask;
        public void Resize(ushort     _, ushort __) { }
        public void SendInterrupt() { }
    }

    sealed class SpyPtyProcessFactory(IPtyProcess? process = null) : IPtyProcessFactory {
        public int                         SpawnCalls  { get; private set; }
        public string?                     LastCommand { get; private set; }
        public string[]?                   LastArgs    { get; private set; }
        public Dictionary<string, string>? LastEnv     { get; private set; }

        public IPtyProcess Spawn(
                string                      command,
                string[]                    args,
                string                      cwd,
                Dictionary<string, string>? extraEnv = null,
                ushort                      cols     = 120,
                ushort                      rows     = 40
            ) {
            SpawnCalls++;
            LastCommand = command;
            LastArgs    = args;
            LastEnv     = extraEnv;

            return process ?? new StubPtyProcess();
        }
    }

    /// <summary>
    /// Stand-in PTY process used after a successful Spawn so the orchestrator's
    /// read-output loop completes immediately and the test doesn't hang waiting
    /// on a real process. ReadOutputAsync yields nothing; everything else is no-op.
    /// </summary>
    sealed class StubPtyProcess : IPtyProcess {
        public int  Pid       => 0;
        public bool HasExited => true;
        public int? ExitCode  => 0;

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

    /// <summary>
    /// Captures LaunchFailedAsync calls and no-ops the other server-side methods
    /// so the orchestrator's launch flow doesn't touch a real SignalR connection.
    /// </summary>
    sealed class CaptureServerConnection() : ServerConnection(
        new() { Name = "test", ServerUrl = "http://127.0.0.1:1" },
        NullLoggerFactory.Instance,
        NullLogger<ServerConnection>.Instance
    ) {
        public string? ConnectionIdForTest { get; init; } = "connection-1";
        internal override string? CurrentConnectionId => ConnectionIdForTest;
        public List<(string AgentId, string Reason)> LaunchFailedCalls { get; } = [];

        /// <summary>When set, EndAgentSessionAsync blocks until this token is cancelled,
        /// simulating a session-end call stuck waiting for a SignalR reconnect.</summary>
        public CancellationTokenSource? EndSessionBlockUntil { get; init; }

        /// <summary>Reasons passed to EndAgentSessionAsync, in call order.</summary>
        public List<string> EndSessionReasons { get; } = [];

        public override Task LaunchFailedAsync(string agentId, string reason) {
            LaunchFailedCalls.Add((agentId, reason));

            return Task.CompletedTask;
        }

        /// <summary>Number of times to fail AgentRegisteredAsync before succeeding (drives
        /// the bounded per-agent re-registration retry test).</summary>
        public int AgentRegisteredFailTimes { get; init; }
        public int AgentRegisteredCallCount { get; private set; }

        /// <summary>Every (agentId, model) pair the orchestrator registered — proves the AgentInstance
        /// the server sees carries the model the process actually runs (the pinned explicit-reviewer
        /// LaunchModel, not the dispatched cmd.Model).</summary>
        public List<(string AgentId, string? Model)> AgentRegisteredCalls { get; } = [];

        /// <summary>The applied Codex posture echoed on each registration, in call order — proves the
        /// initial registration and every reconnect re-registration report the same pair.</summary>
        public List<(string AgentId, string? Sandbox, string? Approval)> AgentRegisteredPostures { get; } = [];

        public override Task AgentRegisteredAsync(
                string agentId, string? prompt, string? model, string? effort, string? repoPath,
                string? sandboxPolicy = null, string? approvalPolicy = null) {
            AgentRegisteredCallCount++;
            lock (AcpCallOrder) AcpCallOrder.Add($"register:{agentId}");
            lock (AgentRegisteredCalls) AgentRegisteredCalls.Add((agentId, model));
            lock (AgentRegisteredPostures) AgentRegisteredPostures.Add((agentId, sandboxPolicy, approvalPolicy));

            return AgentRegisteredCallCount <= AgentRegisteredFailTimes
                ? Task.FromException(new InvalidOperationException("transient re-register failure"))
                : Task.CompletedTask;
        }

        // ── Option B task 4: ACP bind/forward capture ────────────────────────────────────
        // Overrides the RAW hub-invoke seams (mirroring AcpServerConnectionTests' TestServerConnection)
        // rather than the higher-level gated AcpSessionStartedAsync/SendAcpEventsAsync, so every call
        // still goes through the REAL ConnectionRetry/IsReady gating the production wiring relies on.
        // IsReady is overridden to true (no real hub connection exists in these tests) so that gating
        // resolves immediately instead of hanging forever waiting for a connection that never connects.

        internal override bool IsReady => true;

        /// <summary>Every register/bind/events call, in the exact order the orchestrator issued them —
        /// the single source of truth for the bind-ordering and teardown-ordering assertions.</summary>
        public List<string> AcpCallOrder { get; } = [];

        public List<(string AgentId, string Vendor, string AcpSessionId, string? Cwd, string? Model)> AcpSessionStartedCalls { get; } = [];
        public List<(string AgentId, string AcpSessionId, AcpEventEnvelope[] Envelopes)>              AcpEventsCalls         { get; } = [];

        /// <summary>Fires (unbounded, never blocks the caller) once per AcpSessionEvents call, carrying
        /// the 1-based call count — lets a test await "the Nth events call happened" deterministically
        /// instead of guessing with Task.Delay.</summary>
        public Channel<int> AcpEventsCallSignal { get; } = Channel.CreateUnbounded<int>();

        /// <summary>Overrides the ack a given batch receives; defaults to "fully accepted".</summary>
        public Func<AcpEventEnvelope[], AcpBatchAck>? AcpEventsAckOverride { get; init; }

        /// <summary>Set to make the raw AcpSessionEvents invoke hang until this token is cancelled —
        /// simulates a server call that never returns, for the bounded-final-drain test (mirrors
        /// EndSessionBlockUntil's pattern).</summary>
        public CancellationTokenSource? AcpEventsBlockUntil { get; init; }

        /// <summary>One-shot gate: when set, the NEXT raw AcpSessionEvents invoke awaits this task
        /// before returning (then the field is cleared) — lets a test deterministically control
        /// exactly when one specific events call completes, without racing unrelated background
        /// work (e.g. CleanupAgentAsync's own worktree removal) that would otherwise let a call
        /// "eventually" go through regardless of the ordering under test.</summary>
        public TaskCompletionSource? PendingAcpEventsGate { get; set; }

        /// <summary>One-shot gate: when set, the NEXT raw AcpSessionStarted invoke awaits this task
        /// before returning (then the field is cleared) — models a bind call still in flight across
        /// a reconnect outage (reliability fix's stale-binding-race test), independent of
        /// <paramref name="ct"/> so the test controls exactly when the "late bind" resolves.</summary>
        public TaskCompletionSource? PendingAcpBindGate { get; set; }

        internal override async Task InvokeAcpSessionStartedRawAsync(
                string agentId, string vendor, string acpSessionId, string? cwd, string? model,
                IReadOnlyDictionary<string, string>? metadata, CancellationToken ct
            ) {
            lock (AcpCallOrder) {
                AcpCallOrder.Add($"bind:{agentId}");
                AcpSessionStartedCalls.Add((agentId, vendor, acpSessionId, cwd, model));
            }

            if (PendingAcpBindGate is { } gate) {
                PendingAcpBindGate = null;
                await gate.Task;
            }
        }

        internal override async Task<AcpBatchAck> InvokeAcpSessionEventsRawAsync(
                string agentId, string acpSessionId, AcpEventEnvelope[] envelopes, CancellationToken ct
            ) {
            int callCount;

            lock (AcpCallOrder) {
                AcpCallOrder.Add($"events:{agentId}:{string.Join(',', envelopes.Select(e => e.Seq))}");
                AcpEventsCalls.Add((agentId, acpSessionId, envelopes));
                callCount = AcpEventsCalls.Count;
            }

            if (AcpEventsBlockUntil is { } blockCts) {
                // Linked with ct (unlike a bare blockCts.Token wait) so a per-agent CTS cancellation
                // propagates through exactly like a real _hub.InvokeAsync(..., cancellationToken: ct)
                // call would (reliability fix: forwarder-cancel-on-drain-timeout relies on
                // this). A ct-driven cancellation PROPAGATES (mirrors the real hub); blockCts alone
                // is purely test cleanup (releases an otherwise-abandoned background call) and falls
                // through to a normal successful return.
                using var linked = CancellationTokenSource.CreateLinkedTokenSource(blockCts.Token, ct);

                try {
                    await Task.Delay(Timeout.InfiniteTimeSpan, linked.Token);
                } catch (OperationCanceledException) when (ct.IsCancellationRequested) {
                    throw;
                } catch (OperationCanceledException) {
                    /* released by the test (blockCts) */
                }
            }

            // Not interlocked: the forwarder always has exactly one send in flight at a time (its
            // single-in-flight-batch design), so this test double never sees concurrent callers.
            if (PendingAcpEventsGate is { } gate) {
                PendingAcpEventsGate = null;
                await gate.Task;
            }

            AcpEventsCallSignal.Writer.TryWrite(callCount);

            return AcpEventsAckOverride?.Invoke(envelopes) ?? new AcpBatchAck(envelopes[^1].Seq, envelopes[^1].Seq);
        }

        /// <summary>(AgentId, Status) pairs passed to every AgentStatusChangedAsync call, in
        /// call order — lets a test assert on the exact lifecycle transitions the orchestrator
        /// drove (e.g. Fix B/E's immediate Running flip for a no-terminal runtime).</summary>
        public List<(string AgentId, string Status)> StatusChangedCalls { get; } = [];

        public override Task AgentStatusChangedAsync(string agentId, string status, string? sessionId) {
            lock (StatusChangedCalls) StatusChangedCalls.Add((agentId, status));

            return Task.CompletedTask;
        }

        /// <summary>Invoked when AgentUnregisteredAsync runs — the last step of
        /// CleanupAgentAsync, so a useful signal that local cleanup completed.</summary>
        public Action? OnAgentUnregistered { get; init; }

        /// <summary>Every agent id passed to AgentUnregisteredAsync, in call order. Phase B
        /// (D1): a single-flight teardown must unregister an agent exactly once even under a racing
        /// launch-catch + read-loop cleanup.</summary>
        public List<string> AgentUnregisteredCalls { get; } = [];

        public override Task AgentUnregisteredAsync(string agentId) {
            lock (AgentUnregisteredCalls) AgentUnregisteredCalls.Add(agentId);
            OnAgentUnregistered?.Invoke();

            return Task.CompletedTask;
        }

        public override Task UpdateRepoPathsAsync()
            => Task.CompletedTask;

        /// <summary>Set both to make the send block (simulating a full/down terminal
        /// queue) until its <c>ct</c> is cancelled — used by the back-pressure
        /// test. Left null for every other test, where the send is a no-op.</summary>
        public TaskCompletionSource? SendEntered   { get; init; }
        public TaskCompletionSource? SendUnblocked { get; init; }

        public override async Task SendTerminalOutputAsync(string agentId, string base64Data, CancellationToken ct = default) {
            if (SendEntered is null) return;

            SendEntered.TrySetResult();

            try {
                await Task.Delay(Timeout.InfiniteTimeSpan, ct);
            } catch (OperationCanceledException) {
                /* released by the read loop's stop-linked token */
            } finally {
                SendUnblocked?.TrySetResult();
            }
        }

        public override Task AppendAgentRunEventAsync(string agentId, object evt)
            => Task.CompletedTask;

        // ── Task 8: resolved-model report capture ──────────────────────────────────
        /// <summary>Every legacy (agentId, model) pair passed to ReportAgentResolvedModelAsync — proves
        /// the no-model / vendor-only launch keeps using the unchanged legacy channel.</summary>
        public List<(string AgentId, string Model)> ReportAgentResolvedModelCalls { get; } = [];

        /// <summary>Every explicit-model report passed to ReportExplicitReviewerModelResolvedAsync — proves
        /// an explicit-model launch reports the concrete resolved model on the dedicated v3 channel.</summary>
        public List<ExplicitReviewerModelResolvedV1> ExplicitReviewerModelReports { get; } = [];

        /// <summary>Signals each ExplicitReviewerModelResolvedAsync call (1-based count) so a test can
        /// await the fire-and-forget report deterministically instead of racing Task.Delay.</summary>
        public Channel<int> ExplicitReviewerModelReportSignal { get; } = Channel.CreateUnbounded<int>();

        public override Task ReportAgentResolvedModelAsync(string agentId, string model) {
            lock (ReportAgentResolvedModelCalls) ReportAgentResolvedModelCalls.Add((agentId, model));

            return Task.CompletedTask;
        }

        public override Task ReportExplicitReviewerModelResolvedAsync(ExplicitReviewerModelResolvedV1 report) {
            int count;
            lock (ExplicitReviewerModelReports) {
                ExplicitReviewerModelReports.Add(report);
                count = ExplicitReviewerModelReports.Count;
            }
            ExplicitReviewerModelReportSignal.Writer.TryWrite(count);

            return Task.CompletedTask;
        }

        public override async Task<EndAgentSessionResult> EndAgentSessionAsync(string agentId, string reason) {
            EndSessionReasons.Add(reason);
            lock (AcpCallOrder) AcpCallOrder.Add($"endSession:{agentId}");

            if (EndSessionBlockUntil is { } cts) {
                try { await Task.Delay(Timeout.InfiniteTimeSpan, cts.Token); } catch (OperationCanceledException) {
                    /* released by the test */
                }
            }

            return new EndAgentSessionResult();
        }

        public override Task<PermissionDecision> RequestPermissionAsync(
                string            sessionId,
                string?           toolName,
                JsonElement?      toolInput,
                JsonElement?      suggestions,
                CancellationToken ct = default
            ) => Task.FromResult(new PermissionDecision("deny", null, null));
    }

    /// <summary>Minimal IHttpClientFactory so the orchestrator can be constructed without DI.</summary>
    sealed class StubHttpClientFactory : IHttpClientFactory {
        public HttpClient CreateClient(string name) => new();
    }
}
