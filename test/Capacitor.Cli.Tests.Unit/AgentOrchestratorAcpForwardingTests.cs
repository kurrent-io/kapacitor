// test/Capacitor.Cli.Tests.Unit/AgentOrchestratorAcpForwardingTests.cs
using System.Runtime.CompilerServices;
using System.Threading.Channels;
using Capacitor.Cli.Core;
using Capacitor.Cli.Daemon.Services;

namespace Capacitor.Cli.Tests.Unit;

/// <summary>
/// Option B task 4: covers the orchestrator wiring that ties tasks 1–3 into the launch/
/// teardown lifecycle — <c>AgentOrchestrator.HandleLaunchAgent</c>'s post-registration ACP
/// bind + forwarder start, and <see cref="AgentOrchestrator"/>'s teardown path's bounded final-drain
/// before <c>EndAgentSession</c>. Reuses the shared <see cref="AgentOrchestratorVendorTests"/> test
/// seam (<c>BuildOrchestrator</c>, <c>CaptureServerConnection</c>) — the ACP-specific capture fields
/// added there (<c>AcpCallOrder</c>, <c>AcpSessionStartedCalls</c>, <c>AcpEventsCalls</c>,
/// <c>AcpEventsCallSignal</c>) let these tests assert exact call order deterministically instead of
/// guessing with <c>Task.Delay</c>, since the launch-time ACP wiring is fire-and-forget from
/// <c>HandleLaunchAgent</c> (must never stall the launch on a slow/blocked bind — see
/// <c>StartAcpForwardingAsync</c>'s remarks).
/// </summary>
public partial class AgentOrchestratorVendorTests {
    static readonly TimeSpan AcpHangGuard = TimeSpan.FromSeconds(5);

    static LaunchAgentCommand NewCursorLaunch(string agentId, string repoPath) => new(
        AgentId: agentId,
        Prompt: "do work",
        Model: "auto",
        Effort: null,
        RepoPath: repoPath,
        Tools: null,
        AttachmentIds: null,
        Vendor: "cursor"
    );

    // ── Bind ordering (design spec §2.3) ────────────────────────────────────────────────────────

    [Test]
    public async Task Cursor_launch_binds_after_register_and_before_events_and_registers_the_binding() {
        var (repoPath, cleanup) = CreateGitRepo();

        try {
            var server        = new CaptureServerConnection();
            var ptyFactory    = new SpyPtyProcessFactory();
            var cursorFactory = new SpyAcpHostedAgentRuntimeFactory();

            await using var orch = BuildOrchestrator(
                server, ptyFactory, new Dictionary<string, IHostedAgentLauncher>(),
                allowedRepoPath: repoPath, extraRuntimeFactories: [cursorFactory]
            );
            orch.AcpFinalDrainBudget = TimeSpan.FromMilliseconds(200);

            var cmd = NewCursorLaunch("agent-acp-bind", repoPath);

            await orch.HandleLaunchAgentForTest(cmd);

            // The launch-time ACP wiring (bind -> registerBinding -> buildEnvelope -> start
            // forwarder) is fire-and-forget from HandleLaunchAgent (must never stall a launch on a
            // slow bind) — wait deterministically for the first AcpSessionEvents call (always
            // SessionStarted@0) as proof the whole chain ran at least once.
            await server.AcpEventsCallSignal.Reader.ReadAsync().AsTask().WaitAsync(AcpHangGuard);

            var registerIndex    = server.AcpCallOrder.IndexOf("register:agent-acp-bind");
            var bindIndex        = server.AcpCallOrder.IndexOf("bind:agent-acp-bind");
            var firstEventsIndex = server.AcpCallOrder.FindIndex(e => e.StartsWith("events:agent-acp-bind:", StringComparison.Ordinal));

            await Assert.That(registerIndex).IsGreaterThanOrEqualTo(0);
            await Assert.That(bindIndex).IsGreaterThan(registerIndex);
            await Assert.That(firstEventsIndex).IsGreaterThan(bindIndex);

            // RegisterAcpBinding was called: a reconnect re-bind (task 3's ReBindAcpSessionsAsync)
            // must re-invoke AcpSessionStarted for this agent — UnregisterAcpBinding is NOT
            // observable directly (non-virtual), so this is the indirect proof task 3's own tests
            // use for the same seam.
            var bindCallsBefore = server.AcpSessionStartedCalls.Count;
            await server.ReBindAcpSessionsAsync();
            await Assert.That(server.AcpSessionStartedCalls.Count).IsEqualTo(bindCallsBefore + 1);
            await Assert.That(server.AcpSessionStartedCalls[^1].AgentId).IsEqualTo("agent-acp-bind");
            await Assert.That(server.AcpSessionStartedCalls[^1].Vendor).IsEqualTo("cursor");
            await Assert.That(server.AcpSessionStartedCalls[^1].AcpSessionId).IsEqualTo(cursorFactory.LastRuntime!.AcpSessionId);

            await orch.HandleStopAgentForTest("agent-acp-bind");
        } finally {
            cleanup();
        }
    }

    // ── Forwarding (design spec §2.2/§2.4) ──────────────────────────────────────────────────────

    [Test]
    public async Task Cursor_launch_forwards_SessionStarted_first_then_subsequent_envelopes_with_monotonic_seq() {
        var (repoPath, cleanup) = CreateGitRepo();

        try {
            var server        = new CaptureServerConnection();
            var ptyFactory    = new SpyPtyProcessFactory();
            var cursorFactory = new SpyAcpHostedAgentRuntimeFactory();

            await using var orch = BuildOrchestrator(
                server, ptyFactory, new Dictionary<string, IHostedAgentLauncher>(),
                allowedRepoPath: repoPath, extraRuntimeFactories: [cursorFactory]
            );
            orch.AcpFinalDrainBudget = TimeSpan.FromMilliseconds(200);

            var cmd = NewCursorLaunch("agent-acp-forward", repoPath);

            await orch.HandleLaunchAgentForTest(cmd);

            var call1 = await server.AcpEventsCallSignal.Reader.ReadAsync().AsTask().WaitAsync(AcpHangGuard);
            await Assert.That(call1).IsEqualTo(1);

            var firstBatch = server.AcpEventsCalls[0].Envelopes;
            await Assert.That(firstBatch.Length).IsEqualTo(1);
            await Assert.That(firstBatch[0].Kind).IsEqualTo(AcpEventKind.SessionStarted);
            await Assert.That(firstBatch[0].Seq).IsEqualTo(0);
            await Assert.That(firstBatch[0].RawSessionId).IsEqualTo(cursorFactory.LastRuntime!.AcpSessionId);

            // Mirrors task 2's aggregator emitting a translated envelope mid-turn — the forwarder
            // must pick it up off the SAME channel and assign it the next monotonic seq.
            cursorFactory.LastRuntime.EnvelopesWriter.TryWrite(new AcpEventEnvelope(Kind: AcpEventKind.AssistantText, Text: "hello"));

            var call2 = await server.AcpEventsCallSignal.Reader.ReadAsync().AsTask().WaitAsync(AcpHangGuard);
            await Assert.That(call2).IsEqualTo(2);

            var secondBatch = server.AcpEventsCalls[1].Envelopes;
            await Assert.That(secondBatch.Any(e => e.Kind == AcpEventKind.AssistantText && e.Text == "hello")).IsTrue();
            await Assert.That(secondBatch[0].Seq).IsEqualTo(1);

            await orch.HandleStopAgentForTest("agent-acp-forward");
        } finally {
            cleanup();
        }
    }

    // ── Teardown ordering (design spec §2.3 terminal ownership) ────────────────────────────────

    [Test]
    public async Task Teardown_drains_the_transcript_before_ending_the_session_then_unregisters_the_binding() {
        var (repoPath, cleanup) = CreateGitRepo();

        try {
            var unregistered  = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var server        = new CaptureServerConnection { OnAgentUnregistered = () => unregistered.TrySetResult() };
            var ptyFactory    = new SpyPtyProcessFactory();
            var cursorFactory = new SpyAcpHostedAgentRuntimeFactory();

            await using var orch = BuildOrchestrator(
                server, ptyFactory, new Dictionary<string, IHostedAgentLauncher>(),
                allowedRepoPath: repoPath, extraRuntimeFactories: [cursorFactory]
            );
            orch.AcpFinalDrainBudget = TimeSpan.FromSeconds(2); // long enough that the gated send below is
                                                                 // still observably pending at the check point

            var cmd = NewCursorLaunch("agent-acp-teardown", repoPath);

            await orch.HandleLaunchAgentForTest(cmd);
            await server.AcpEventsCallSignal.Reader.ReadAsync().AsTask().WaitAsync(AcpHangGuard); // SessionStarted@0

            // Gate the NEXT AcpSessionEvents call (the trailing "final words" send) so its
            // completion is entirely test-controlled, rather than racing CleanupAgentAsync's own
            // (unrelated) later dispose/worktree-removal steps — that race would let this test pass
            // even without the ordering guarantee actually wired up.
            var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            server.PendingAcpEventsGate = gate;

            var runtime = cursorFactory.LastRuntime!;
            runtime.EnvelopesWriter.TryWrite(new AcpEventEnvelope(Kind: AcpEventKind.AssistantText, Text: "final words"));

            // Ends the process (releases ReadOutputAsync -> FinalizeAgentRunAsync). HandleStopAgent
            // itself never touches ACP forwarding, so it returns promptly regardless of the gate.
            await orch.HandleStopAgentForTest(cmd.AgentId).WaitAsync(AcpHangGuard);

            // Give FinalizeAgentRunAsync a moment to reach (and block inside) the gated send.
            await Task.Delay(TimeSpan.FromMilliseconds(150));

            // While the drain is still gated, EndAgentSession must NOT have been called yet — the
            // load-bearing ordering guarantee (drain-before-finalize, design spec §2.3).
            await Assert.That(server.AcpCallOrder).DoesNotContain($"endSession:{cmd.AgentId}");
            await Assert.That(unregistered.Task.IsCompleted).IsFalse();

            // Release the gate: the drain completes comfortably inside its 2s budget.
            gate.TrySetResult();

            await unregistered.Task.WaitAsync(AcpHangGuard);

            // The runtime was disposed (completing the transcript channel) as part of the drain.
            await Assert.That(runtime.DisposeCount).IsGreaterThanOrEqualTo(1);

            // The trailing envelope reached the server — the bounded drain actually drained it.
            await Assert.That(server.AcpEventsCalls.SelectMany(c => c.Envelopes).Any(e => e.Text == "final words")).IsTrue();

            // Ordering: every events call precedes EndAgentSession.
            var lastEventsIndex = server.AcpCallOrder.FindLastIndex(e => e.StartsWith("events:", StringComparison.Ordinal));
            var endSessionIndex = server.AcpCallOrder.IndexOf($"endSession:{cmd.AgentId}");

            await Assert.That(lastEventsIndex).IsGreaterThanOrEqualTo(0);
            await Assert.That(endSessionIndex).IsGreaterThan(lastEventsIndex);

            // UnregisterAcpBinding ran (non-virtual — verified indirectly, same as task 3's own
            // tests): a reconnect re-bind no longer touches this now-dead agent.
            var bindCallsBefore = server.AcpSessionStartedCalls.Count;
            await server.ReBindAcpSessionsAsync();
            await Assert.That(server.AcpSessionStartedCalls.Count).IsEqualTo(bindCallsBefore);
        } finally {
            cleanup();
        }
    }

    [Test]
    public async Task Teardown_proceeds_to_EndAgentSession_within_the_budget_even_if_the_drain_never_completes() {
        var (repoPath, cleanup) = CreateGitRepo();
        using var neverRecovers = new CancellationTokenSource();

        try {
            var unregistered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

            var server = new CaptureServerConnection {
                OnAgentUnregistered = () => unregistered.TrySetResult(),
                AcpEventsBlockUntil = neverRecovers // every AcpSessionEvents call (incl. SessionStarted@0) hangs
            };
            var ptyFactory    = new SpyPtyProcessFactory();
            var cursorFactory = new SpyAcpHostedAgentRuntimeFactory();

            await using var orch = BuildOrchestrator(
                server, ptyFactory, new Dictionary<string, IHostedAgentLauncher>(),
                allowedRepoPath: repoPath, extraRuntimeFactories: [cursorFactory]
            );
            var budget = TimeSpan.FromMilliseconds(300);
            orch.AcpFinalDrainBudget = budget; // must NOT wait the real 5s default

            var cmd = NewCursorLaunch("agent-acp-hang", repoPath);

            await orch.HandleLaunchAgentForTest(cmd);

            // Give the fire-and-forget bind + forwarder start a moment to actually begin its (now
            // permanently blocked) first send before ending the agent.
            await Task.Delay(TimeSpan.FromMilliseconds(100));

            var stopwatch = System.Diagnostics.Stopwatch.StartNew();
            await orch.HandleStopAgentForTest(cmd.AgentId).WaitAsync(AcpHangGuard);

            // The bounded drain gives up after ~budget; EndAgentSession must still run promptly —
            // proving the permanently-hung send never pinned teardown (the load-bearing guarantee
            // this whole mechanism exists to protect).
            await unregistered.Task.WaitAsync(AcpHangGuard);
            stopwatch.Stop();

            // Timing is the discriminator: too fast (well under the budget) would mean no drain was
            // even attempted; hanging past AcpHangGuard above would already have failed the test —
            // asserting a FLOOR close to the budget proves an actual bounded wait happened, not that
            // teardown just skipped the drain outright.
            await Assert.That(stopwatch.Elapsed).IsGreaterThanOrEqualTo(budget - TimeSpan.FromMilliseconds(100));

            await Assert.That(server.AcpCallOrder).Contains($"endSession:{cmd.AgentId}");
        } finally {
            neverRecovers.Cancel(); // release the still-blocked background send so nothing leaks past the test
            cleanup();
        }
    }

    // ── Reliability fixes (PR #301 review: per-agent CTS) ───────────────────────────────

    /// <summary>
    /// Qodo #4: a drain that misses its budget must not leave the forwarder's <c>RunTask</c> running
    /// forever in the background. The per-agent CTS created at launch is cancelled specifically on
    /// drain-timeout (<c>AgentOrchestrator.FinalDrainAcpTranscriptAsync</c>) — this test proves
    /// that cancellation actually reaches the forwarder's blocked send (via
    /// <c>SendAcpEventsAsync</c>'s <c>ct</c> parameter, which the test double now honors like a real
    /// hub invoke would) so <c>RunTask</c> completes ON ITS OWN, without the test manually releasing
    /// the block the way the (unrelated) budget test above has to.
    /// </summary>
    [Test]
    public async Task Drain_timeout_cancels_the_forwarder_so_its_RunTask_completes_without_leaking() {
        var (repoPath, cleanup) = CreateGitRepo();
        using var neverRecovers = new CancellationTokenSource();

        try {
            var unregistered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

            var server = new CaptureServerConnection {
                OnAgentUnregistered = () => unregistered.TrySetResult(),
                AcpEventsBlockUntil = neverRecovers // every AcpSessionEvents call hangs until cancelled
            };
            var ptyFactory    = new SpyPtyProcessFactory();
            var cursorFactory = new SpyAcpHostedAgentRuntimeFactory();

            await using var orch = BuildOrchestrator(
                server, ptyFactory, new Dictionary<string, IHostedAgentLauncher>(),
                allowedRepoPath: repoPath, extraRuntimeFactories: [cursorFactory]
            );
            var budget = TimeSpan.FromMilliseconds(300);
            orch.AcpFinalDrainBudget = budget; // must NOT wait the real 5s default

            var cmd = NewCursorLaunch("agent-acp-cancel-on-timeout", repoPath);

            await orch.HandleLaunchAgentForTest(cmd);

            // Give the fire-and-forget bind + forwarder start a moment to actually begin its (now
            // permanently blocked) first send before ending the agent.
            await Task.Delay(TimeSpan.FromMilliseconds(100));

            var forwarderHandle = orch.GetAgentForTest(cmd.AgentId)!.AcpForwarder!;

            await orch.HandleStopAgentForTest(cmd.AgentId).WaitAsync(AcpHangGuard);
            await unregistered.Task.WaitAsync(AcpHangGuard);

            // The crux of the fix: cancelling the per-agent CTS on drain-timeout must actually reach
            // the forwarder's in-flight send and unwind it — RunTask completes on its own, with NO
            // help from this test (contrast the budget test above, which has to Cancel() its own
            // blockCts in a `finally` to avoid leaking the background task).
            await forwarderHandle.RunTask.WaitAsync(AcpHangGuard);
        } finally {
            neverRecovers.Cancel(); // belt-and-suspenders — a no-op if the fix already released it
            cleanup();
        }
    }

    /// <summary>
    /// Qodo #5 / Codex P1 #2: the bind-vs-finalize stale-binding race. <c>AcpSessionStartedAsync</c>'s
    /// <c>ConnectionRetry</c> gating can block across a reconnect outage; if the agent's WHOLE
    /// lifecycle (launch → exit → finalize → cleanup) runs to completion while that bind is still
    /// in flight, the late bind resolving afterwards must NOT register a binding for the now-dead
    /// agent — <c>AgentOrchestrator.StartAcpForwardingAsync</c>'s liveness check (per-agent
    /// CTS cancelled + <c>agent</c> no longer tracked) must abort before ever reaching
    /// <c>RegisterAcpBinding</c>, and the finalizer's own unconditional
    /// <c>UnregisterAcpBinding(agent.Id)</c> must leave nothing registered even if it ran first.
    /// </summary>
    [Test]
    public async Task Agent_finalized_before_the_bind_completes_registers_no_binding_for_the_late_bind() {
        var (repoPath, cleanup) = CreateGitRepo();

        try {
            var unregistered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var server        = new CaptureServerConnection { OnAgentUnregistered = () => unregistered.TrySetResult() };
            var ptyFactory    = new SpyPtyProcessFactory();
            var cursorFactory = new SpyAcpHostedAgentRuntimeFactory();

            await using var orch = BuildOrchestrator(
                server, ptyFactory, new Dictionary<string, IHostedAgentLauncher>(),
                allowedRepoPath: repoPath, extraRuntimeFactories: [cursorFactory]
            );
            orch.AcpFinalDrainBudget = TimeSpan.FromMilliseconds(200);

            // Gate the bind call so it's still pending when the agent finalizes — models the launch
            // racing a reconnect outage that outlasts the agent's whole lifetime.
            var bindGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            server.PendingAcpBindGate = bindGate;

            var cmd = NewCursorLaunch("agent-acp-stale-bind", repoPath);

            await orch.HandleLaunchAgentForTest(cmd);

            // The bind call has actually been ISSUED (recorded before the gate await) — give it a
            // moment, then confirm it landed and is now gated/pending.
            await Task.Delay(TimeSpan.FromMilliseconds(150));
            await Assert.That(server.AcpSessionStartedCalls.Count(c => c.AgentId == cmd.AgentId)).IsEqualTo(1);
            await Assert.That(orch.GetAgentForTest(cmd.AgentId)!.AcpForwarder).IsNull(); // bind hasn't resolved yet

            // Run the agent's WHOLE lifecycle to completion WHILE the bind is still pending.
            await orch.HandleStopAgentForTest(cmd.AgentId).WaitAsync(AcpHangGuard);
            await unregistered.Task.WaitAsync(AcpHangGuard);
            await Assert.That(orch.GetAgentForTest(cmd.AgentId)).IsNull(); // CleanupAgentAsync removed it

            // NOW the late bind "succeeds" server-side.
            bindGate.TrySetResult();

            // Give the late setup task a moment to resume past the bind and reach its liveness check.
            await Task.Delay(TimeSpan.FromMilliseconds(150));

            // The liveness check must have aborted BEFORE RegisterAcpBinding — a reconnect re-bind
            // must never see this dead agent (proof no binding leaked, whether from the late setup
            // OR from the finalizer running before the bind resolved).
            var bindCallsBefore = server.AcpSessionStartedCalls.Count;
            await server.ReBindAcpSessionsAsync();
            await Assert.That(server.AcpSessionStartedCalls.Count).IsEqualTo(bindCallsBefore);
        } finally {
            cleanup();
        }
    }

    // ── PTY unaffected ───────────────────────────────────────────────────────────────────────────

    [Test]
    public async Task Claude_launch_never_touches_the_Acp_hub_methods() {
        var (repoPath, cleanup) = CreateGitRepo();

        try {
            // A PTY that stays "running" (ReadOutputAsync blocks) until stopped — keeps the agent
            // alive long enough to assert AgentInstance.AcpForwarder without racing the (fast, fire-
            // and-forget) finalize/cleanup path a StubPtyProcess's instant exit would otherwise win.
            var terminated = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var server      = new CaptureServerConnection();
            var ptyFactory  = new FixedPtyProcessFactory(new TerminateSignalingPtyProcess(terminated));
            var claudeSpy   = new SpyHostedAgentLauncher("claude", cliPath: "spy-claude");
            var launchers   = new Dictionary<string, IHostedAgentLauncher> { ["claude"] = claudeSpy };

            await using var orch = BuildOrchestrator(server, ptyFactory, launchers, allowedRepoPath: repoPath);

            var cmd = new LaunchAgentCommand(
                AgentId: "agent-pty-unaffected",
                Prompt: "go",
                Model: "opus",
                Effort: null,
                RepoPath: repoPath,
                Tools: null,
                AttachmentIds: null,
                Vendor: "claude"
            );

            await orch.HandleLaunchAgentForTest(cmd);

            // Let any fire-and-forget ACP wiring run, if it were (incorrectly) triggered for a PTY
            // agent — HostedRuntimeStart.Transcript is null for the PTY factory, so it never should.
            await Task.Delay(TimeSpan.FromMilliseconds(200));

            await Assert.That(server.AcpSessionStartedCalls).IsEmpty();
            await Assert.That(server.AcpEventsCalls).IsEmpty();
            await Assert.That(orch.GetAgentForTest(cmd.AgentId)!.AcpForwarder).IsNull();

            await orch.HandleStopAgentForTest(cmd.AgentId);
        } finally {
            cleanup();
        }
    }

    // ── Failure isolation (design spec §2.3, load-bearing correctness point 3) ─────────────────

    [Test]
    public async Task Forwarder_fault_does_not_crash_the_agent_or_the_orchestrator() {
        var (repoPath, cleanup) = CreateGitRepo();

        try {
            var server        = new CaptureServerConnection();
            var ptyFactory    = new SpyPtyProcessFactory();
            var cursorFactory = new SpyAcpHostedAgentRuntimeFactory();

            await using var orch = BuildOrchestrator(
                server, ptyFactory, new Dictionary<string, IHostedAgentLauncher>(),
                allowedRepoPath: repoPath, extraRuntimeFactories: [cursorFactory]
            );
            orch.AcpFinalDrainBudget = TimeSpan.FromMilliseconds(200);

            var cmd = NewCursorLaunch("agent-acp-fault", repoPath);

            await orch.HandleLaunchAgentForTest(cmd);
            await server.AcpEventsCallSignal.Reader.ReadAsync().AsTask().WaitAsync(AcpHangGuard); // SessionStarted@0

            var agent           = orch.GetAgentForTest(cmd.AgentId)!;
            var forwarderHandle = agent.AcpForwarder!;

            // Simulate a translate/forward bug escaping into the forwarder loop: fault the
            // transcript channel itself. This escapes RunAsync's own top-level try (which only
            // absorbs ITS OWN cancellation) — exactly the shape ForwardAcpTranscriptAsync's outer
            // catch exists for.
            cursorFactory.LastRuntime!.EnvelopesWriter.TryComplete(new InvalidOperationException("simulated translate/forward bug"));

            // The wrapped run task must complete SUCCESSFULLY (not faulted) — proving the fault
            // never escaped to crash the agent or the daemon.
            await forwarderHandle.RunTask.WaitAsync(AcpHangGuard);

            // The orchestrator itself is unaffected — the still-live agent can still be stopped
            // cleanly afterwards.
            await orch.HandleStopAgentForTest(cmd.AgentId).WaitAsync(AcpHangGuard);
        } finally {
            cleanup();
        }
    }

    // ── Test doubles ─────────────────────────────────────────────────────

    /// <summary>
    /// <see cref="IHostedAgentRuntime"/> + <see cref="IAcpTranscriptSource"/> test double mirroring
    /// the real <c>AcpHostedAgentRuntime</c>'s dual role (task 2) closely enough to exercise task 4's
    /// wiring: <see cref="ReadOutputAsync"/> stays open until <see cref="TerminateAsync"/>/cancellation
    /// (exactly like the real ACP runtime, and <see cref="FakeHostedAgentRuntime"/> above), and
    /// <see cref="DisposeAsync"/> completes the <see cref="Envelopes"/> channel — mirroring the real
    /// runtime's <c>DisposeAsync</c> completing its transcript channel — so the bounded final-drain
    /// path has something real to observe.
    /// </summary>
    sealed class FakeAcpRuntime : IHostedAgentRuntime, IAcpTranscriptSource {
        readonly Channel<AcpEventEnvelope> _envelopes = Channel.CreateUnbounded<AcpEventEnvelope>();

        public string Vendor              => "cursor";
        public int    Pid                 => 0;
        public bool   HasExited           => ExitGate.Task.IsCompleted;
        public int?   ExitCode            => 0;
        public bool   EmitsTerminalOutput => false;

        public string  AcpSessionId  { get; init; } = "acp-sess-1";
        public string  Cwd           { get; init; } = "/tmp/acp-wt";
        public string? ResolvedModel { get; init; } = "gpt-x";

        public ChannelReader<AcpEventEnvelope> Envelopes       => _envelopes.Reader;
        public ChannelWriter<AcpEventEnvelope> EnvelopesWriter => _envelopes.Writer;

        /// <summary>Released by a test (via TerminateAsync, driven by HandleStopAgent) to simulate
        /// the ACP process exiting.</summary>
        public TaskCompletionSource ExitGate { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        /// <summary>Number of times DisposeAsync ran — proves teardown actually disposed the
        /// runtime (and, since it's harmless to call twice, that CleanupAgentAsync's own later
        /// dispose call is a safe no-op-ish repeat, exactly like the real idempotent-guarded runtime).</summary>
        public int DisposeCount { get; private set; }

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

        public ValueTask DisposeAsync() {
            DisposeCount++;
            _envelopes.Writer.TryComplete(); // mirrors task 2's real DisposeAsync completing the transcript channel

            return default;
        }
    }

    /// <summary>
    /// <see cref="IHostedAgentRuntimeFactory"/> test double for the ACP seam: returns a
    /// <see cref="HostedRuntimeStart"/> with <see cref="FakeAcpRuntime"/> threaded onto BOTH
    /// <c>Runtime</c> and <c>Transcript</c> — mirroring <c>AcpHostedAgentRuntimeFactory</c>'s real
    /// wiring, where the runtime IS its own transcript source.
    /// </summary>
    sealed class SpyAcpHostedAgentRuntimeFactory(string vendor = "cursor") : IHostedAgentRuntimeFactory {
        public string Vendor             { get; } = vendor;
        public bool   SupportsUnattended => false;

        /// <summary>Threaded onto the <see cref="FakeAcpRuntime"/> as its handshake-confirmed model —
        /// null stands in for a request that did not take (no match / agent rejected the option).</summary>
        public string? ResolvedModel { get; init; } = "gpt-x";

        public int                  StartCalls  { get; private set; }
        public string?              LastAgentId { get; private set; }
        public RuntimeStartContext? LastContext { get; private set; }
        public FakeAcpRuntime?      LastRuntime { get; private set; }

        public bool IsAvailable() => true;

        public Task<HostedRuntimeStart> StartAsync(RuntimeStartContext ctx, CancellationToken ct) {
            StartCalls++;
            LastAgentId = ctx.AgentId;
            LastContext = ctx;

            var runtime = new FakeAcpRuntime { ResolvedModel = ResolvedModel };
            LastRuntime = runtime;

            return Task.FromResult(new HostedRuntimeStart(runtime, McpConfigPath: null, Transcript: runtime));
        }
    }
}
