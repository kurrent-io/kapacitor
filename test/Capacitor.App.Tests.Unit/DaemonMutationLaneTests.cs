using Capacitor.App.Services;
using Capacitor.App.Services.Mutation;

namespace Capacitor.App.Tests.Unit;

/// Deterministic TUnit tests: every ordering assertion is driven by TaskCompletionSource gates on
/// FakeKcapCli (shared from DaemonLifecycleControllerTests.cs, same namespace), never Task.Delay.
public class DaemonMutationLaneTests {
    static readonly TimeSpan Bounded = TimeSpan.FromSeconds(5);

    static MutationRequest Req(
            MutationVerb verb = MutationVerb.StartVerified, string profile = "default",
            string server = "https://cap.example.test", string daemonName = "daemon-a") =>
        new(verb, profile, server, daemonName);

    sealed class ScriptedObservation : IDaemonObservation {
        public Func<MutationRequest, CancellationToken, Task<ObservedEvidence?>> Behavior = (_, _) => Task.FromResult<ObservedEvidence?>(null);
        public int CallCount;
        public Task<ObservedEvidence?> ObserveAsync(MutationRequest request, CancellationToken ct) {
            CallCount++;
            return Behavior(request, ct);
        }
    }

    static ObservedEvidence MatchingEvidence(int pid = 111, string instanceId = "inst-1") =>
        new(true, [], "1.0.0", "https://cap.example.test", "daemon-a", pid, instanceId, true);

    sealed class RecordingExecutorFactory {
        public readonly List<(MutationRequest Request, string? PinnedPath)> Calls = [];
        public Func<MutationRequest, string?, IKcapCli> Behavior = (_, path) => new FakeKcapCli { CliPath = path };
        public IKcapCli Invoke(MutationRequest request, string? pinnedPath) {
            Calls.Add((request, pinnedPath));
            return Behavior(request, pinnedPath);
        }
    }

    static DaemonMutationLane MakeLane(
            RecordingExecutorFactory factory, OutcomeChannel? channel = null,
            Func<string?>? cliOverride = null, ILoginShellProbe? shellProbe = null,
            Func<MutationRequest, IDaemonObservation>? oneShotFactory = null) =>
        new(
            shellProbe ?? new FakeLoginShellProbe { KcapPathBehavior = _ => Task.FromResult<string?>(null) },
            channel ?? new OutcomeChannel(),
            cliOverride ?? (() => "/opt/kcap/bin/kcap"),
            factory.Invoke,
            oneShotFactory ?? (_ => new ScriptedObservation()),
            TimeProvider.System);

    static async Task<OutcomeEnvelope> NextEnvelopeAsync(OutcomeChannel channel) {
        using var cts = new CancellationTokenSource();
        var enumerator = channel.ConsumeAsync(cts.Token).GetAsyncEnumerator();
        try {
            var got = await enumerator.MoveNextAsync().AsTask().WaitAsync(Bounded);
            if (!got) throw new InvalidOperationException("channel ended without an envelope");
            var envelope = enumerator.Current.Envelope;
            enumerator.Current.Ack();
            return envelope;
        } finally {
            await enumerator.DisposeAsync();
        }
    }

    static async Task AssertChannelEmptyAsync(OutcomeChannel channel) {
        using var cts = new CancellationTokenSource();
        var enumerator = channel.ConsumeAsync(cts.Token).GetAsyncEnumerator();
        var moveNext = enumerator.MoveNextAsync();
        await cts.CancelAsync();
        await Assert.That(async () => await moveNext.AsTask().WaitAsync(Bounded)).Throws<OperationCanceledException>();
        await enumerator.DisposeAsync();
    }

    [Test]
    public async Task Identical_concurrent_requests_coalesce_into_one_probe_and_one_mutation() {
        var request = Req();
        var gate = new TaskCompletionSource<string?>();
        var cli = new FakeKcapCli { VersionBehavior = _ => gate.Task };
        var factory = new RecordingExecutorFactory { Behavior = (_, _) => cli };
        var lane = MakeLane(factory);

        var t1 = lane.RunAsync(request, CancellationToken.None);
        var t2 = lane.RunAsync(request, CancellationToken.None);

        // Second call arrived while the first was still in flight — it must coalesce, not probe again.
        await Assert.That(factory.Calls.Count).IsEqualTo(1);
        await Assert.That(cli.StartVerifiedCallCount).IsEqualTo(0);

        gate.SetResult("9.9.9");
        var outcome1 = await t1;
        var outcome2 = await t2;

        await Assert.That(outcome1).IsEqualTo(new MutationOutcome.Succeeded());
        await Assert.That(outcome2).IsEqualTo(outcome1);
        await Assert.That(factory.Calls.Count).IsEqualTo(1);
        await Assert.That(cli.VersionCallCount).IsEqualTo(1);
        await Assert.That(cli.StartVerifiedCallCount).IsEqualTo(1);

        await lane.DisposeAsync();
    }

    [Test]
    public async Task Different_queued_request_gets_its_own_fresh_probe_only_after_the_first_reaches_a_terminal_state() {
        var requestA = Req(daemonName: "daemon-a");
        var requestB = Req(daemonName: "daemon-b");
        var gateA = new TaskCompletionSource<ProcessResult>();
        var cliA = new FakeKcapCli { StartVerifiedBehavior = _ => gateA.Task }; // gate the MUTATION, not just the probe
        var cliB = new FakeKcapCli();
        var factory = new RecordingExecutorFactory { Behavior = (req, _) => req.DaemonName == "daemon-a" ? cliA : cliB };
        var lane = MakeLane(factory);

        var t1 = lane.RunAsync(requestA, CancellationToken.None);
        var t2 = lane.RunAsync(requestB, CancellationToken.None);

        // A's own probe already succeeded (VersionAsync resolves synchronously by default) but its
        // MUTATION is still gated — B (a different, queued request) must not have probed yet.
        await Assert.That(cliA.VersionCallCount).IsEqualTo(1);
        await Assert.That(cliA.StartVerifiedCallCount).IsEqualTo(1);
        await Assert.That(factory.Calls.Count).IsEqualTo(1);
        await Assert.That(cliB.VersionCallCount).IsEqualTo(0);

        gateA.SetResult(new ProcessResult(0, "", "", false));
        var outcome1 = await t1;
        var outcome2 = await t2;

        await Assert.That(outcome1).IsEqualTo(new MutationOutcome.Succeeded());
        await Assert.That(outcome2).IsEqualTo(new MutationOutcome.Succeeded());
        await Assert.That(factory.Calls.Count).IsEqualTo(2);
        await Assert.That(factory.Calls[0].Request).IsEqualTo(requestA);
        await Assert.That(factory.Calls[1].Request).IsEqualTo(requestB);
        await Assert.That(cliB.VersionCallCount).IsEqualTo(1);

        await lane.DisposeAsync();
    }

    [Test]
    public async Task Waiter_cancellation_detaches_only_that_waiter_the_action_still_completes_for_others() {
        var request = Req();
        var gate = new TaskCompletionSource<string?>();
        var cli = new FakeKcapCli { VersionBehavior = _ => gate.Task };
        var factory = new RecordingExecutorFactory { Behavior = (_, _) => cli };
        var lane = MakeLane(factory);

        using var ctsA = new CancellationTokenSource();
        var tA = lane.RunAsync(request, ctsA.Token);
        var tB = lane.RunAsync(request, CancellationToken.None);

        await ctsA.CancelAsync();
        await Assert.ThrowsAsync<OperationCanceledException>(() => tA);

        gate.SetResult("9.9.9");
        var outcomeB = await tB;

        await Assert.That(outcomeB).IsEqualTo(new MutationOutcome.Succeeded());
        await Assert.That(cli.VersionCallCount).IsEqualTo(1);
        await Assert.That(cli.StartVerifiedCallCount).IsEqualTo(1); // uncancelled: exactly one mutation attempt

        await lane.DisposeAsync();
    }

    [Test]
    public async Task All_waiters_cancelled_action_still_reaches_a_terminal_state_and_the_outcome_reaches_the_channel() {
        var request = Req();
        var gate = new TaskCompletionSource<string?>();
        var cli = new FakeKcapCli {
            VersionBehavior = _ => gate.Task,
            StartVerifiedBehavior = _ => Task.FromResult(new ProcessResult(3, "", "boom", false)),
        };
        var factory = new RecordingExecutorFactory { Behavior = (_, _) => cli };
        var channel = new OutcomeChannel();
        var lane = MakeLane(factory, channel: channel);

        using var ctsA = new CancellationTokenSource();
        using var ctsB = new CancellationTokenSource();
        var tA = lane.RunAsync(request, ctsA.Token);
        var tB = lane.RunAsync(request, ctsB.Token);

        await ctsA.CancelAsync();
        await ctsB.CancelAsync();
        await Assert.ThrowsAsync<OperationCanceledException>(() => tA);
        await Assert.ThrowsAsync<OperationCanceledException>(() => tB);

        gate.SetResult("9.9.9"); // no waiters left — the action still must run to completion

        var envelope = await NextEnvelopeAsync(channel);
        await Assert.That(envelope.Request).IsEqualTo(request);
        await Assert.That(envelope.Outcome).IsEqualTo(new MutationOutcome.Failed(3, null, RecoverySurface.Attention));
        await Assert.That(cli.StartVerifiedCallCount).IsEqualTo(1);

        await lane.DisposeAsync();
    }

    [Test]
    public async Task Null_pin_refuses_without_ever_building_an_executor() {
        var factory = new RecordingExecutorFactory();
        var channel = new OutcomeChannel();
        var lane = MakeLane(
            factory, channel: channel, cliOverride: () => null,
            shellProbe: new FakeLoginShellProbe { KcapPathBehavior = _ => Task.FromResult<string?>(null) });

        var outcome = await lane.RunAsync(Req(), CancellationToken.None);

        await Assert.That(outcome).IsEqualTo(new MutationOutcome.Refused("cli_not_found", RecoverySurface.Attention));
        await Assert.That(factory.Calls.Count).IsEqualTo(0);

        var envelope = await NextEnvelopeAsync(channel);
        await Assert.That(envelope.Request).IsEqualTo(Req());
        await Assert.That(envelope.Outcome).IsEqualTo(outcome);

        await lane.DisposeAsync();
    }

    [Test]
    public async Task Below_floor_version_refuses_without_a_mutation_call() {
        var cli = new FakeKcapCli { VersionBehavior = _ => Task.FromResult<string?>("0.1.0") };
        var factory = new RecordingExecutorFactory { Behavior = (_, _) => cli };
        var channel = new OutcomeChannel();
        var lane = MakeLane(factory, channel: channel);

        var outcome = await lane.RunAsync(Req(), CancellationToken.None);

        await Assert.That(outcome).IsEqualTo(new MutationOutcome.Refused("cli_below_floor", RecoverySurface.Attention));
        await Assert.That(cli.VersionCallCount).IsEqualTo(1);
        await Assert.That(cli.StartVerifiedCallCount).IsEqualTo(0);

        var envelope = await NextEnvelopeAsync(channel);
        await Assert.That(envelope.Request).IsEqualTo(Req());
        await Assert.That(envelope.Outcome).IsEqualTo(outcome);

        await lane.DisposeAsync();
    }

    [Test]
    public async Task Probe_in_flight_blocks_the_mutation_until_it_resolves() {
        var gate = new TaskCompletionSource<string?>();
        var cli = new FakeKcapCli { VersionBehavior = _ => gate.Task };
        var factory = new RecordingExecutorFactory { Behavior = (_, _) => cli };
        var lane = MakeLane(factory);

        var t = lane.RunAsync(Req(), CancellationToken.None);

        await Assert.That(cli.StartVerifiedCallCount).IsEqualTo(0);

        gate.SetResult("9.9.9");
        var outcome = await t;

        await Assert.That(outcome).IsEqualTo(new MutationOutcome.Succeeded());
        await Assert.That(cli.StartVerifiedCallCount).IsEqualTo(1);

        await lane.DisposeAsync();
    }

    [Test]
    public async Task Executor_factory_runs_once_per_action_and_the_same_pinned_path_serves_probe_and_mutation() {
        var cli = new FakeKcapCli();
        var factory = new RecordingExecutorFactory { Behavior = (_, _) => cli };
        var lane = MakeLane(factory, cliOverride: () => "/custom/kcap");

        var outcome = await lane.RunAsync(Req(), CancellationToken.None);

        await Assert.That(outcome).IsEqualTo(new MutationOutcome.Succeeded());
        await Assert.That(factory.Calls.Count).IsEqualTo(1);
        await Assert.That(factory.Calls[0].PinnedPath).IsEqualTo("/custom/kcap");
        await Assert.That(cli.VersionCallCount).IsEqualTo(1);
        await Assert.That(cli.StartVerifiedCallCount).IsEqualTo(1); // same FakeKcapCli instance served both calls

        await lane.DisposeAsync();
    }

    [Test]
    public async Task QuiescedAsync_completes_only_after_the_owned_action_reaches_a_terminal_state() {
        var gate = new TaskCompletionSource<string?>();
        var cli = new FakeKcapCli { VersionBehavior = _ => gate.Task };
        var factory = new RecordingExecutorFactory { Behavior = (_, _) => cli };
        var lane = MakeLane(factory);

        var t = lane.RunAsync(Req(), CancellationToken.None);
        var quiesced = lane.QuiescedAsync(CancellationToken.None);

        await Assert.That(quiesced.IsCompleted).IsFalse();

        gate.SetResult("9.9.9");
        await t;

        await quiesced.WaitAsync(Bounded);
        await Assert.That(quiesced.IsCompleted).IsTrue();

        await lane.DisposeAsync();
    }

    [Test]
    public async Task Cli_override_is_read_fresh_per_action_not_cached_from_a_prior_action() {
        var paths = new Queue<string?>(["path-a", "path-b"]);
        var cliA = new FakeKcapCli();
        var cliB = new FakeKcapCli();
        var factory = new RecordingExecutorFactory();
        factory.Behavior = (req, _) => req.DaemonName == "daemon-a" ? cliA : cliB;
        var lane = MakeLane(factory, cliOverride: paths.Dequeue);

        var outcome1 = await lane.RunAsync(Req(daemonName: "daemon-a"), CancellationToken.None);
        var outcome2 = await lane.RunAsync(Req(daemonName: "daemon-b"), CancellationToken.None);

        await Assert.That(outcome1).IsEqualTo(new MutationOutcome.Succeeded());
        await Assert.That(outcome2).IsEqualTo(new MutationOutcome.Succeeded());
        await Assert.That(factory.Calls.Count).IsEqualTo(2);
        await Assert.That(factory.Calls[0].PinnedPath).IsEqualTo("path-a");
        await Assert.That(factory.Calls[1].PinnedPath).IsEqualTo("path-b");

        await lane.DisposeAsync();
    }

    [Test]
    public async Task Observation_strategy_is_pinned_once_and_falls_back_to_the_oneshot_factory_when_no_live_adapter_is_set() {
        var cli = new FakeKcapCli();
        var factory = new RecordingExecutorFactory { Behavior = (_, _) => cli };
        var oneShotCalls = 0;
        IDaemonObservation? seen = null;
        var lane = MakeLane(factory, oneShotFactory: _ => { oneShotCalls++; return new ScriptedObservation(); });
        lane.Classify = (request, result, executor, observation, attemptId, ct) => {
            seen = observation;
            return Task.FromResult<MutationOutcome>(new MutationOutcome.Succeeded());
        };

        await lane.RunAsync(Req(), CancellationToken.None);

        await Assert.That(oneShotCalls).IsEqualTo(1);
        await Assert.That(seen).IsTypeOf<ScriptedObservation>();

        await lane.DisposeAsync();
    }

    // ---- T1: verb coverage ----

    [Test]
    public async Task Install_verb_calls_ServiceInstallVerifiedAsync_with_replace_false() {
        var cli = new FakeKcapCli();
        var factory = new RecordingExecutorFactory { Behavior = (_, _) => cli };
        var lane = MakeLane(factory);

        var outcome = await lane.RunAsync(Req(verb: MutationVerb.Install), CancellationToken.None);

        await Assert.That(outcome).IsEqualTo(new MutationOutcome.Succeeded());
        await Assert.That(cli.InstallVerifiedCallCount).IsEqualTo(1);
        await Assert.That(cli.LastInstallReplace).IsEqualTo(false);

        await lane.DisposeAsync();
    }

    [Test]
    public async Task Replace_verb_calls_ServiceInstallVerifiedAsync_with_replace_true() {
        var cli = new FakeKcapCli();
        var factory = new RecordingExecutorFactory { Behavior = (_, _) => cli };
        var lane = MakeLane(factory);

        var outcome = await lane.RunAsync(Req(verb: MutationVerb.Replace), CancellationToken.None);

        await Assert.That(outcome).IsEqualTo(new MutationOutcome.Succeeded());
        await Assert.That(cli.InstallVerifiedCallCount).IsEqualTo(1);
        await Assert.That(cli.LastInstallReplace).IsEqualTo(true);

        await lane.DisposeAsync();
    }

    [Test]
    public async Task StartVerified_verb_calls_ServiceStartVerifiedAsync() {
        var cli = new FakeKcapCli();
        var factory = new RecordingExecutorFactory { Behavior = (_, _) => cli };
        var lane = MakeLane(factory);

        var outcome = await lane.RunAsync(Req(verb: MutationVerb.StartVerified), CancellationToken.None);

        await Assert.That(outcome).IsEqualTo(new MutationOutcome.Succeeded());
        await Assert.That(cli.StartVerifiedCallCount).IsEqualTo(1);

        await lane.DisposeAsync();
    }

    [Test]
    public async Task DetachedStart_verb_calls_the_bootAttemptId_overload_with_an_N_format_guid() {
        var cli = new FakeKcapCli();
        var factory = new RecordingExecutorFactory { Behavior = (_, _) => cli };
        var lane = MakeLane(factory);

        var outcome = await lane.RunAsync(Req(verb: MutationVerb.DetachedStart), CancellationToken.None);

        await Assert.That(outcome).IsEqualTo(new MutationOutcome.Succeeded());
        await Assert.That(cli.DetachedStartCallCount).IsEqualTo(1);
        await Assert.That(cli.LastBootAttemptId).IsNotNull();
        await Assert.That(Guid.TryParseExact(cli.LastBootAttemptId, "N", out _)).IsTrue();

        await lane.DisposeAsync();
    }

    [Test]
    public async Task Successive_detached_start_actions_get_different_attempt_ids() {
        var cliA = new FakeKcapCli();
        var cliB = new FakeKcapCli();
        var factory = new RecordingExecutorFactory();
        factory.Behavior = (req, _) => req.DaemonName == "daemon-a" ? cliA : cliB;
        var lane = MakeLane(factory);

        await lane.RunAsync(Req(verb: MutationVerb.DetachedStart, daemonName: "daemon-a"), CancellationToken.None);
        await lane.RunAsync(Req(verb: MutationVerb.DetachedStart, daemonName: "daemon-b"), CancellationToken.None);

        await Assert.That(cliA.LastBootAttemptId).IsNotNull();
        await Assert.That(cliB.LastBootAttemptId).IsNotNull();
        await Assert.That(cliA.LastBootAttemptId).IsNotEqualTo(cliB.LastBootAttemptId);

        await lane.DisposeAsync();
    }

    // ---- T2: SetLiveAdapter coverage ----

    [Test]
    public async Task Live_adapter_with_matching_evidence_is_pinned_and_the_oneshot_factory_is_never_called() {
        var cli = new FakeKcapCli();
        var factory = new RecordingExecutorFactory { Behavior = (_, _) => cli };
        var live = new ScriptedObservation { Behavior = (_, _) => Task.FromResult<ObservedEvidence?>(MatchingEvidence()) };
        var oneShotCalls = 0;
        IDaemonObservation? seen = null;
        var lane = MakeLane(factory, oneShotFactory: _ => { oneShotCalls++; return new ScriptedObservation(); });
        lane.Classify = (request, result, executor, observation, attemptId, ct) => {
            seen = observation;
            return Task.FromResult<MutationOutcome>(new MutationOutcome.Succeeded());
        };
        lane.SetLiveAdapter(live);

        await lane.RunAsync(Req(), CancellationToken.None);

        await Assert.That(oneShotCalls).IsEqualTo(0);
        await Assert.That(seen).IsSameReferenceAs(live);

        await lane.DisposeAsync();
    }

    [Test]
    public async Task Live_adapter_returning_null_falls_back_to_the_oneshot_factory() {
        var cli = new FakeKcapCli();
        var factory = new RecordingExecutorFactory { Behavior = (_, _) => cli };
        var live = new ScriptedObservation(); // default behavior returns null — cannot target this request
        var oneShotCalls = 0;
        IDaemonObservation? seen = null;
        var lane = MakeLane(factory, oneShotFactory: _ => { oneShotCalls++; return new ScriptedObservation(); });
        lane.Classify = (request, result, executor, observation, attemptId, ct) => {
            seen = observation;
            return Task.FromResult<MutationOutcome>(new MutationOutcome.Succeeded());
        };
        lane.SetLiveAdapter(live);

        await lane.RunAsync(Req(), CancellationToken.None);

        await Assert.That(oneShotCalls).IsEqualTo(1);
        await Assert.That(seen).IsNotSameReferenceAs(live);

        await lane.DisposeAsync();
    }

    [Test]
    public async Task Observation_strategy_pinned_at_action_start_survives_a_mid_action_SetLiveAdapter_swap() {
        var gate = new TaskCompletionSource<ProcessResult>();
        var cli = new FakeKcapCli { StartVerifiedBehavior = _ => gate.Task }; // gate AFTER the pin step, not before it
        var factory = new RecordingExecutorFactory { Behavior = (_, _) => cli };
        var originalLive = new ScriptedObservation { Behavior = (_, _) => Task.FromResult<ObservedEvidence?>(MatchingEvidence()) };
        var otherLive = new ScriptedObservation { Behavior = (_, _) => Task.FromResult<ObservedEvidence?>(MatchingEvidence(222, "inst-2")) };
        IDaemonObservation? seen = null;
        var lane = MakeLane(factory);
        lane.Classify = (request, result, executor, observation, attemptId, ct) => {
            seen = observation;
            return Task.FromResult<MutationOutcome>(new MutationOutcome.Succeeded());
        };
        lane.SetLiveAdapter(originalLive);

        var t = lane.RunAsync(Req(), CancellationToken.None);
        // By now the pin step has already run (only the mutation is gated) — a swap here must not change it.
        lane.SetLiveAdapter(otherLive);

        gate.SetResult(new ProcessResult(0, "", "", false));
        await t;

        await Assert.That(seen).IsSameReferenceAs(originalLive);
        await Assert.That(seen).IsNotSameReferenceAs(otherLive);

        await lane.DisposeAsync();
    }

    // ---- I1: post-dispose admission must never spawn a real mutation ----

    [Test]
    public async Task Dispose_mid_action_cancels_queued_work_without_starting_a_successor() {
        var requestA = Req(daemonName: "daemon-a");
        var requestB = Req(daemonName: "daemon-b");
        var gateA = new TaskCompletionSource<string?>();
        var cliA = new FakeKcapCli { VersionBehavior = _ => gateA.Task };
        var cliB = new FakeKcapCli();
        var factory = new RecordingExecutorFactory { Behavior = (req, _) => req.DaemonName == "daemon-a" ? cliA : cliB };
        var lane = MakeLane(factory);

        var tA = lane.RunAsync(requestA, CancellationToken.None);
        var tB = lane.RunAsync(requestB, CancellationToken.None); // queues behind A

        var disposeTask = lane.DisposeAsync().AsTask(); // drains+cancels the queue synchronously; still awaiting A

        await Assert.ThrowsAsync<OperationCanceledException>(() => tB); // B's queued waiter cancels promptly, not waiting for A
        await Assert.That(disposeTask.IsCompleted).IsFalse(); // still waiting on A's owned task

        gateA.SetResult("9.9.9"); // A observes the cancelled lifetime token at its pre-Dispatch gate

        await Assert.ThrowsAsync<OperationCanceledException>(() => tA);
        await disposeTask.WaitAsync(Bounded);

        await Assert.That(factory.Calls.Count).IsEqualTo(1); // B's executor factory never invoked — no successor started
        await Assert.That(cliA.StartVerifiedCallCount).IsEqualTo(0); // A never actually dispatched either

        await lane.DisposeAsync(); // idempotent
    }

    // ---- I2: no outcome silently vanishes ----

    [Test, NotInParallel]
    public async Task Waiterless_cancellation_from_a_disposed_lane_logs_one_line_and_enqueues_nothing() {
        var request = Req();
        var gate = new TaskCompletionSource<string?>();
        var cli = new FakeKcapCli { VersionBehavior = _ => gate.Task };
        var factory = new RecordingExecutorFactory { Behavior = (_, _) => cli };
        var channel = new OutcomeChannel();
        var lane = MakeLane(factory, channel: channel);

        using var ctsA = new CancellationTokenSource();
        var tA = lane.RunAsync(request, ctsA.Token);
        await ctsA.CancelAsync();
        await Assert.ThrowsAsync<OperationCanceledException>(() => tA); // detaches — zero waiters remain on the owned slot

        var disposeTask = lane.DisposeAsync().AsTask(); // still awaiting the owned action

        var originalError = Console.Error;
        var stderrWriter = new StringWriter();
        try {
            Console.SetError(stderrWriter);
            gate.SetResult("9.9.9"); // unblocks the (now waiterless) owned action into its pre-Dispatch cancellation throw
        } finally {
            Console.SetError(originalError);
        }

        await disposeTask.WaitAsync(Bounded);

        await Assert.That(stderrWriter.ToString()).Contains("waiterless cancellation");
        await AssertChannelEmptyAsync(channel); // shutdown is not actionable evidence
    }

    [Test, NotInParallel]
    public async Task Unexpected_exception_during_a_mutation_attempt_logs_and_enqueues_a_failed_outcome() {
        var cli = new FakeKcapCli { StartVerifiedBehavior = _ => throw new InvalidOperationException("boom") };
        var factory = new RecordingExecutorFactory { Behavior = (_, _) => cli };
        var channel = new OutcomeChannel();
        var lane = MakeLane(factory, channel: channel);

        var originalError = Console.Error;
        var stderrWriter = new StringWriter();
        MutationOutcome outcome;
        try {
            Console.SetError(stderrWriter);
            outcome = await lane.RunAsync(Req(), CancellationToken.None);
        } finally {
            Console.SetError(originalError);
        }

        await Assert.That(outcome).IsEqualTo(new MutationOutcome.Failed(-1, nameof(InvalidOperationException), RecoverySurface.Attention));
        await Assert.That(stderrWriter.ToString()).Contains(nameof(InvalidOperationException));

        var envelope = await NextEnvelopeAsync(channel);
        await Assert.That(envelope.Outcome).IsEqualTo(outcome);

        await lane.DisposeAsync();
    }

    // ---- M5: waiterless success is logged, not silently dropped ----

    [Test, NotInParallel]
    public async Task Waiterless_success_logs_one_line_to_console_error() {
        var request = Req();
        var gate = new TaskCompletionSource<string?>();
        var cli = new FakeKcapCli { VersionBehavior = _ => gate.Task };
        var factory = new RecordingExecutorFactory { Behavior = (_, _) => cli };
        var lane = MakeLane(factory);

        using var cts = new CancellationTokenSource();
        var t = lane.RunAsync(request, cts.Token);
        await cts.CancelAsync();
        await Assert.ThrowsAsync<OperationCanceledException>(() => t); // detaches — zero waiters remain on the owned slot

        var originalError = Console.Error;
        var stderrWriter = new StringWriter();
        try {
            Console.SetError(stderrWriter);
            gate.SetResult("9.9.9"); // action still runs to a normal Succeeded outcome with nobody left to observe it
        } finally {
            Console.SetError(originalError);
        }

        await Assert.That(cli.StartVerifiedCallCount).IsEqualTo(1); // the action still completed
        await Assert.That(stderrWriter.ToString()).Contains("waiterless Succeeded");

        await lane.DisposeAsync();
    }
}
