using Capacitor.Cli.Core;
using Capacitor.Cli.Daemon.Acp;
using Capacitor.Cli.Daemon.Services;
using Capacitor.Cli.Tests.Unit.Acp;
using Microsoft.Extensions.Logging.Abstractions;

namespace Capacitor.Cli.Tests.Unit;

/// <summary>
/// The orchestrator's finalizer verdict arm — the registered-agent report seam. When an ACP
/// reviewer's launch-window reap verdict (published by
/// <see cref="AcpHostedAgentRuntime.TryStartReap"/>) is observed by
/// <see cref="AgentOrchestrator.FinalizeAgentRunAsync"/> for an agent that already registered
/// (post-successful-<c>StartAsync</c>, so the factory's own reclassification never had the chance
/// to fire — see <see cref="Report_sent_exactly_once_across_factory_and_finalizer"/>'s Part A),
/// the finalizer reports it as its FIRST action — before the process-exit wait — exactly once,
/// failure-contained, and forces terminal Failed regardless of the child's exit code. Post-window
/// reaps are deliberately excluded: today's teardown for that case must stay byte-identical.
///
/// Partial of <see cref="AgentOrchestratorVendorTests"/> to reuse its BuildOrchestrator /
/// CaptureServerConnection / CreateGitRepo / SpyHostedAgentRuntimeFactory harness.
/// </summary>
public partial class AgentOrchestratorVendorTests {
    /// <summary>
    /// Minimal <see cref="IAcpProcess"/> whose <see cref="WaitForExitAsync"/> is a SEPARATE,
    /// test-controlled gate from <see cref="HasExited"/>/<see cref="ExitCode"/> — unlike
    /// production (where "the process exited" and "WaitForExitAsync resolves" are the same
    /// signal), this lets a test hold <c>FinalizeAgentRunAsync</c>'s process-exit wait open
    /// independently of exit state, to prove the verdict report happens strictly BEFORE that wait
    /// resolves.
    /// </summary>
    sealed class FinalizerTestAcpProcess : IAcpProcess {
        readonly TaskCompletionSource _waitGate = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public int  Pid            => 9191;
        public bool HasExited      { get; private set; }
        public int? ExitCode       { get; private set; }
        public int  TerminateCalls { get; private set; }

        /// <summary>Sets exit state AND releases the wait gate — the normal production coupling,
        /// for tests that don't care about ordering.</summary>
        public void SignalExited(int exitCode = 0) {
            HasExited = true;
            ExitCode  = exitCode;
            _waitGate.TrySetResult();
        }

        public Task WaitForExitAsync(TimeSpan? timeout = null) => _waitGate.Task;

        public Task TerminateAsync(TimeSpan? timeout = null) {
            TerminateCalls++;
            SignalExited(ExitCode ?? 0);

            return Task.CompletedTask;
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    /// <summary>
    /// Builds a REAL <see cref="AcpHostedAgentRuntime"/> — never <c>StartAsync</c>'d; these tests
    /// publish a verdict directly via <c>TryStartReap</c>/<c>FirstTurnSettledForTest</c> rather
    /// than driving a full handshake — backed by a controllable process and inert in-memory
    /// connection streams (<see cref="FakeAcpAgent"/>, unstarted: no protocol traffic is ever
    /// sent). A real runtime is required, not a fake <see cref="IHostedAgentRuntime"/>: the
    /// orchestrator's verdict arm pattern-matches the CONCRETE <see cref="AcpHostedAgentRuntime"/>
    /// type to reach <c>Verdict</c>.
    /// </summary>
    static (AcpHostedAgentRuntime Runtime, FinalizerTestAcpProcess Process, FakeAcpAgent Fake) BuildVerdictRuntime(
            string agentId) {
        var fake    = new FakeAcpAgent();
        var conn    = new AcpConnection(fake.ClientWriteStream, fake.ClientReadStream, NullLogger.Instance);
        var process = new FinalizerTestAcpProcess();
        var runtime = new AcpHostedAgentRuntime(conn, process, NullLogger.Instance, agentId: agentId);

        return (runtime, process, fake);
    }

    /// <summary>Constructs an <see cref="AgentInstance"/> directly — <c>SeedAgentForTest</c> only
    /// builds PTY runtimes — wrapping the given ACP runtime, and registers it via
    /// <c>RegisterAgentForTest</c> so it is reachable exactly like a real launch's registration.</summary>
    static AgentInstance SeedAcpAgent(
            AgentOrchestrator orch, string agentId, IHostedAgentRuntime runtime, string status = "Running") {
        var agent = new AgentInstance(
            agentId, "review this", "default", null, "/repo", "cursor",
            runtime,
            new WorktreeInfo("/repo", "b", "/repo"),
            new CancellationTokenSource()) {
            Status = status
        };

        orch.RegisterAgentForTest(agent);

        return agent;
    }

    [Test]
    public async Task Report_sent_before_exit_wait() {
        var server = new CaptureServerConnection();
        await using var orch = BuildOrchestrator(
            server, new SpyPtyProcessFactory(), new Dictionary<string, IHostedAgentLauncher>());

        var (runtime, process, fake) = BuildVerdictRuntime("wedge-1");
        await using var _ = fake;

        var claimed = runtime.TryStartReap(
            "kiro_reviewer_mcp_surface_unexpected: violation", () => Task.CompletedTask);
        await Assert.That(claimed).IsTrue();
        await Assert.That(runtime.Verdict!.ReapedInsideLaunchWindow).IsTrue();

        var agent = SeedAcpAgent(orch, "wedge-1", runtime);

        var finalizeTask = orch.FinalizeAgentRunForTest(agent);

        // Poll for the report rather than assuming synchronous completion — the barrier assertion
        // below is what actually proves ordering, deterministically, regardless of scheduling.
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(15);
        while (server.LaunchFailedCalls.Count == 0 && DateTime.UtcNow < deadline)
            await Task.Delay(10);

        await Assert.That(server.LaunchFailedCalls.Count).IsEqualTo(1);
        await Assert.That(server.LaunchFailedCalls[0].AgentId).IsEqualTo("wedge-1");
        await Assert.That(server.LaunchFailedCalls[0].Reason).Contains("kiro_reviewer_mcp_surface_unexpected");

        // The barrier: the process-exit wait is STILL held — finalize cannot have completed —
        // proving the report really was sent BEFORE that wait, not merely before some later step.
        await Assert.That(finalizeTask.IsCompleted).IsFalse();

        process.SignalExited(0);

        await finalizeTask.WaitAsync(TimeSpan.FromSeconds(30));

        await Assert.That(agent.Status).IsEqualTo("Failed");
        await Assert.That(server.AgentUnregisteredCalls).Contains("wedge-1");
    }

    [Test]
    public async Task Report_sent_exactly_once_across_factory_and_finalizer() {
        // ── Part A: the factory path — a reap during StartAsync. No AgentInstance is ever
        // created (StartAsync throws before PublishAgent runs in HandleLaunchAgentCore), so the
        // finalizer's verdict arm structurally never gets a chance to ALSO fire for this launch
        // attempt. This is what "the guard flag must be shared" is defending against — expressed
        // here as: the factory path is independently exactly-once too, and produces no
        // AgentInstance for the finalizer to duplicate against.
        var (repoPath, cleanup) = CreateGitRepo();

        try {
            var factoryServer  = new CaptureServerConnection();
            var reapingFactory = new SpyHostedAgentRuntimeFactory("cursor") {
                StartThrow = new InvalidOperationException(
                    "kiro_reviewer_mcp_surface_unexpected: violation (transport: read loop ended)")
            };

            await using var factoryOrch = BuildOrchestrator(
                factoryServer, new SpyPtyProcessFactory(), new Dictionary<string, IHostedAgentLauncher>(),
                allowedRepoPath: repoPath, extraRuntimeFactories: [reapingFactory]);

            await factoryOrch.HandleLaunchAgentForTest(new LaunchAgentCommand(
                AgentId: "factory-reap-1",
                Prompt: "go",
                Model: "",
                Effort: null,
                RepoPath: repoPath,
                Tools: null,
                AttachmentIds: null,
                Vendor: "cursor"
            ));

            await Assert.That(factoryServer.LaunchFailedCalls.Count(c => c.AgentId == "factory-reap-1")).IsEqualTo(1);
            await Assert.That(factoryServer.LaunchFailedCalls.Single(c => c.AgentId == "factory-reap-1").Reason)
                .Contains("kiro_reviewer_mcp_surface_unexpected");
            await Assert.That(factoryOrch.GetAgentForTest("factory-reap-1")).IsNull();
        } finally {
            cleanup();
        }

        // ── Part B: the finalizer path, invoked TWICE for the SAME agent — simulating a
        // hypothetical re-entrant/racing call to prove the per-agent guard is structural, not
        // merely a consequence of today's single-call-site shape.
        var server = new CaptureServerConnection();
        await using var orch = BuildOrchestrator(
            server, new SpyPtyProcessFactory(), new Dictionary<string, IHostedAgentLauncher>());

        var (runtime, process, fake) = BuildVerdictRuntime("finalizer-reap-1");
        await using var _ = fake;

        runtime.TryStartReap(
            "unattended_interaction_forbidden:session/request_permission", () => Task.CompletedTask);
        process.SignalExited(0);

        var agent = SeedAcpAgent(orch, "finalizer-reap-1", runtime);

        await orch.FinalizeAgentRunForTest(agent);
        await orch.FinalizeAgentRunForTest(agent); // re-entrant call — must not double-report

        await Assert.That(server.LaunchFailedCalls.Count(c => c.AgentId == "finalizer-reap-1")).IsEqualTo(1);
    }

    [Test]
    public async Task Faulted_report_never_skips_cleanup_or_unregister() {
        var server = new CaptureServerConnection {
            LaunchFailedThrow = new InvalidOperationException("transient SignalR failure")
        };
        await using var orch = BuildOrchestrator(
            server, new SpyPtyProcessFactory(), new Dictionary<string, IHostedAgentLauncher>());

        var (runtime, process, fake) = BuildVerdictRuntime("faulted-report-1");
        await using var _ = fake;

        runtime.TryStartReap("kiro_reviewer_mcp_surface_unexpected: violation", () => Task.CompletedTask);
        process.SignalExited(0);

        var agent = SeedAcpAgent(orch, "faulted-report-1", runtime);

        // Must complete without throwing — a report fault must never propagate out of finalize.
        await orch.FinalizeAgentRunForTest(agent).WaitAsync(TimeSpan.FromSeconds(30));

        await Assert.That(server.LaunchFailedCalls.Count(c => c.AgentId == "faulted-report-1")).IsEqualTo(1); // attempted
        await Assert.That(server.AgentUnregisteredCalls).Contains("faulted-report-1");                        // cleanup still ran
        await Assert.That(agent.Status).IsEqualTo("Failed");                                                  // status force still applied
        await Assert.That(orch.GetAgentForTest("faulted-report-1")).IsNull();                                 // unregistered
    }

    [Test]
    public async Task Published_verdict_forces_terminal_failed_regardless_of_exit_code() {
        var server = new CaptureServerConnection();
        await using var orch = BuildOrchestrator(
            server, new SpyPtyProcessFactory(), new Dictionary<string, IHostedAgentLauncher>());

        var (runtime, process, fake) = BuildVerdictRuntime("clean-exit-reap-1");
        await using var _ = fake;

        runtime.TryStartReap("kiro_reviewer_mcp_surface_unexpected: violation", () => Task.CompletedTask);
        process.SignalExited(0); // clean exit — would compute "Completed" absent the fix

        var agent = SeedAcpAgent(orch, "clean-exit-reap-1", runtime);

        await orch.FinalizeAgentRunForTest(agent).WaitAsync(TimeSpan.FromSeconds(30));

        await Assert.That(agent.Status).IsEqualTo("Failed");

        // No later status transition clears the reason: the finalizer's own exit-code-driven
        // classification block is a structural no-op once Status is already terminal, so it must
        // never have sent ANY AgentStatusChanged for this agent — not even a "Failed" one; the
        // hub's LaunchFailed handling already marks the registry entry Failed with the reason, and
        // a redundant AgentStatusChanged is exactly the seam a future edit could turn into a
        // non-failure clear.
        await Assert.That(server.StatusChangedCalls.Any(c => c.AgentId == "clean-exit-reap-1")).IsFalse();
    }

    [Test]
    public async Task Post_window_reap_sends_no_launchfailed_teardown_byte_identical() {
        var server = new CaptureServerConnection();
        await using var orch = BuildOrchestrator(
            server, new SpyPtyProcessFactory(), new Dictionary<string, IHostedAgentLauncher>());

        var (runtime, process, fake) = BuildVerdictRuntime("post-window-1");
        await using var _ = fake;

        // Close the launch window BEFORE reaping (mirrors a real first turn settling) —
        // TryStartReap then classifies OUTSIDE the window.
        runtime.FirstTurnSettledForTest.TrySetResult();
        var claimed = runtime.TryStartReap("some_later_administrative_reap", () => Task.CompletedTask);
        await Assert.That(claimed).IsTrue();
        await Assert.That(runtime.Verdict!.ReapedInsideLaunchWindow).IsFalse();

        process.SignalExited(0);

        var agent = SeedAcpAgent(orch, "post-window-1", runtime);

        await orch.FinalizeAgentRunForTest(agent).WaitAsync(TimeSpan.FromSeconds(30));

        // No LaunchFailed from the verdict arm.
        await Assert.That(server.LaunchFailedCalls.Any(c => c.AgentId == "post-window-1")).IsFalse();

        // Today's teardown, byte-identical: exit code 0 + EmitsTerminalOutput==false (ACP) means
        // the existing startup-failure classification is skipped too (gated on
        // EmitsTerminalOutput), status resolves to "Completed" via the plain exit-code path, and
        // IS reported — exactly as it would be with no verdict machinery in the picture at all.
        await Assert.That(agent.Status).IsEqualTo("Completed");
        await Assert.That(server.StatusChangedCalls).Contains(("post-window-1", "Completed"));
        await Assert.That(server.AgentUnregisteredCalls).Contains("post-window-1");
    }

    [Test]
    public async Task Empty_reason_fallback_carries_exception_type() {
        // Direct unit coverage of the mapping helper itself (the general-purpose fallback cover).
        await Assert.That(AgentOrchestrator.MapLaunchFailureReason(null, "SomeSource"))
            .IsEqualTo("launch_failed:SomeSource — see daemon log");
        await Assert.That(AgentOrchestrator.MapLaunchFailureReason("   ", "SomeSource"))
            .IsEqualTo("launch_failed:SomeSource — see daemon log");
        await Assert.That(AgentOrchestrator.MapLaunchFailureReason("real-reason", "SomeSource"))
            .IsEqualTo("real-reason");

        // Integration: an (unrealistic — TryStartReap does not validate its reason — but
        // reachable) empty verdict reason must still report something diagnosable, never a blank
        // string that renders as "unknown failure" with no way to grep the daemon log.
        var server = new CaptureServerConnection();
        await using var orch = BuildOrchestrator(
            server, new SpyPtyProcessFactory(), new Dictionary<string, IHostedAgentLauncher>());

        var (runtime, process, fake) = BuildVerdictRuntime("empty-reason-1");
        await using var _ = fake;

        runtime.TryStartReap("", () => Task.CompletedTask);
        await Assert.That(runtime.Verdict!.Reason).IsEmpty();

        process.SignalExited(0);

        var agent = SeedAcpAgent(orch, "empty-reason-1", runtime);

        await orch.FinalizeAgentRunForTest(agent).WaitAsync(TimeSpan.FromSeconds(30));

        var reported = server.LaunchFailedCalls.Single(c => c.AgentId == "empty-reason-1").Reason;
        await Assert.That(reported).IsNotEmpty();
        await Assert.That(reported).Contains("TerminationVerdict");
        await Assert.That(reported).Contains("launch_failed:");
    }

    // ── StopAgentCoreAsync must not walk a published verdict's Failed back to Completed ────────
    // Design spec §3.3's "no non-failure AgentStatusChanged after a published verdict" guarantee
    // has a second seam beyond the finalizer itself: StopAgentCoreAsync is the single funnel for
    // EVERY stop trigger (HandleStopAgent's own doc comment: server StopAgent, sequenced
    // StopAgentV2, and the heartbeat's own TTL/idle/stuck-Starting reap sweep — plus a
    // local-socket stop, which calls it directly) and runs unconditionally while the agent is
    // still published in _agents, which is exactly the window between the finalizer's report and
    // CleanupAgentAsync's unpublish. A reviewer that trips a containment tripwire is a plausible
    // candidate for the SAME heartbeat tick's TTL/idle reap, so this is a real race, not a
    // hypothetical one. These two tests seed the guard state directly (rather than driving a full
    // concurrent finalize+stop race, which the rest of this file's tests already show reports
    // LaunchFailureVerdictReported=1 and Status="Failed" — see e.g.
    // Published_verdict_forces_terminal_failed_regardless_of_exit_code) so the race window itself
    // is exercised deterministically.

    [Test]
    public async Task Stop_after_published_verdict_does_not_clear_failed_status_or_reason() {
        var server = new CaptureServerConnection();
        await using var orch = BuildOrchestrator(
            server, new SpyPtyProcessFactory(), new Dictionary<string, IHostedAgentLauncher>());

        var agent = orch.SeedAgentForTest("stop-race-1", status: "Running");

        // Simulate the finalizer's verdict arm having already run — it reports the coded reason
        // then forces terminal Failed — BEFORE CleanupAgentAsync has unpublished this agent, i.e.
        // the exact window a concurrent stop can land in.
        agent.LaunchFailureVerdictReported = 1;
        agent.Status                       = "Failed";

        await orch.HandleStopAgentForTest("stop-race-1");

        // No non-failure AgentStatusChanged at all — not even a REPEATED "Failed" one, since the
        // gate suppresses the whole call, matching Published_verdict_forces_terminal_failed_regardless_of_exit_code's
        // "zero calls" bar rather than merely "no Completed call".
        await Assert.That(server.StatusChangedCalls.Any(c => c.AgentId == "stop-race-1")).IsFalse();
        await Assert.That(agent.Status).IsEqualTo("Failed");
    }

    [Test]
    public async Task Stop_without_a_published_verdict_still_transitions_to_completed_and_notifies() {
        // Regression check: an ordinary stop (no verdict ever published — the overwhelmingly
        // common case, e.g. every claude/codex PTY agent, and every ACP agent that never trips a
        // containment tripwire) must still behave exactly as before this fix.
        var server = new CaptureServerConnection();
        await using var orch = BuildOrchestrator(
            server, new SpyPtyProcessFactory(), new Dictionary<string, IHostedAgentLauncher>());

        var agent = orch.SeedAgentForTest("stop-normal-1", status: "Running");
        await Assert.That(agent.LaunchFailureVerdictReported).IsEqualTo(0); // precondition: no verdict

        await orch.HandleStopAgentForTest("stop-normal-1");

        await Assert.That(server.StatusChangedCalls).Contains(("stop-normal-1", "Completed"));
        await Assert.That(agent.Status).IsEqualTo("Completed");
    }

    /// <summary>Part A final-review fix wave, item 2: the publish→report race. A same-tick
    /// TTL/idle/stuck-Starting sweep can reach StopAgentCoreAsync in the instant AFTER
    /// TryStartReap has published the verdict but BEFORE the finalizer's CAS has set
    /// LaunchFailureVerdictReported — this seeds exactly that instant (verdict published, guard
    /// flag still 0) rather than the fully-reported state
    /// Stop_after_published_verdict_does_not_clear_failed_status_or_reason already covers.</summary>
    [Test]
    public async Task Stop_racing_a_published_but_not_yet_reported_verdict_does_not_emit_completed() {
        var server = new CaptureServerConnection();
        await using var orch = BuildOrchestrator(
            server, new SpyPtyProcessFactory(), new Dictionary<string, IHostedAgentLauncher>());

        var (runtime, process, fake) = BuildVerdictRuntime("stop-race-2");
        await using var _ = fake;

        var claimed = runtime.TryStartReap(
            "kiro_reviewer_mcp_surface_unexpected: violation", () => Task.CompletedTask);
        await Assert.That(claimed).IsTrue();
        await Assert.That(runtime.Verdict!.ReapedInsideLaunchWindow).IsTrue();
        process.SignalExited(0);

        var agent = SeedAcpAgent(orch, "stop-race-2", runtime, status: "Running");
        // Precondition: the verdict is published, but nothing has reported it yet — the finalizer
        // never ran for this agent in this test.
        await Assert.That(agent.LaunchFailureVerdictReported).IsEqualTo(0);

        await orch.HandleStopAgentForTest("stop-race-2");

        await Assert.That(server.StatusChangedCalls.Any(c => c.AgentId == "stop-race-2")).IsFalse();
        await Assert.That(agent.Status).IsNotEqualTo("Completed");
    }

    // ── Part A final-review fix wave, item 1: §3.5 wired into the general launch-failure catch ──
    // MapLaunchFailureReason/DescribeLaunchFailure previously covered ONLY the finalizer's verdict
    // arm; the orchestrator's general catch (HandleLaunchAgentCore, the site a reclassified
    // AcpReviewerReapedException — and any other pre-StartAsync factory failure — actually lands
    // on) still forwarded ex.Message raw, so a blank/whitespace message reached LaunchFailed
    // unmapped. These two tests exercise that general catch directly (SpyHostedAgentRuntimeFactory
    // .StartThrow, no AgentInstance ever created — the same "pre-insert failure" landing zone
    // Report_sent_exactly_once_across_factory_and_finalizer's Part A already proved is the sole
    // reporter for this scenario).

    [Test]
    public async Task Factory_prespawn_failure_with_blank_message_reports_the_typed_fallback() {
        var (repoPath, cleanup) = CreateGitRepo();

        try {
            var server = new CaptureServerConnection();
            var blankMessageFactory = new SpyHostedAgentRuntimeFactory("cursor") {
                StartThrow = new InvalidOperationException("   ")
            };

            await using var orch = BuildOrchestrator(
                server, new SpyPtyProcessFactory(), new Dictionary<string, IHostedAgentLauncher>(),
                allowedRepoPath: repoPath, extraRuntimeFactories: [blankMessageFactory]);

            await orch.HandleLaunchAgentForTest(new LaunchAgentCommand(
                AgentId: "blank-msg-1",
                Prompt: "go",
                Model: "",
                Effort: null,
                RepoPath: repoPath,
                Tools: null,
                AttachmentIds: null,
                Vendor: "cursor"
            ));

            var reported = server.LaunchFailedCalls.Single(c => c.AgentId == "blank-msg-1").Reason;
            await Assert.That(reported).IsEqualTo($"launch_failed:{nameof(InvalidOperationException)} — see daemon log");
        } finally {
            cleanup();
        }
    }

    [Test]
    public async Task Factory_prespawn_failure_with_a_real_message_passes_it_through_verbatim() {
        var (repoPath, cleanup) = CreateGitRepo();

        try {
            var server = new CaptureServerConnection();
            var realMessageFactory = new SpyHostedAgentRuntimeFactory("cursor") {
                StartThrow = new InvalidOperationException("cursor-agent binary not found on PATH")
            };

            await using var orch = BuildOrchestrator(
                server, new SpyPtyProcessFactory(), new Dictionary<string, IHostedAgentLauncher>(),
                allowedRepoPath: repoPath, extraRuntimeFactories: [realMessageFactory]);

            await orch.HandleLaunchAgentForTest(new LaunchAgentCommand(
                AgentId: "real-msg-1",
                Prompt: "go",
                Model: "",
                Effort: null,
                RepoPath: repoPath,
                Tools: null,
                AttachmentIds: null,
                Vendor: "cursor"
            ));

            var reported = server.LaunchFailedCalls.Single(c => c.AgentId == "real-msg-1").Reason;
            await Assert.That(reported).IsEqualTo("cursor-agent binary not found on PATH");
        } finally {
            cleanup();
        }
    }
}
