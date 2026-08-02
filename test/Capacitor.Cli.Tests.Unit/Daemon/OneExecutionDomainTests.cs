using System.Collections.Concurrent;
using Capacitor.Cli.Core;
using Capacitor.Cli.Daemon.Pty;
using Capacitor.Cli.Daemon.Services;
using Capacitor.Cli.Tests.Unit.Daemon;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;

namespace Capacitor.Cli.Tests.Unit;

// Spec §3.3 (ONE execution domain), orchestrator level: how the launch/stop HANDLERS route, what the pump
// is free to do while a launch parks on consent, the publication/transition barrier, the internal-reaping
// bypass, and the shutdown teardown/handoff layers. The lane's own mechanics (coalescing, admission,
// alarm, shutdown settlement) are pinned at the processor level in OneExecutionDomainProcessorTests.
//
// Reuses the AgentOrchestratorVendorTests harness (BuildOrchestrator/SpyPtyProcessFactory/
// SpyHostedAgentLauncher/SeqCaptureServerConnection/CapturingLogger/DenyDefaultGate/CreateGitRepo) — see
// HealBarrierReportTests.cs for the precedent of a second partial-class file doing the same.
public partial class AgentOrchestratorVendorTests {
    static readonly TimeSpan Bounded = TimeSpan.FromSeconds(30);

    /// <summary>Parks every <see cref="ILaunchConsentPrompter.PromptAsync"/> call on a per-request
    /// TaskCompletionSource (keyed by <see cref="LaunchConsentPromptRequest.RequestId"/>, which is the agent
    /// id) until the test resolves it, or until the caller's <c>ct</c> fires — treated GRACEFULLY as "no
    /// answer" (a plain timeout-shaped null) rather than a thrown OperationCanceledException, since these
    /// tests are not exercising cancellation semantics (that is <see cref="CancelingPrompter"/>'s job).
    /// <c>HasSubscriber</c>/<c>WaitForSubscriberAsync</c> always succeed immediately so <c>DecideAsync</c>
    /// reaches <c>PromptAsync</c> with no real wait; a FakeTimeProvider is still handed to the gate so no
    /// real clock is ever touched.</summary>
    sealed class ParkingPrompter : ILaunchConsentPrompter {
        readonly ConcurrentDictionary<string, TaskCompletionSource<bool?>> _pending = new();
        readonly ConcurrentDictionary<string, TaskCompletionSource> _arrived = new();

        public bool HasSubscriber => true;

        public Task<bool> WaitForSubscriberAsync(TimeSpan wait, TimeProvider time, CancellationToken ct) =>
            Task.FromResult(true);

        public Task<bool?> PromptAsync(LaunchConsentPromptRequest req, TimeSpan timeout, TimeProvider time, CancellationToken ct) {
            var tcs = _pending.GetOrAdd(req.RequestId,
                _ => new TaskCompletionSource<bool?>(TaskCreationOptions.RunContinuationsAsynchronously));
            ct.Register(() => tcs.TrySetResult(null));
            Arrived(req.RequestId).TrySetResult();
            return tcs.Task;
        }

        TaskCompletionSource Arrived(string requestId) =>
            _arrived.GetOrAdd(requestId, _ => new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously));

        /// <summary>Completes once the gate has actually reached PromptAsync for this agent — i.e. the launch
        /// is DEQUEUED and parked, not merely committed.</summary>
        public Task WaitForPromptAsync(string agentId) => Arrived(agentId).Task;

        public void Resolve(string agentId, bool? answer) {
            if (_pending.TryGetValue(agentId, out var tcs)) tcs.TrySetResult(answer);
        }

        /// <summary>Repeatedly resolves whatever prompts are CURRENTLY pending, retrying until
        /// <paramref name="done"/> reports success. One shot is not enough: on a SERIAL lane a later item's
        /// PromptAsync is not even called — so its TCS does not exist yet — until the earlier item settles.</summary>
        public async Task ResolveUntilAsync(bool? answer, Func<bool> done) {
            var deadline = DateTime.UtcNow + Bounded;
            while (true) {
                foreach (var tcs in _pending.Values) tcs.TrySetResult(answer);
                if (done()) return;
                if (DateTime.UtcNow >= deadline)
                    throw new TimeoutException("ParkingPrompter.ResolveUntilAsync: condition not met within the timeout.");
                await Task.Delay(10);
            }
        }
    }

    /// <summary>Mirrors the REAL LaunchConsentBroker's documented external-cancellation behavior ("an
    /// external ct cancellation always rethrows") closely enough to pin the SEQUENCED LANE's classification
    /// of that OperationCanceledException, without depending on the real broker/IPC machinery (already
    /// covered by LaunchConsentBrokerTests/LaunchConsentGateTests). <c>Register</c> fires
    /// synchronously for an already-cancelled token, so this is deterministic regardless of whether
    /// <c>ct</c> fires before or after PromptAsync is reached.</summary>
    sealed class CancelingPrompter : ILaunchConsentPrompter {
        public bool HasSubscriber => true;

        public Task<bool> WaitForSubscriberAsync(TimeSpan wait, TimeProvider time, CancellationToken ct) =>
            Task.FromResult(true);

        public Task<bool?> PromptAsync(LaunchConsentPromptRequest req, TimeSpan timeout, TimeProvider time, CancellationToken ct) {
            var tcs = new TaskCompletionSource<bool?>(TaskCreationOptions.RunContinuationsAsynchronously);
            ct.Register(() => tcs.TrySetException(new OperationCanceledException(ct)));
            return tcs.Task;
        }
    }

    /// <summary>An <see cref="IHostApplicationLifetime"/> whose <see cref="ApplicationStopping"/> is a REAL,
    /// test-controlled token — <see cref="StubHostLifetime"/> (every other test's default) is fixed at
    /// <see cref="CancellationToken.None"/> and can never fire. AgentOrchestrator links its internal
    /// <c>_shutdownCts</c> to this token AT CONSTRUCTION via
    /// <see cref="CancellationTokenSource.CreateLinkedTokenSource(CancellationToken)"/>, which is a LIVE link
    /// (a registered callback, not a snapshot), so cancelling after a launch was submitted still cancels the
    /// token threaded into the consent gate.</summary>
    sealed class CancellableHostLifetime : IHostApplicationLifetime {
        public readonly CancellationTokenSource Cts = new();
        public CancellationToken ApplicationStarted  => CancellationToken.None;
        public CancellationToken ApplicationStopping => Cts.Token;
        public CancellationToken ApplicationStopped  => CancellationToken.None;
        public void StopApplication() => Cts.Cancel();
    }

    /// <summary>A pty double backed by a real, test-owned child process whose TerminateAsync actually kills
    /// it — the opposite of LivePtyDouble, which deliberately survives teardown. Needed to prove "every
    /// registered child is physically gone" after a real shutdown.</summary>
    sealed class KillingPtyDouble(DummyProcess child) : IPtyProcess {
        public int  Pid       => child.Pid;
        public bool HasExited => child.HasExited;
        public int? ExitCode  => child.HasExited ? 0 : null;
        public ValueTask DisposeAsync() => default;
        public Task WaitForExitAsync(TimeSpan? timeout) { child.WaitForExit(timeout ?? TimeSpan.FromSeconds(5)); return Task.CompletedTask; }
        public Task TerminateAsync(TimeSpan?   timeout) { child.Kill(); child.WaitForExit(timeout ?? TimeSpan.FromSeconds(5)); return Task.CompletedTask; }
#pragma warning disable CS1998
        public async IAsyncEnumerable<byte[]> ReadOutputAsync([System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken _ = default) { yield break; }
#pragma warning restore CS1998
        public Task WriteAsync(string _) => Task.CompletedTask;
        public Task WriteAsync(byte[] _) => Task.CompletedTask;
        public void Resize(ushort     _, ushort __) { }
        public void SendInterrupt() { }
    }

    static (LaunchConsentGate gate, ParkingPrompter prompter) PromptGateWithParkingPrompter(
            string dir, int promptTimeoutSeconds = 60) {
        var store = new LaunchConsentStore(dir, NullLogger.Instance);
        store.TryReplace(new LaunchConsentPolicy(LaunchConsentDefault.Prompt, promptTimeoutSeconds, []), out _);
        var prompter = new ParkingPrompter();
        var gate = new LaunchConsentGate(store, new LaunchConsentDecisionLog(dir, NullLogger.Instance),
            prompter, new FakeTimeProvider(), NullLogger<LaunchConsentGate>.Instance);
        return (gate, prompter);
    }

    static LaunchAgentCommand SequencedLaunch(string agentId, string epoch, long seq, string vendor = "bogus-vendor") =>
        new(AgentId: agentId, Prompt: "hi", Model: "opus", Effort: null,
            RepoPath: "/tmp/does-not-matter", Tools: null, AttachmentIds: null, Vendor: vendor,
            Epoch: epoch, Seq: seq, CommandId: $"cmd-{seq}");

    static LaunchAgentCommand UnsequencedLaunch(string agentId, string vendor = "claude", string repoPath = "/tmp/does-not-matter") =>
        new(AgentId: agentId, Prompt: "hi", Model: "opus", Effort: null,
            RepoPath: repoPath, Tools: null, AttachmentIds: null, Vendor: vendor);

    static async Task SpinUntilAsync(Func<bool> condition, TimeSpan timeout) {
        var deadline = DateTime.UtcNow + timeout;
        while (!condition() && DateTime.UtcNow < deadline) await Task.Delay(10);
        if (!condition()) throw new TimeoutException("Condition was not met within the timeout.");
    }

    /// <summary>A stop EXECUTED means the shared executor ran: it stamps Status=Completed and the
    /// user-stop end reason. It does NOT mean the entry left the registry — for a SEEDED agent there is no
    /// read loop to drive FinalizeAgentRunAsync/CleanupAgentAsync, so removal is not the observable here
    /// (the same reason Stop_via_v2_advances_watermark asserts on the live-agents view, not on removal).</summary>
    static async Task AssertStopExecutedAsync(AgentOrchestrator orch, string agentId) {
        var agent = orch.GetAgentForTest(agentId);
        await Assert.That(agent).IsNotNull();
        await Assert.That(agent!.Status).IsEqualTo("Completed");
        await Assert.That(agent.PendingEndReason).IsEqualTo("agent_stopped");
        await Assert.That(orch.BuildLiveAgents().Select(a => a.Id)).DoesNotContain(agentId);
    }

    static async Task WaitBoundedAsync(Task task, string because) {
        var finished = await Task.WhenAny(task, Task.Delay(Bounded));
        await Assert.That(finished == task).IsTrue().Because(because);
        await task;
    }

    // ══ Pump + lane contract ════════════════════════════════════════════════════════════════════════

    // The core §3.3 liveness claim: with a SEQUENCED launch parked on a consent prompt, the pump keeps
    // dispatching. A later sequenced stop's ACCEPTANCE is answered immediately while its EXECUTION queues,
    // and a status-report request (processor-independent) completes freely.
    [Test]
    public async Task Parked_launch_does_not_block_stop_acceptance_or_a_status_report_request() {
        var dir = Directory.CreateTempSubdirectory("kcap-domain-pump-").FullName;
        var (gate, prompter) = PromptGateWithParkingPrompter(dir);
        var server = new SeqCaptureServerConnection();
        await using var orch = BuildOrchestrator(server, new SpyPtyProcessFactory(),
            new Dictionary<string, IHostedAgentLauncher>(), consentGate: gate);
        var epoch = orch.DaemonEpochForTest;

        var launchTask = orch.SubmitLaunchAgentForTest(SequencedLaunch("parked", epoch, 1));
        await WaitBoundedAsync(launchTask, "HandleLaunchAgent must not await the sequenced launch's execution");
        await prompter.WaitForPromptAsync("parked"); // dequeued and genuinely parked at the gate

        orch.SeedAgentForTest("other", status: "Running");
        // NOT awaited: SubmitAsync is not itself `async` — its whole body, including the lock-protected
        // accept decision, runs synchronously before it returns a Task — so by the time this line returns,
        // Seq 2 is already accepted even though its execution is still queued behind the parked launch.
        var stopTask = orch.SubmitStopAgentV2ForTest(new StopAgentV2("other", epoch, 2, "cmd-2"));
        await Assert.That(orch.BuildStatusReport().HighestAcceptedSeq).IsEqualTo(2L);
        await Assert.That(server.Rejects).IsEmpty();
        await Assert.That(orch.BuildStatusReport().LastProcessedSeq).IsEqualTo(0L); // execution has NOT run

        // Processor-independent handlers dispatch freely while the lane is parked.
        await WaitBoundedAsync(orch.SendDaemonStatusReportOnceAsync(), "a status-report request stalled behind the parked launch");

        await prompter.ResolveUntilAsync(null, () => orch.BuildStatusReport().LastProcessedSeq >= 2L);
        await WaitBoundedAsync(stopTask, "the sequenced stop handler never returned");
        Directory.Delete(dir, true);
    }

    // Acceptance ordering still depends only on pump serialization: nothing awaits before SubmitAsync, so
    // back-to-back wire-order seqs are accepted in order with no WrongNext.
    [Test]
    public async Task Back_to_back_sequenced_launches_are_accepted_in_wire_order_with_no_rejection() {
        var dir = Directory.CreateTempSubdirectory("kcap-domain-order-").FullName;
        var (gate, prompter) = PromptGateWithParkingPrompter(dir);
        var server = new SeqCaptureServerConnection();
        await using var orch = BuildOrchestrator(server, new SpyPtyProcessFactory(),
            new Dictionary<string, IHostedAgentLauncher>(), consentGate: gate);
        var epoch = orch.DaemonEpochForTest;

        await orch.SubmitLaunchAgentForTest(SequencedLaunch("a1", epoch, 1));
        await orch.SubmitLaunchAgentForTest(SequencedLaunch("a2", epoch, 2));

        await Assert.That(orch.BuildStatusReport().HighestAcceptedSeq).IsEqualTo(2L);
        await Assert.That(server.Rejects).IsEmpty(); // no WrongNext — each was next when it arrived

        await prompter.ResolveUntilAsync(null, () => orch.BuildStatusReport().LastProcessedSeq == 2L);
        Directory.Delete(dir, true);
    }

    // Exactly one terminal answer per accepted item — outcome (a) success.
    [Test]
    public async Task Settlement_success_is_accepted_before_terminal_with_exactly_one_ack() {
        var (repoPath, cleanup) = CreateGitRepo();

        try {
            var server     = new SeqCaptureServerConnection();
            var ptyFactory = new SpyPtyProcessFactory();
            var claudeSpy  = new SpyHostedAgentLauncher("claude", cliPath: "spy-claude");
            var launchers  = new Dictionary<string, IHostedAgentLauncher> { ["claude"] = claudeSpy };
            await using var orch = BuildOrchestrator(server, ptyFactory, launchers, allowedRepoPath: repoPath);
            var epoch = orch.DaemonEpochForTest;

            await orch.SubmitLaunchAgentForTest(new LaunchAgentCommand(
                AgentId: "succ", Prompt: "hi", Model: "opus", Effort: null,
                RepoPath: repoPath, Tools: null, AttachmentIds: null, Vendor: "claude",
                Epoch: epoch, Seq: 1, CommandId: "cmd-1"));

            await SpinUntilAsync(() => server.Acks.Count > 0, Bounded);

            await Assert.That(server.Rejects).IsEmpty();
            await Assert.That(server.Acks).HasCount().EqualTo(1); // exactly one terminal answer
            await Assert.That(server.Acks[0].State).IsEqualTo(CommandAckState.Processed);
            await Assert.That(server.Acks[0].OutcomeKind).IsEqualTo(CommandOutcomeKind.LaunchExecuted);
            await Assert.That(server.Acks[0].Seq).IsEqualTo(1L);
        } finally {
            cleanup();
        }
    }

    // Outcome (b) consent denial.
    [Test]
    public async Task Settlement_consent_denial_is_accepted_before_terminal_with_exactly_one_rejection() {
        var dir       = Directory.CreateTempSubdirectory("kcap-domain-deny-").FullName;
        var server    = new SeqCaptureServerConnection();
        var claudeSpy = new SpyHostedAgentLauncher("claude", cliPath: "spy-claude");
        var launchers = new Dictionary<string, IHostedAgentLauncher> { ["claude"] = claudeSpy };
        await using var orch = BuildOrchestrator(server, new SpyPtyProcessFactory(), launchers,
            consentGate: DenyDefaultGate(dir));
        var epoch = orch.DaemonEpochForTest;

        await orch.SubmitLaunchAgentForTest(new LaunchAgentCommand(
            AgentId: "deny", Prompt: "hi", Model: "opus", Effort: null,
            RepoPath: "/tmp/does-not-matter", Tools: null, AttachmentIds: null, Vendor: "claude",
            Epoch: epoch, Seq: 1, CommandId: "cmd-1"));

        await SpinUntilAsync(() => server.Acks.Count > 0, Bounded);

        await Assert.That(server.Rejects).HasCount().EqualTo(1);           // exactly one terminal answer
        await Assert.That(server.Rejects[0].Reason).IsEqualTo(CommandRejectedReason.Semantic);
        await Assert.That(server.Acks).HasCount().EqualTo(1);
        await Assert.That(server.Acks[0].OutcomeKind).IsEqualTo(CommandOutcomeKind.LaunchRejected);
        await Assert.That(server.Acks[0].RejectionReason).IsEqualTo("semantic");
        await Assert.That(server.LaunchFaileds).HasCount().EqualTo(1);     // legacy LaunchFailed lane, unaffected

        // Denied before any worktree/PTY side effects — the vendor path never runs.
        await Assert.That(claudeSpy.PrepareCalls).IsEqualTo(0);
    }

    // Outcome (c) lane failure: the launch token cancelled while parked IS daemon shutdown (§1.11a), and the
    // gate's OperationCanceledException must settle as InternalError + one terminal CommandRejected — never
    // a hang and never a double answer.
    [Test]
    public async Task Settlement_gate_cancellation_settles_as_lane_failure_with_no_double_answer() {
        var dir = Directory.CreateTempSubdirectory("kcap-domain-cancel-").FullName;
        var store = new LaunchConsentStore(dir, NullLogger.Instance);
        store.TryReplace(new LaunchConsentPolicy(LaunchConsentDefault.Prompt, 60, []), out _);
        var gate = new LaunchConsentGate(store, new LaunchConsentDecisionLog(dir, NullLogger.Instance),
            new CancelingPrompter(), new FakeTimeProvider(), NullLogger<LaunchConsentGate>.Instance);

        var lifetime = new CancellableHostLifetime();
        var server   = new SeqCaptureServerConnection();
        await using var orch = BuildOrchestrator(server, new SpyPtyProcessFactory(),
            new Dictionary<string, IHostedAgentLauncher>(), consentGate: gate, lifetime: lifetime);
        var epoch = orch.DaemonEpochForTest;

        await orch.SubmitLaunchAgentForTest(SequencedLaunch("cancel-me", epoch, 1));
        lifetime.Cts.Cancel();

        await SpinUntilAsync(() => server.Acks.Count > 0, Bounded);

        await Assert.That(server.Acks).HasCount().EqualTo(1);  // exactly one terminal answer — no double answer
        await Assert.That(server.Acks[0].State).IsEqualTo(CommandAckState.Processed);
        await Assert.That(server.Acks[0].OutcomeKind).IsEqualTo(CommandOutcomeKind.InternalError);
        await Assert.That(server.Rejects).HasCount().EqualTo(1);
        await Assert.That(server.Rejects[0].Reason).IsEqualTo(CommandRejectedReason.InternalError);
    }

    // ══ One-domain ordering (handler level) ═════════════════════════════════════════════════════════

    // The un-seq'd shape EVERY ordinary dashboard/hosted-agent/PR-review launch has must be accepted, not
    // refused for its format, and must be ordered against a stop that arrives after it.
    [Test]
    public async Task An_unsequenced_stop_after_an_unsequenced_launch_executes_after_it_through_the_handlers() {
        var dir = Directory.CreateTempSubdirectory("kcap-domain-unseq-").FullName;
        var (gate, prompter) = PromptGateWithParkingPrompter(dir);
        var server = new SeqCaptureServerConnection();
        var logger = new CapturingLogger<AgentOrchestrator>();
        await using var orch = BuildOrchestrator(server, new SpyPtyProcessFactory(),
            new Dictionary<string, IHostedAgentLauncher>(), consentGate: gate, logger: logger);

        // An un-seq'd launch, committed to the lane and parked at the gate.
        await orch.SubmitLaunchAgentForTest(UnsequencedLaunch("parked"));
        await prompter.WaitForPromptAsync("parked");

        // An un-seq'd stop for a live agent, submitted while the launch is parked: admitted, queued, and
        // provably not yet executed.
        orch.SeedAgentForTest("victim", status: "Running");
        await orch.SubmitServerStopAgentForTest("victim");
        await Assert.That(orch.GetAgentForTest("victim")!.Status).IsEqualTo("Running");
        await Assert.That(orch.ProcessorForTest!.QueuedStopDepth).IsEqualTo(1);

        prompter.Resolve("parked", false);
        await WaitBoundedAsync(orch.DrainLaneForTest(), "the lane never drained the launch and its queued stop");
        await AssertStopExecutedAsync(orch, "victim");
        await Assert.That(orch.ProcessorForTest!.QueuedStopDepth).IsEqualTo(0);
        Directory.Delete(dir, true);
    }

    // The internal-reaping BYPASS: heartbeat reviewer-TTL/idle reaping and local-socket stops call the
    // shared executor directly. Routing them through the lane would let a parked consent prompt delay
    // reviewer reaping — the exact inversion of what the reaper exists for.
    [Test]
    public async Task Internal_reaping_still_stops_a_live_agent_while_the_lane_is_parked() {
        var dir = Directory.CreateTempSubdirectory("kcap-domain-bypass-").FullName;
        var (gate, prompter) = PromptGateWithParkingPrompter(dir);
        var server = new SeqCaptureServerConnection();
        await using var orch = BuildOrchestrator(server, new SpyPtyProcessFactory(),
            new Dictionary<string, IHostedAgentLauncher>(), consentGate: gate);

        await orch.SubmitLaunchAgentForTest(UnsequencedLaunch("parked"));
        await prompter.WaitForPromptAsync("parked");

        orch.SeedAgentForTest("reviewer", LaunchKind.ReviewFlow, status: "Running");
        // The internal path, unchanged: bypasses the lane entirely and completes while it is parked.
        await WaitBoundedAsync(orch.HandleStopAgentForTest("reviewer"),
            "internal reaping was routed through the parked execution lane");
        await AssertStopExecutedAsync(orch, "reviewer");

        prompter.Resolve("parked", false);
        await WaitBoundedAsync(orch.DrainLaneForTest(), "the lane never drained");
        Directory.Delete(dir, true);
    }

    // The existence pin behind that bypass: a consent-parked launch has created NO registry entry, so the
    // internal paths (which select their targets by enumerating the registry) cannot target it at all.
    [Test]
    public async Task A_consent_parked_launch_has_no_registry_entry_so_internal_paths_cannot_target_it() {
        var dir = Directory.CreateTempSubdirectory("kcap-domain-exists-").FullName;
        var (gate, prompter) = PromptGateWithParkingPrompter(dir);
        var server = new SeqCaptureServerConnection();
        await using var orch = BuildOrchestrator(server, new SpyPtyProcessFactory(),
            new Dictionary<string, IHostedAgentLauncher>(), consentGate: gate);

        await orch.SubmitLaunchAgentForTest(UnsequencedLaunch("parked"));
        await prompter.WaitForPromptAsync("parked");

        await Assert.That(orch.GetAgentForTest("parked")).IsNull();
        await Assert.That(orch.BuildLiveAgents().Select(a => a.Id)).DoesNotContain("parked");
        await Assert.That(orch.FindReviewersToReap().Select(r => r.Id)).DoesNotContain("parked");
        // ...and yet the lane's own active-instance set DOES admit a stop for it (the active-set pin).
        await Assert.That(orch.ProcessorForTest!.IsActiveLaunchTargetForTest("parked")).IsTrue();

        prompter.Resolve("parked", false);
        await WaitBoundedAsync(orch.DrainLaneForTest(), "the lane never drained");
        Directory.Delete(dir, true);
    }

    // Pre-settlement regression pin: with NO processor published, an un-seq'd launch executes INLINE and the
    // handler returns only after the core completes. The inline await IS the backpressure for that
    // population; nothing about §3.3 may change it.
    [Test]
    public async Task With_no_processor_an_unsequenced_launch_executes_inline_before_the_handler_returns() {
        var (repoPath, cleanup) = CreateGitRepo();

        try {
            var server     = new SeqCaptureServerConnection();
            var ptyFactory = new SpyPtyProcessFactory();
            var claudeSpy  = new SpyHostedAgentLauncher("claude", cliPath: "spy-claude");
            var launchers  = new Dictionary<string, IHostedAgentLauncher> { ["claude"] = claudeSpy };
            await using var orch = BuildOrchestrator(server, ptyFactory, launchers, allowedRepoPath: repoPath,
                deferProcessorPublication: true);
            await Assert.That(orch.ProcessorForTest).IsNull();

            // SubmitLaunchAgentForTest deliberately does NOT wait for the lane, so anything observable here
            // was decided on this call's own stack.
            await orch.SubmitLaunchAgentForTest(UnsequencedLaunch("legacy-inline", repoPath: repoPath));

            // Deliberately NOT asserting on GetAgentForTest/LaunchFaileds: the stub PTY's ReadOutputAsync
            // yields no bytes, so the background read-loop's own (pre-existing) startup-failure heuristic
            // races to mark the launch failed. SpawnCalls/PrepareCalls/BuildArgsCalls are all decided
            // synchronously inside the inline-awaited core, before that task is even scheduled.
            await Assert.That(ptyFactory.SpawnCalls).IsEqualTo(1);
            await Assert.That(claudeSpy.PrepareCalls).IsEqualTo(1);
            await Assert.That(claudeSpy.BuildArgsCalls).IsEqualTo(1);
        } finally {
            cleanup();
        }
    }

    // ══ Transition barrier ═════════════════════════════════════════════════════════════════════════

    // The transition-lock pin: a handler that snapshotted a NULL processor reserved the inline slot in the
    // same critical section publication uses, so once the processor publishes, the lane's FIRST item cannot
    // overlap that still-running inline work.
    [Test]
    public async Task A_handler_that_saw_a_null_processor_cannot_overlap_the_lanes_first_item() {
        var dir = Directory.CreateTempSubdirectory("kcap-domain-barrier-").FullName;
        var (gate, prompter) = PromptGateWithParkingPrompter(dir);
        var server = new SeqCaptureServerConnection();
        await using var orch = BuildOrchestrator(server, new SpyPtyProcessFactory(),
            new Dictionary<string, IHostedAgentLauncher>(), consentGate: gate, deferProcessorPublication: true);

        // Inline (pre-publication) un-seq'd launch, paused inside the core on the consent prompt.
        var inline = orch.SubmitLaunchAgentForTest(UnsequencedLaunch("inline"));
        await prompter.WaitForPromptAsync("inline");
        await Assert.That(inline.IsCompleted).IsFalse();

        orch.PublishSequencedProcessorForTest();
        await Assert.That(orch.ProcessorForTest).IsNotNull();

        // A lane item now exists, but the lane must not start it while the inline slot is reserved.
        orch.SeedAgentForTest("victim", status: "Running");
        await orch.SubmitServerStopAgentForTest("victim");
        await Task.Delay(150);
        await Assert.That(orch.GetAgentForTest("victim")!.Status).IsEqualTo("Running")
            .Because("the lane executed its first item while a reserved inline item was still running");

        // Releasing the inline item releases the barrier, and only then does the lane run.
        prompter.Resolve("inline", false);
        await WaitBoundedAsync(inline, "the inline launch never completed");
        await WaitBoundedAsync(orch.DrainLaneForTest(), "the lane never started after the inline drain");
        await AssertStopExecutedAsync(orch, "victim");
        Directory.Delete(dir, true);
    }

    // ══ Stop admission (handler level) ═════════════════════════════════════════════════════════════

    // What admission actually considers a target: the registry, plus the durable PID record — because a
    // record-backed stop is the registry-independent physical stop that reaps a prior incarnation's
    // survivor, which is NOT the no-op that justifies dropping unknown ids.
    [Test]
    public async Task Unknown_stop_targets_are_dropped_but_registry_and_pid_record_targets_are_admitted() {
        await using var orch = BuildOrchestrator(new SeqCaptureServerConnection(), new SpyPtyProcessFactory(),
            new Dictionary<string, IHostedAgentLauncher>());

        await Assert.That(orch.IsKnownStopTargetForTest("nobody")).IsFalse();
        await Assert.That(orch.IsKnownStopTargetForTest("")).IsFalse();

        orch.SeedAgentForTest("registered", status: "Running");
        await Assert.That(orch.IsKnownStopTargetForTest("registered")).IsTrue();

        orch.WritePidRecordForTest(new AgentPidRecord(
            "survivor", 999_999, "identity", PidIdentityKind.Present, "ReviewFlow", "codex", "f1", "reviewer",
            orch.DaemonIdForTest, "old-epoch", DateTimeOffset.UtcNow));
        await Assert.That(orch.IsKnownStopTargetForTest("survivor")).IsTrue();

        // An unknown-target stop through the real handler is dropped at admission: nothing queues, and the
        // handler still returns cleanly (there is no reply surface to fail).
        await WaitBoundedAsync(orch.HandleServerStopAgentForTest("nobody"), "an unknown-target stop hung the handler");
        await Assert.That(orch.ProcessorForTest!.QueuedStopDepth).IsEqualTo(0);
    }

    // ══ Handler classification ═════════════════════════════════════════════════════════════════════

    // What unparking means for every OTHER handler. Agent-addressed commands are sent only for registered
    // agents, and the daemon drops-and-logs an unknown id — already today's post-exit behavior — so
    // dispatching them while a launch executes is benign. A status report is served from the registry
    // snapshot, where an in-flight launch is simply absent.
    [Test]
    public async Task With_a_launch_parked_input_and_resize_for_an_unknown_id_drop_and_the_status_report_omits_it() {
        var dir = Directory.CreateTempSubdirectory("kcap-domain-classify-").FullName;
        var (gate, prompter) = PromptGateWithParkingPrompter(dir);
        var server = new SeqCaptureServerConnection();
        var logger = new CapturingLogger<AgentOrchestrator>();
        await using var orch = BuildOrchestrator(server, new SpyPtyProcessFactory(),
            new Dictionary<string, IHostedAgentLauncher>(), consentGate: gate, logger: logger);

        await orch.SubmitLaunchAgentForTest(UnsequencedLaunch("in-flight"));
        await prompter.WaitForPromptAsync("in-flight");

        // Input for the in-flight (unregistered) launch: dropped and logged, no throw, no stall.
        await WaitBoundedAsync(orch.HandleSendInputForTest(new SendInputCommand("in-flight", "hello", null)),
            "SendInput for an unknown agent stalled instead of dropping");
        await Assert.That(logger.Entries.Any(e =>
            e.Level == LogLevel.Warning && e.Message.Contains("in-flight") && e.Message.Contains("SendInput dropped"))).IsTrue();

        // Resize for the same unknown id: a no-op that must not throw.
        orch.HandleResizeTerminalForTest(new ResizeTerminalCommand("in-flight", 100, 40));

        // The status report is a registry snapshot — the in-flight launch is absent, by design, and the
        // server's consumers never infer absence from omission.
        var report = orch.BuildStatusReport();
        await Assert.That(report.LiveAgents.Select(a => a.Id)).DoesNotContain("in-flight");
        await Assert.That(report.Quarantined.Select(a => a.Id)).DoesNotContain("in-flight");
        await WaitBoundedAsync(orch.SendDaemonStatusReportOnceAsync(), "a status-report request stalled behind the parked launch");

        prompter.Resolve("in-flight", false);
        await WaitBoundedAsync(orch.DrainLaneForTest(), "the lane never drained");
        Directory.Delete(dir, true);
    }

    // ══ Shutdown: teardown-reap + next-boot handoff ════════════════════════════════════════════════

    // Layer (a) of the shutdown supersession proof: real shutdown with live children AND queued un-seq'd
    // stops discards the queue (the stops never execute) AND leaves every registered child physically gone.
    // Discarding is only safe because teardown reaps the children, so both halves are asserted together.
    [Test]
    public async Task Shutdown_with_live_children_and_queued_stops_discards_the_queue_and_kills_every_child() {
        var dir = Directory.CreateTempSubdirectory("kcap-domain-teardown-").FullName;
        var (gate, prompter) = PromptGateWithParkingPrompter(dir);
        var server = new SeqCaptureServerConnection();
        var logger = new CapturingLogger<AgentOrchestrator>();
        var orch   = BuildOrchestrator(server, new SpyPtyProcessFactory(),
            new Dictionary<string, IHostedAgentLauncher>(), consentGate: gate, logger: logger);

        using var childA = DummyProcess.StartSleep(60);
        using var childB = DummyProcess.StartSleep(60);
        orch.SeedAgentForTest("child-a", status: "Running", pty: new KillingPtyDouble(childA));
        orch.SeedAgentForTest("child-b", status: "Running", pty: new KillingPtyDouble(childB));

        // Park the lane so the stops below cannot drain before shutdown.
        await orch.SubmitLaunchAgentForTest(UnsequencedLaunch("parked"));
        await prompter.WaitForPromptAsync("parked");

        await orch.SubmitServerStopAgentForTest("child-a");
        await orch.SubmitServerStopAgentForTest("child-b");
        await Assert.That(orch.ProcessorForTest!.QueuedStopDepth).IsEqualTo(2);

        // Real shutdown. Cancelling the shutdown token releases the parked prompt, and closing the lane to
        // new work happens BEFORE agent teardown, so the queued stops are discarded rather than raced.
        await WaitBoundedAsync(orch.DisposeAsync().AsTask(), "DisposeAsync hung on the parked lane");

        childA.WaitForExit(TimeSpan.FromSeconds(10));
        childB.WaitForExit(TimeSpan.FromSeconds(10));
        await Assert.That(childA.HasExited).IsTrue();
        await Assert.That(childB.HasExited).IsTrue();

        // The queue was discarded: neither stop ever entered the stop executor (which logs "Stopping agent"
        // as its first act), and the counter returned to zero.
        await Assert.That(logger.Entries.Any(e => e.Message.Contains("Stopping agent child-a"))).IsFalse();
        await Assert.That(logger.Entries.Any(e => e.Message.Contains("Stopping agent child-b"))).IsFalse();
        await Assert.That(orch.ProcessorForTest!.QueuedStopDepth).IsEqualTo(0);
        Directory.Delete(dir, true);
    }

    // Layer (b): the handoff. A child that starts after the teardown snapshot survives shutdown WITH its
    // durable identity record, and the NEXT boot's scan reaps exactly that child — never a PID-reused
    // unrelated process.
    //
    // NOTE ON SCOPE: the unit harness cannot restart a real daemon process, so "the next boot's scan" is
    // driven at its true seam — a fresh OrphanReaper over the SAME record root with a NEW epoch, which is
    // exactly what DaemonRunner constructs at boot. What is therefore NOT covered end-to-end here is the
    // process-restart plumbing itself (that the next boot rebuilds the record root from the same state dir
    // and runs this scan); the record-root wiring is asserted below via the orchestrator's own store, and
    // the reaper's identity/epoch rules have their own coverage in OrphanReaperTests (including the
    // forced-pid-reuse case).
    [Test]
    public async Task A_child_surviving_shutdown_is_reaped_by_the_next_boots_scan_and_a_pid_reused_process_is_not() {
        var server = new SeqCaptureServerConnection();
        var orch   = BuildOrchestrator(server, new SpyPtyProcessFactory(),
            new Dictionary<string, IHostedAgentLauncher>());

        // A child started AFTER the teardown snapshot: it exists and has a durable record, but the
        // orchestrator's registry never knew about it, so shutdown cannot terminate it.
        using var survivor = DummyProcess.StartSleep(
            60, new Dictionary<string, string> { ["KCAP_AGENT_ID"] = "late-child" });
        var survivorIdentity = ProcessIdentity.Capture(survivor.Pid);
        await Assert.That(survivorIdentity).IsNotNull();

        // A second live process with a record whose start identity is DELIBERATELY wrong — the shape of a
        // pid-reused unrelated process. It must be left alone.
        using var stranger = DummyProcess.StartSleep(60);

        var daemonId = orch.DaemonIdForTest;
        var oldEpoch = orch.DaemonEpochForTest;
        orch.WritePidRecordForTest(new AgentPidRecord(
            "late-child", survivor.Pid, survivorIdentity!, PidIdentityKind.Present, "ReviewFlow", "codex",
            "f1", "reviewer", daemonId, oldEpoch, DateTimeOffset.UtcNow));
        orch.WritePidRecordForTest(new AgentPidRecord(
            "stranger", stranger.Pid, survivorIdentity! + "-not-this-process", PidIdentityKind.Present,
            "ReviewFlow", "codex", "f1", "reviewer", daemonId, oldEpoch, DateTimeOffset.UtcNow));

        var recordRoot = orch.PidRecordRootForTest;
        await WaitBoundedAsync(orch.DisposeAsync().AsTask(), "DisposeAsync hung");

        // Shutdown left the late child alive with its record intact — the explicit residual.
        await Assert.That(survivor.HasExited).IsFalse();
        var store = new AgentPidRecordStore(recordRoot, NullLogger.Instance);
        await Assert.That(store.ReadAll().Select(r => r.AgentId)).Contains("late-child");

        // The NEXT boot: same daemon id + record root, a FRESH epoch. Two passes, because on macOS the
        // first kill may observe a not-yet-parent-reaped zombie as alive and defer record deletion.
        var nextBoot = new OrphanReaper(store, daemonId, currentEpoch: "next-boot-epoch", NullLogger.Instance);
        await nextBoot.ReapOnceAsync();
        survivor.WaitForExit(TimeSpan.FromSeconds(10));
        await nextBoot.ReapOnceAsync();

        await Assert.That(survivor.HasExited).IsTrue()
            .Because("the next boot's scan must reap a shutdown survivor by its durable start identity");
        await Assert.That(stranger.HasExited).IsFalse()
            .Because("a pid whose start identity does not match its record is an unrelated process");
        await Assert.That(store.ReadAll().Select(r => r.AgentId)).DoesNotContain("late-child");
    }
}
