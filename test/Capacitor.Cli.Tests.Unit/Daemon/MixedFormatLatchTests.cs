using System.Collections.Concurrent;
using Capacitor.Cli.Core;
using Capacitor.Cli.Daemon.Services;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;

namespace Capacitor.Cli.Tests.Unit;

// Spec §3.3 (unpark the receive loop for sequenced launches behind a daemon-lifetime format latch).
// Reuses the AgentOrchestratorVendorTests harness (BuildOrchestrator/SpyPtyProcessFactory/
// SpyHostedAgentLauncher/SeqCaptureServerConnection/CapturingLogger/DenyDefaultGate/CreateGitRepo) —
// see HealBarrierReportTests.cs for the precedent of a second partial-class file doing the same.
public partial class AgentOrchestratorVendorTests {
    /// <summary>Parks every <see cref="PromptAsync"/> call on a per-request (keyed by
    /// <see cref="LaunchConsentPromptRequest.RequestId"/>, which is the agent id) TaskCompletionSource
    /// until the test resolves it via <see cref="ResolveUntilAsync"/>, or until the caller's <c>ct</c> fires —
    /// treated GRACEFULLY as "no answer" (a plain timeout-shaped null), not a thrown
    /// OperationCanceledException, since these tests are not exercising cancellation semantics (that is
    /// <see cref="CancelingPrompter"/>'s job). <see cref="HasSubscriber"/>/<see cref="WaitForSubscriberAsync"/>
    /// always succeed immediately so <c>DecideAsync</c> reaches <see cref="PromptAsync"/> without any real
    /// wait — a <see cref="FakeTimeProvider"/> is still passed to the gate so no real clock is ever touched.</summary>
    sealed class ParkingPrompter : ILaunchConsentPrompter {
        readonly ConcurrentDictionary<string, TaskCompletionSource<bool?>> _pending = new();
        public bool HasSubscriber => true;
        public Task<bool> WaitForSubscriberAsync(TimeSpan wait, TimeProvider time, CancellationToken ct) =>
            Task.FromResult(true);

        public Task<bool?> PromptAsync(LaunchConsentPromptRequest req, TimeSpan timeout, TimeProvider time, CancellationToken ct) {
            var tcs = _pending.GetOrAdd(req.RequestId,
                _ => new TaskCompletionSource<bool?>(TaskCreationOptions.RunContinuationsAsynchronously));
            ct.Register(() => tcs.TrySetResult(null));
            return tcs.Task;
        }

        /// <summary>Repeatedly resolves whatever prompt(s) are CURRENTLY pending with <paramref name="answer"/>,
        /// retrying until <paramref name="done"/> reports success or <paramref name="timeout"/> elapses. A
        /// single one-shot resolve is not enough for two reasons: (1) the processor's serial lane means a
        /// LATER submitted item's own PromptAsync is not even called — so its TCS does not yet exist in
        /// <c>_pending</c> — until an EARLIER item has fully settled and the lane moves on to it; (2) the
        /// detached execution runs on a background <c>Task.Run</c> thread that may simply not have been
        /// scheduled yet by the time the test calls this. Both resolve into "keep retrying" rather than a
        /// fixed one-shot call.</summary>
        public async Task ResolveUntilAsync(bool? answer, Func<bool> done, TimeSpan timeout) {
            var deadline = DateTime.UtcNow + timeout;
            while (true) {
                foreach (var tcs in _pending.Values) tcs.TrySetResult(answer);
                if (done()) return;
                if (DateTime.UtcNow >= deadline)
                    throw new TimeoutException("ParkingPrompter.ResolveUntilAsync: condition not met within the timeout.");
                await Task.Delay(10);
            }
        }
    }

    /// <summary>Mirrors the REAL LaunchConsentBroker's documented external-cancellation behavior
    /// (Task 4: "an external ct cancellation always rethrows") closely enough to pin the SEQUENCED
    /// LANE's classification of that OperationCanceledException, without depending on the real
    /// broker/IPC machinery (already covered by LaunchConsentBrokerTests/LaunchConsentGateTests).
    /// <see cref="CancellationToken.Register(Action)"/> fires synchronously/immediately for an
    /// ALREADY-cancelled token, so this is deterministic regardless of whether <c>ct</c> fires before
    /// or after <see cref="PromptAsync"/> is reached.</summary>
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

    /// <summary>An <see cref="IHostApplicationLifetime"/> whose <see cref="ApplicationStopping"/> is a
    /// REAL, test-controlled token — <see cref="StubHostLifetime"/> (every other test's default) is
    /// fixed at <see cref="CancellationToken.None"/> and can never fire. AgentOrchestrator links its
    /// internal <c>_shutdownCts</c> to this token AT CONSTRUCTION time via
    /// <see cref="CancellationTokenSource.CreateLinkedTokenSource(CancellationToken)"/>, which is a LIVE
    /// link (a registered callback, not a snapshot) — so cancelling <see cref="Cts"/> after construction,
    /// even well after a launch has already been submitted, still correctly cancels the token threaded
    /// into <c>LaunchConsentGate.DecideAsync</c>.</summary>
    sealed class CancellableHostLifetime : IHostApplicationLifetime {
        public readonly CancellationTokenSource Cts = new();
        public CancellationToken ApplicationStarted  => CancellationToken.None;
        public CancellationToken ApplicationStopping => Cts.Token;
        public CancellationToken ApplicationStopped  => CancellationToken.None;
        public void StopApplication() => Cts.Cancel();
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

    static LaunchAgentCommand LegacyLaunch(string agentId, string vendor = "claude") =>
        new(AgentId: agentId, Prompt: "hi", Model: "opus", Effort: null,
            RepoPath: "/tmp/does-not-matter", Tools: null, AttachmentIds: null, Vendor: vendor);

    static async Task SpinUntilAsync(Func<bool> condition, TimeSpan timeout) {
        var deadline = DateTime.UtcNow + timeout;
        while (!condition() && DateTime.UtcNow < deadline) await Task.Delay(10);
        if (!condition()) throw new TimeoutException("Condition was not met within the timeout.");
    }

    // ══ Scenario 1: pump liveness — a parked sequenced launch does not block a concurrent
    //    HandleStopAgentV2 (acceptance) for another agent, nor a status-report request. ══════════════

    [Test]
    public async Task Parked_launch_does_not_block_stop_acceptance_or_a_status_report_request() {
        var dir = Directory.CreateTempSubdirectory("kcap-latch-pump-").FullName;
        var (gate, prompter) = PromptGateWithParkingPrompter(dir);
        var server = new SeqCaptureServerConnection();
        await using var orch = BuildOrchestrator(server, new SpyPtyProcessFactory(),
            new Dictionary<string, IHostedAgentLauncher>(), consentGate: gate);
        var epoch = orch.DaemonEpochForTest;

        var launchTask = orch.HandleLaunchAgentForTest(SequencedLaunch("parked", epoch, 1));

        // The whole point of §3.3: HandleLaunchAgent returns once the item is SUBMITTED, not once
        // it's executed. Bounded so a regression (re-adding the await) fails fast instead of hanging
        // the whole suite.
        var returnedPromptly = await Task.WhenAny(launchTask, Task.Delay(TimeSpan.FromSeconds(30))) == launchTask;
        await Assert.That(returnedPromptly).IsTrue()
            .Because("HandleLaunchAgent must not await the sequenced launch's execution");
        await launchTask; // already completed — this only surfaces an exception, if any

        orch.SeedAgentForTest("other", status: "Running");
        // NOT awaited yet: SequencedCommandProcessor.SubmitAsync is not itself `async` — its entire
        // body, including the lock-protected accept decision, runs synchronously before it returns a
        // Task — so by the time THIS line returns a Task handle, Seq 2 is already accepted, even
        // though the stop's own execution is still queued (the documented §3.3 residual) behind the
        // still-parked launch on the same serial lane.
        var stopTask = orch.HandleStopAgentV2ForTest(new StopAgentV2("other", epoch, 2, "cmd-2"));
        await Assert.That(orch.BuildStatusReport().HighestAcceptedSeq).IsEqualTo(2L);
        await Assert.That(server.Rejects).IsEmpty();

        // Independent of the processor entirely — must dispatch and complete freely regardless.
        await orch.SendDaemonStatusReportOnceAsync();

        // Resolve until the STOP (Seq 2, behind the parked launch on the same serial lane) has
        // actually settled — a one-shot resolve races the still-parked launch's own TCS existing yet.
        await prompter.ResolveUntilAsync(null, () => orch.BuildStatusReport().LastProcessedSeq >= 2L, TimeSpan.FromSeconds(30));
        await stopTask;
        Directory.Delete(dir, true);
    }

    // ══ Scenario 2: acceptance ordering — two back-to-back sequenced launches submitted in wire
    //    order are accepted in order, with no non-next (WrongNext) rejection. ══════════════════════

    [Test]
    public async Task Back_to_back_sequenced_launches_are_accepted_in_wire_order_with_no_rejection() {
        var dir = Directory.CreateTempSubdirectory("kcap-latch-order-").FullName;
        var (gate, prompter) = PromptGateWithParkingPrompter(dir);
        var server = new SeqCaptureServerConnection();
        await using var orch = BuildOrchestrator(server, new SpyPtyProcessFactory(),
            new Dictionary<string, IHostedAgentLauncher>(), consentGate: gate);
        var epoch = orch.DaemonEpochForTest;

        await orch.HandleLaunchAgentForTest(SequencedLaunch("a1", epoch, 1));
        await orch.HandleLaunchAgentForTest(SequencedLaunch("a2", epoch, 2));

        await Assert.That(orch.BuildStatusReport().HighestAcceptedSeq).IsEqualTo(2L);
        await Assert.That(server.Rejects).IsEmpty(); // no WrongNext — each was next when it arrived

        // Item 2's own PromptAsync is not even called until item 1 (the serial lane's current item)
        // fully settles, so this must keep re-resolving as each item's prompt comes up in turn.
        await prompter.ResolveUntilAsync(null, () => orch.BuildStatusReport().LastProcessedSeq == 2L, TimeSpan.FromSeconds(30));
        Directory.Delete(dir, true);
    }

    // ══ Scenario 3: settlement — accepted-before-terminal + exactly one terminal answer, for each of
    //    success / consent-denial / lane-failure (carry-in (b): a gate OCE from a cancelled launch
    //    token). ═══════════════════════════════════════════════════════════════════════════════════

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

            await orch.HandleLaunchAgentForTest(new LaunchAgentCommand(
                AgentId: "succ", Prompt: "hi", Model: "opus", Effort: null,
                RepoPath: repoPath, Tools: null, AttachmentIds: null, Vendor: "claude",
                Epoch: epoch, Seq: 1, CommandId: "cmd-1"));

            await SpinUntilAsync(() => server.Acks.Count > 0, TimeSpan.FromSeconds(30));

            await Assert.That(server.Rejects).IsEmpty();
            await Assert.That(server.Acks).HasCount().EqualTo(1); // exactly one terminal answer
            await Assert.That(server.Acks[0].State).IsEqualTo(CommandAckState.Processed);
            await Assert.That(server.Acks[0].OutcomeKind).IsEqualTo(CommandOutcomeKind.LaunchExecuted);
            await Assert.That(server.Acks[0].Seq).IsEqualTo(1L);
        } finally {
            cleanup();
        }
    }

    [Test]
    public async Task Settlement_consent_denial_is_accepted_before_terminal_with_exactly_one_rejection() {
        var dir       = Directory.CreateTempSubdirectory("kcap-latch-deny-").FullName;
        var server    = new SeqCaptureServerConnection();
        var claudeSpy = new SpyHostedAgentLauncher("claude", cliPath: "spy-claude");
        var launchers = new Dictionary<string, IHostedAgentLauncher> { ["claude"] = claudeSpy };
        await using var orch = BuildOrchestrator(server, new SpyPtyProcessFactory(), launchers,
            consentGate: DenyDefaultGate(dir));
        var epoch = orch.DaemonEpochForTest;

        await orch.HandleLaunchAgentForTest(new LaunchAgentCommand(
            AgentId: "deny", Prompt: "hi", Model: "opus", Effort: null,
            RepoPath: "/tmp/does-not-matter", Tools: null, AttachmentIds: null, Vendor: "claude",
            Epoch: epoch, Seq: 1, CommandId: "cmd-1"));

        await SpinUntilAsync(() => server.Acks.Count > 0, TimeSpan.FromSeconds(30));

        await Assert.That(server.Rejects).HasCount().EqualTo(1);           // exactly one terminal answer
        await Assert.That(server.Rejects[0].Reason).IsEqualTo(CommandRejectedReason.Semantic);
        await Assert.That(server.Acks).HasCount().EqualTo(1);
        await Assert.That(server.Acks[0].OutcomeKind).IsEqualTo(CommandOutcomeKind.LaunchRejected);
        await Assert.That(server.Acks[0].RejectionReason).IsEqualTo("semantic");
        await Assert.That(server.LaunchFaileds).HasCount().EqualTo(1);     // legacy LaunchFailed lane, unaffected

        // Denied before any worktree/PTY side effects — the vendor path never runs.
        await Assert.That(claudeSpy.PrepareCalls).IsEqualTo(0);
    }

    // Carry-in (b) from the Task 4 review: the sequenced lane must convert a gate
    // OperationCanceledException (a cancelled launch token, torn down mid-prompt) into InternalError +
    // a terminal CommandRejected — the "lane failure" settlement — never a hang and never a double
    // answer.
    [Test]
    public async Task Settlement_gate_cancellation_settles_as_lane_failure_with_no_double_answer() {
        var dir = Directory.CreateTempSubdirectory("kcap-latch-cancel-").FullName;
        var store = new LaunchConsentStore(dir, NullLogger.Instance);
        store.TryReplace(new LaunchConsentPolicy(LaunchConsentDefault.Prompt, 60, []), out _);
        var prompter = new CancelingPrompter();
        var gate = new LaunchConsentGate(store, new LaunchConsentDecisionLog(dir, NullLogger.Instance),
            prompter, new FakeTimeProvider(), NullLogger<LaunchConsentGate>.Instance);

        var lifetime = new CancellableHostLifetime();
        var server   = new SeqCaptureServerConnection();
        await using var orch = BuildOrchestrator(server, new SpyPtyProcessFactory(),
            new Dictionary<string, IHostedAgentLauncher>(), consentGate: gate, lifetime: lifetime);
        var epoch = orch.DaemonEpochForTest;

        await orch.HandleLaunchAgentForTest(SequencedLaunch("cancel-me", epoch, 1));

        // Cancel the launch token while parked on the prompt (a live link — see
        // CancellableHostLifetime's remarks — so this is correct regardless of exactly when the
        // detached execution reaches the gate).
        lifetime.Cts.Cancel();

        await SpinUntilAsync(() => server.Acks.Count > 0, TimeSpan.FromSeconds(30));

        await Assert.That(server.Acks).HasCount().EqualTo(1);  // exactly one terminal answer — no double answer
        await Assert.That(server.Acks[0].State).IsEqualTo(CommandAckState.Processed);
        await Assert.That(server.Acks[0].OutcomeKind).IsEqualTo(CommandOutcomeKind.InternalError);
        await Assert.That(server.Rejects).HasCount().EqualTo(1);
        await Assert.That(server.Rejects[0].Reason).IsEqualTo(CommandRejectedReason.InternalError);
    }

    // ══ Scenario 4: latch — a REJECTED first sequenced command (a malformed partial tuple) still
    //    latches; a later un-seq'd launch is refused mixed_command_formats and never reaches Core, and
    //    a later un-seq'd stop is discarded (not executed) + logged at Error. ═══════════════════════

    [Test]
    public async Task Rejected_sequenced_launch_still_latches_blocking_a_later_legacy_launch_and_stop() {
        var server    = new SeqCaptureServerConnection();
        var claudeSpy = new SpyHostedAgentLauncher("claude", cliPath: "spy-claude");
        var launchers = new Dictionary<string, IHostedAgentLauncher> { ["claude"] = claudeSpy };
        var logger    = new CapturingLogger<AgentOrchestrator>();
        await using var orch = BuildOrchestrator(server, new SpyPtyProcessFactory(), launchers, logger: logger);
        var epoch = orch.DaemonEpochForTest;

        // A malformed PARTIAL tuple (Epoch+Seq, no CommandId) is REJECTED (fail-closed LaunchFailed) —
        // but it is still sequenced-SHAPED (anySeq is true), so it must latch regardless of its fate.
        await orch.HandleLaunchAgentForTest(new LaunchAgentCommand(
            AgentId: "partial", Prompt: "hi", Model: "opus", Effort: null,
            RepoPath: "/tmp/does-not-matter", Tools: null, AttachmentIds: null, Vendor: "claude",
            Epoch: epoch, Seq: 1, CommandId: null));

        await Assert.That(server.LaunchFaileds.Single().Reason).Contains("Malformed sequenced launch");
        await Assert.That(orch.SequencedSeenForTest).IsTrue();

        // A subsequent un-seq'd LAUNCH must be refused mixed_command_formats and never reach Core.
        await orch.HandleLaunchAgentForTest(LegacyLaunch("legacy-after"));

        await Assert.That(server.LaunchFaileds[1].AgentId).IsEqualTo("legacy-after");
        await Assert.That(server.LaunchFaileds[1].Reason).Contains("mixed_command_formats");
        await Assert.That(orch.GetAgentForTest("legacy-after")).IsNull();
        await Assert.That(claudeSpy.PrepareCalls).IsEqualTo(0); // never reached Core

        // A subsequent un-seq'd STOP must be discarded (not executed), logged at Error.
        orch.SeedAgentForTest("legacy-stop-target", status: "Running");
        await orch.HandleLegacyStopAgentForTest("legacy-stop-target");

        await Assert.That(orch.GetAgentForTest("legacy-stop-target")!.Status).IsEqualTo("Running"); // untouched
        await Assert.That(logger.Entries.Any(e =>
            e.Level == LogLevel.Error && e.Message.Contains("legacy-stop-target"))).IsTrue();
    }

    // ══ Scenario 5: the latch survives a simulated reconnect (same orchestrator instance, no handler
    //    re-wiring — exactly as production works); a FRESH orchestrator instance starts unlatched. ══

    [Test]
    public async Task Latch_survives_a_simulated_reconnect_reregistration() {
        var dir = Directory.CreateTempSubdirectory("kcap-latch-reconnect-").FullName;
        var (gate, prompter) = PromptGateWithParkingPrompter(dir);
        var server = new SeqCaptureServerConnection();
        var logger = new CapturingLogger<AgentOrchestrator>();
        await using var orch = BuildOrchestrator(server, new SpyPtyProcessFactory(),
            new Dictionary<string, IHostedAgentLauncher>(), consentGate: gate, logger: logger);
        var epoch = orch.DaemonEpochForTest;

        // A detached, still-prompting sequenced launch latches the daemon.
        await orch.HandleLaunchAgentForTest(SequencedLaunch("reconnect-parked", epoch, 1));
        await Assert.That(orch.SequencedSeenForTest).IsTrue();

        // Simulate the daemon's reconnect re-registration hook — the SAME orchestrator instance and
        // the SAME latch field; production never re-wires per-connection handlers either.
        await orch.ReRegisterAgentsForTestAsync();

        // Post-"reconnect": an un-seq'd launch is STILL refused mixed_command_formats...
        await orch.HandleLaunchAgentForTest(LegacyLaunch("post-reconnect-launch"));
        await Assert.That(server.LaunchFaileds[^1].AgentId).IsEqualTo("post-reconnect-launch");
        await Assert.That(server.LaunchFaileds[^1].Reason).Contains("mixed_command_formats");
        await Assert.That(orch.GetAgentForTest("post-reconnect-launch")).IsNull();

        // ...and an un-seq'd stop is STILL discarded, not executed.
        orch.SeedAgentForTest("post-reconnect-stop", status: "Running");
        await orch.HandleLegacyStopAgentForTest("post-reconnect-stop");
        await Assert.That(orch.GetAgentForTest("post-reconnect-stop")!.Status).IsEqualTo("Running");
        await Assert.That(logger.Entries.Any(e =>
            e.Level == LogLevel.Error && e.Message.Contains("post-reconnect-stop"))).IsTrue();

        // Let the still-parked launch settle so DisposeAsync doesn't hang draining the lane.
        await prompter.ResolveUntilAsync(null, () => orch.BuildStatusReport().LastProcessedSeq >= 1L, TimeSpan.FromSeconds(30));
        Directory.Delete(dir, true);
    }

    [Test]
    public async Task Fresh_orchestrator_instance_starts_unlatched() {
        var server = new SeqCaptureServerConnection();
        await using var orch = BuildOrchestrator(server, new SpyPtyProcessFactory(),
            new Dictionary<string, IHostedAgentLauncher>());

        await Assert.That(orch.SequencedSeenForTest).IsFalse();
    }

    // ══ Scenario 6: legacy pin — an un-seq'd launch on an unlatched daemon still executes INLINE
    //    (HandleLaunchAgent returns only after HandleLaunchAgentCore completes). ═════════════════════

    [Test]
    public async Task Legacy_launch_on_an_unlatched_daemon_still_executes_inline_before_returning() {
        var (repoPath, cleanup) = CreateGitRepo();

        try {
            var server     = new SeqCaptureServerConnection();
            var ptyFactory = new SpyPtyProcessFactory();
            var claudeSpy  = new SpyHostedAgentLauncher("claude", cliPath: "spy-claude");
            var launchers  = new Dictionary<string, IHostedAgentLauncher> { ["claude"] = claudeSpy };
            await using var orch = BuildOrchestrator(server, ptyFactory, launchers, allowedRepoPath: repoPath);

            await orch.HandleLaunchAgentForTest(new LaunchAgentCommand(
                AgentId: "legacy-inline", Prompt: "hi", Model: "opus", Effort: null,
                RepoPath: repoPath, Tools: null, AttachmentIds: null, Vendor: "claude"));

            // Returns ONLY after HandleLaunchAgentCore actually ran the launch synchronously through to
            // spawn — no detach on this lane. Deliberately NOT asserting on GetAgentForTest/LaunchFaileds
            // here: the stub PTY double's ReadOutputAsync yields no bytes, so the background read-loop's
            // own (pre-existing, unrelated to §3.3) startup-failure heuristic races to mark the launch
            // failed and clean it up — a race this test must not depend on either side of. SpawnCalls/
            // PrepareCalls/BuildArgsCalls are all decided SYNCHRONOUSLY, before that background task is
            // even scheduled, so they are the robust proof of "ran inline".
            await Assert.That(ptyFactory.SpawnCalls).IsEqualTo(1);
            await Assert.That(claudeSpy.PrepareCalls).IsEqualTo(1);
            await Assert.That(claudeSpy.BuildArgsCalls).IsEqualTo(1);
        } finally {
            cleanup();
        }
    }
}
