using Capacitor.App.Services;
using Capacitor.App.Services.Mutation;
using Capacitor.Cli.Core;
using Microsoft.Extensions.Time.Testing;

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

    static ObservedEvidence MatchingEvidence(
            int pid = 111, string instanceId = "inst-1", string server = "https://cap.example.test",
            string daemonName = "daemon-a", IReadOnlyList<string>? capabilities = null, string? version = "1.0.0") =>
        new(true, capabilities ?? ["consent/3"], version, server, daemonName, pid, instanceId, true);

    static ServiceSnapshot Ownership(
            int? jobPid = 111, int? daemonPid = 111, bool txnMarker = false, bool txnActive = false,
            string state = "running", bool unitPresent = true) =>
        new("daemon-a", unitPresent, state, "/opt/kcap/kcapd", "/opt/kcap/kcapd", jobPid, daemonPid, txnMarker, txnActive);

    static string MarkerJson(string daemonName, string? attemptId) => $$"""
        {"daemon_name":"{{daemonName}}","token":"server_expectation_mismatch","expectation":"https://s","resolved":"https://t","pid":4242,"instance_id":"inst-1","attempt_id":{{(attemptId is null ? "null" : $"\"{attemptId}\"")}}}
        """;

    static void PlantMarker(string daemonName, string content) {
        var path = BootRefusalMarker.MarkerPath(daemonName);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
    }

    /// Drives a suspended poll loop by repeatedly advancing a FakeTimeProvider until the task
    /// settles — Task.Delay(interval, time, ct)'s continuation resumes synchronously inside
    /// Advance(), so no real waiting is needed (same pattern as ServiceVerifyStartTests.Drive).
    static async Task<MutationOutcome> Drive(Task<MutationOutcome> task, FakeTimeProvider time, TimeSpan step) {
        var guard = 0;
        while (!task.IsCompleted && guard++ < 500) time.Advance(step);
        return await task.WaitAsync(Bounded);
    }

    static Task<MutationOutcome> CannedSucceeded(
            MutationRequest request, ProcessResult result, IKcapCli executor, IDaemonObservation observation,
            string? attemptId, CancellationToken ct) =>
        Task.FromResult<MutationOutcome>(new MutationOutcome.Succeeded());

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
            Func<MutationRequest, IDaemonObservation>? oneShotFactory = null,
            TimeProvider? time = null, MutationClassifier? classify = null) {
        var lane = new DaemonMutationLane(
            shellProbe ?? new FakeLoginShellProbe { KcapPathBehavior = _ => Task.FromResult<string?>(null) },
            channel ?? new OutcomeChannel(),
            cliOverride ?? (() => "/opt/kcap/bin/kcap"),
            factory.Invoke,
            oneShotFactory ?? (_ => new ScriptedObservation()),
            time ?? TimeProvider.System);
        if (classify is not null) lane.Classify = classify;
        return lane;
    }

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
        var lane = MakeLane(factory, classify: CannedSucceeded);

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
        var lane = MakeLane(factory, classify: CannedSucceeded);

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
        var lane = MakeLane(factory, classify: CannedSucceeded);

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
        var lane = MakeLane(factory, classify: CannedSucceeded);

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
        var lane = MakeLane(factory, cliOverride: () => "/custom/kcap", classify: CannedSucceeded);

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
        var lane = MakeLane(factory, cliOverride: paths.Dequeue, classify: CannedSucceeded);

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
        var lane = MakeLane(factory, classify: CannedSucceeded);

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
        var lane = MakeLane(factory, classify: CannedSucceeded);

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
        var lane = MakeLane(factory, classify: CannedSucceeded);

        var outcome = await lane.RunAsync(Req(verb: MutationVerb.StartVerified), CancellationToken.None);

        await Assert.That(outcome).IsEqualTo(new MutationOutcome.Succeeded());
        await Assert.That(cli.StartVerifiedCallCount).IsEqualTo(1);

        await lane.DisposeAsync();
    }

    [Test]
    public async Task DetachedStart_verb_calls_the_bootAttemptId_overload_with_an_N_format_guid() {
        var cli = new FakeKcapCli();
        var factory = new RecordingExecutorFactory { Behavior = (_, _) => cli };
        var lane = MakeLane(factory, classify: CannedSucceeded);

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
        var lane = MakeLane(factory, classify: CannedSucceeded);

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

    // N1: RunAsync arriving after DisposeAsync has already completed must resolve cancelled under
    // the _gate _disposed check in AttachOrCreate — no action started, nothing enqueued.
    [Test]
    public async Task RunAsync_after_dispose_has_completed_resolves_cancelled_and_starts_nothing() {
        var factory = new RecordingExecutorFactory();
        var channel = new OutcomeChannel();
        var lane = MakeLane(factory, channel: channel);
        await lane.DisposeAsync();

        await Assert.ThrowsAsync<OperationCanceledException>(() => lane.RunAsync(Req(), CancellationToken.None));

        await Assert.That(factory.Calls.Count).IsEqualTo(0);
        await AssertChannelEmptyAsync(channel);
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

        // N2: the exception type/message stay ONLY in the log line — the outcome itself carries a
        // named, stable exit code and reason token, never a leaked exception identity.
        await Assert.That(outcome).IsEqualTo(new MutationOutcome.Failed(DaemonMutationLane.UnexpectedExitCode, "internal_error", RecoverySurface.Attention));
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
        var lane = MakeLane(factory, classify: CannedSucceeded);

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

    // ==== Task 9b: outcome classification (service verbs, exit 0) ====

    [Test]
    public async Task Mutation_failure_beside_matching_evidence_is_not_Succeeded() {
        var cli = new FakeKcapCli {
            StartVerifiedBehavior = _ => Task.FromResult(new ProcessResult(5, "", "", false)),
            StatusBehavior = _ => Task.FromResult<ServiceSnapshot?>(Ownership()),
        };
        var factory = new RecordingExecutorFactory { Behavior = (_, _) => cli };
        var observation = new ScriptedObservation { Behavior = (_, _) => Task.FromResult<ObservedEvidence?>(MatchingEvidence()) };
        var lane = MakeLane(factory, oneShotFactory: _ => observation);

        var outcome = await lane.RunAsync(Req(), CancellationToken.None);

        // A nonzero exit is decisive regardless of what evidence happens to show — never consulted.
        await Assert.That(outcome).IsEqualTo(new MutationOutcome.Failed(5, null, RecoverySurface.Attention));

        await lane.DisposeAsync();
    }

    [Test]
    public async Task Wrong_server_evidence_on_success_exit_yields_AttentionSkew() {
        var cli = new FakeKcapCli { StatusBehavior = _ => Task.FromResult<ServiceSnapshot?>(Ownership()) };
        var factory = new RecordingExecutorFactory { Behavior = (_, _) => cli };
        var wrongServer = MatchingEvidence() with { ServerUrl = "https://wrong.example.test" };
        var lane = MakeLane(factory, oneShotFactory: _ => new ScriptedObservation { Behavior = (_, _) => Task.FromResult<ObservedEvidence?>(wrongServer) });

        var outcome = await lane.RunAsync(Req(), CancellationToken.None);

        await Assert.That(outcome).IsTypeOf<MutationOutcome.AttentionSkew>();

        await lane.DisposeAsync();
    }

    [Test]
    public async Task Manual_non_owning_job_pid_mismatch_is_not_Succeeded() {
        var cli = new FakeKcapCli { StatusBehavior = _ => Task.FromResult<ServiceSnapshot?>(Ownership(jobPid: 111, daemonPid: 222)) };
        var factory = new RecordingExecutorFactory { Behavior = (_, _) => cli };
        var evidence = MatchingEvidence(pid: 222);
        var lane = MakeLane(factory, oneShotFactory: _ => new ScriptedObservation { Behavior = (_, _) => Task.FromResult<ObservedEvidence?>(evidence) });

        var outcome = await lane.RunAsync(Req(), CancellationToken.None);

        await Assert.That(outcome).IsNotEqualTo(new MutationOutcome.Succeeded());
        await Assert.That(outcome).IsTypeOf<MutationOutcome.AttentionSkew>();

        await lane.DisposeAsync();
    }

    [Test]
    public async Task Unreachable_evidence_with_no_recorded_owner_is_UnconfirmedNoAttach() {
        var cli = new FakeKcapCli(); // default: exit 0, StatusBehavior returns null (no owner)
        var factory = new RecordingExecutorFactory { Behavior = (_, _) => cli };
        var lane = MakeLane(factory); // default oneShotFactory's ScriptedObservation returns null evidence

        var outcome = await lane.RunAsync(Req(), CancellationToken.None);

        await Assert.That(outcome).IsEqualTo(new MutationOutcome.UnconfirmedNoAttach());

        await lane.DisposeAsync();
    }

    [Test]
    public async Task Unreachable_evidence_with_a_recorded_owner_pid_yields_AttentionSkew() {
        var cli = new FakeKcapCli { StatusBehavior = _ => Task.FromResult<ServiceSnapshot?>(Ownership()) };
        var factory = new RecordingExecutorFactory { Behavior = (_, _) => cli };
        var lane = MakeLane(factory); // evidence stays unreachable (default ScriptedObservation)

        var outcome = await lane.RunAsync(Req(), CancellationToken.None);

        await Assert.That(outcome).IsTypeOf<MutationOutcome.AttentionSkew>();

        await lane.DisposeAsync();
    }

    [Test]
    public async Task Missing_consent_capability_yields_AttentionSkew() {
        var cli = new FakeKcapCli { StatusBehavior = _ => Task.FromResult<ServiceSnapshot?>(Ownership()) };
        var factory = new RecordingExecutorFactory { Behavior = (_, _) => cli };
        var noCapabilities = MatchingEvidence() with { Capabilities = [] };
        var lane = MakeLane(factory, oneShotFactory: _ => new ScriptedObservation { Behavior = (_, _) => Task.FromResult<ObservedEvidence?>(noCapabilities) });

        var outcome = await lane.RunAsync(Req(), CancellationToken.None);

        await Assert.That(outcome).IsTypeOf<MutationOutcome.AttentionSkew>();

        await lane.DisposeAsync();
    }

    [Test]
    public async Task Preslice_evidence_without_pid_or_instance_never_succeeds() {
        var cli = new FakeKcapCli { StatusBehavior = _ => Task.FromResult<ServiceSnapshot?>(Ownership()) };
        var factory = new RecordingExecutorFactory { Behavior = (_, _) => cli };
        var preslice = new ObservedEvidence(true, ["consent/3"], "1.0.0", "https://cap.example.test", "daemon-a", null, null, false);
        var lane = MakeLane(factory, oneShotFactory: _ => new ScriptedObservation { Behavior = (_, _) => Task.FromResult<ObservedEvidence?>(preslice) });

        var outcome = await lane.RunAsync(Req(), CancellationToken.None);

        await Assert.That(outcome).IsNotEqualTo(new MutationOutcome.Succeeded());
        await Assert.That(outcome).IsTypeOf<MutationOutcome.AttentionSkew>();

        await lane.DisposeAsync();
    }

    [Test]
    public async Task Below_floor_daemon_version_at_observation_yields_AttentionSkew() {
        var cli = new FakeKcapCli { StatusBehavior = _ => Task.FromResult<ServiceSnapshot?>(Ownership()) };
        var factory = new RecordingExecutorFactory { Behavior = (_, _) => cli };
        var oldVersion = MatchingEvidence() with { DaemonVersion = "0.1.0" };
        var lane = MakeLane(factory, oneShotFactory: _ => new ScriptedObservation { Behavior = (_, _) => Task.FromResult<ObservedEvidence?>(oldVersion) });

        var outcome = await lane.RunAsync(Req(), CancellationToken.None);

        await Assert.That(outcome).IsTypeOf<MutationOutcome.AttentionSkew>();

        await lane.DisposeAsync();
    }

    [Test]
    public async Task Instance_pid_cross_check_failure_yields_AttentionSkew() {
        var cli = new FakeKcapCli { StatusBehavior = _ => Task.FromResult<ServiceSnapshot?>(Ownership(jobPid: 333, daemonPid: 333)) };
        var factory = new RecordingExecutorFactory { Behavior = (_, _) => cli };
        var evidence = MatchingEvidence(pid: 111); // ownership.DaemonPid(333) != evidence.Pid(111)
        var lane = MakeLane(factory, oneShotFactory: _ => new ScriptedObservation { Behavior = (_, _) => Task.FromResult<ObservedEvidence?>(evidence) });

        var outcome = await lane.RunAsync(Req(), CancellationToken.None);

        await Assert.That(outcome).IsTypeOf<MutationOutcome.AttentionSkew>();

        await lane.DisposeAsync();
    }

    [Test]
    public async Task Stale_txn_marker_on_success_exit_yields_AttentionRepair() {
        var cli = new FakeKcapCli { StatusBehavior = _ => Task.FromResult<ServiceSnapshot?>(Ownership(txnMarker: true, txnActive: false)) };
        var factory = new RecordingExecutorFactory { Behavior = (_, _) => cli };
        var lane = MakeLane(factory, oneShotFactory: _ => new ScriptedObservation { Behavior = (_, _) => Task.FromResult<ObservedEvidence?>(MatchingEvidence()) });

        var outcome = await lane.RunAsync(Req(), CancellationToken.None);

        await Assert.That(outcome).IsTypeOf<MutationOutcome.AttentionRepair>();

        await lane.DisposeAsync();
    }

    [Test]
    public async Task Full_matching_evidence_and_ownership_on_exit_zero_is_Succeeded() {
        var cli = new FakeKcapCli { StatusBehavior = _ => Task.FromResult<ServiceSnapshot?>(Ownership()) };
        var factory = new RecordingExecutorFactory { Behavior = (_, _) => cli };
        var lane = MakeLane(factory, oneShotFactory: _ => new ScriptedObservation { Behavior = (_, _) => Task.FromResult<ObservedEvidence?>(MatchingEvidence()) });

        var outcome = await lane.RunAsync(Req(), CancellationToken.None);

        await Assert.That(outcome).IsEqualTo(new MutationOutcome.Succeeded());

        await lane.DisposeAsync();
    }

    [Test]
    public async Task Succeeded_outcome_never_reaches_the_channel() {
        var cli = new FakeKcapCli { StatusBehavior = _ => Task.FromResult<ServiceSnapshot?>(Ownership()) };
        var factory = new RecordingExecutorFactory { Behavior = (_, _) => cli };
        var channel = new OutcomeChannel();
        var lane = MakeLane(
            factory, channel: channel,
            oneShotFactory: _ => new ScriptedObservation { Behavior = (_, _) => Task.FromResult<ObservedEvidence?>(MatchingEvidence()) });

        var outcome = await lane.RunAsync(Req(), CancellationToken.None);
        await Assert.That(outcome).IsEqualTo(new MutationOutcome.Succeeded());

        await AssertChannelEmptyAsync(channel);

        await lane.DisposeAsync();
    }

    [Test]
    public async Task AttentionSkew_outcome_is_enqueued_exactly_once_with_its_own_request() {
        var cli = new FakeKcapCli { StatusBehavior = _ => Task.FromResult<ServiceSnapshot?>(Ownership()) };
        var factory = new RecordingExecutorFactory { Behavior = (_, _) => cli };
        var channel = new OutcomeChannel();
        var wrongServer = MatchingEvidence() with { ServerUrl = "https://wrong.example.test" };
        var lane = MakeLane(
            factory, channel: channel,
            oneShotFactory: _ => new ScriptedObservation { Behavior = (_, _) => Task.FromResult<ObservedEvidence?>(wrongServer) });

        var request = Req();
        var outcome = await lane.RunAsync(request, CancellationToken.None);
        await Assert.That(outcome).IsTypeOf<MutationOutcome.AttentionSkew>();

        var envelope = await NextEnvelopeAsync(channel);
        await Assert.That(envelope.Request).IsEqualTo(request);
        await Assert.That(envelope.Outcome).IsEqualTo(outcome);
        await AssertChannelEmptyAsync(channel); // exactly once, not merely at-least-once

        await lane.DisposeAsync();
    }

    [Test]
    public async Task Service_verb_TimedOut_never_enters_the_success_ladder_regardless_of_exit_code() {
        // A forced kill's exit code is not a verify outcome — the transaction may have already
        // committed. Full matching evidence must NOT read as Succeeded here; SucceededAfterTimeout
        // is detached-only.
        var cli = new FakeKcapCli {
            StartVerifiedBehavior = _ => Task.FromResult(new ProcessResult(0, "", "", true)),
            StatusBehavior = _ => Task.FromResult<ServiceSnapshot?>(Ownership()),
        };
        var factory = new RecordingExecutorFactory { Behavior = (_, _) => cli };
        var lane = MakeLane(factory, oneShotFactory: _ => new ScriptedObservation { Behavior = (_, _) => Task.FromResult<ObservedEvidence?>(MatchingEvidence()) });

        var outcome = await lane.RunAsync(Req(), CancellationToken.None);

        await Assert.That(outcome).IsEqualTo(new MutationOutcome.UnconfirmedNoAttach());

        await lane.DisposeAsync();
    }

    // ---- IMPORTANT 2 (round 1): ownership repair/skew signals evaluated regardless of evidence reachability ----

    [Test]
    public async Task Stale_txn_marker_with_unreachable_evidence_yields_AttentionRepair_not_UnconfirmedNoAttach() {
        var cli = new FakeKcapCli { StatusBehavior = _ => Task.FromResult<ServiceSnapshot?>(Ownership(txnMarker: true, txnActive: false)) };
        var factory = new RecordingExecutorFactory { Behavior = (_, _) => cli };
        var lane = MakeLane(factory); // default evidence stays unreachable

        var outcome = await lane.RunAsync(Req(), CancellationToken.None);

        await Assert.That(outcome).IsEqualTo(new MutationOutcome.AttentionRepair("stale_txn_marker"));

        await lane.DisposeAsync();
    }

    [Test]
    public async Task Running_service_without_a_daemon_pid_yields_AttentionRepair() {
        // Mirrors DaemonLifecycleController.Reconcile's `state == ServiceState.Running && snap.DaemonPid is null` leg.
        var cli = new FakeKcapCli { StatusBehavior = _ => Task.FromResult<ServiceSnapshot?>(Ownership(jobPid: 111, daemonPid: null, state: "running")) };
        var factory = new RecordingExecutorFactory { Behavior = (_, _) => cli };
        var lane = MakeLane(factory, oneShotFactory: _ => new ScriptedObservation { Behavior = (_, _) => Task.FromResult<ObservedEvidence?>(MatchingEvidence()) });

        var outcome = await lane.RunAsync(Req(), CancellationToken.None);

        await Assert.That(outcome).IsEqualTo(new MutationOutcome.AttentionRepair("running_without_daemon_pid"));

        await lane.DisposeAsync();
    }

    [Test]
    public async Task Daemon_running_outside_an_uninstalled_service_yields_AttentionRepair() {
        // Mirrors DaemonLifecycleController.Reconcile's `snap.UnitPresent && state == ServiceState.NotInstalled && snap.DaemonPid is not null` leg.
        var cli = new FakeKcapCli { StatusBehavior = _ => Task.FromResult<ServiceSnapshot?>(Ownership(jobPid: null, daemonPid: 999, state: "not_installed", unitPresent: true)) };
        var factory = new RecordingExecutorFactory { Behavior = (_, _) => cli };
        var lane = MakeLane(factory); // evidence unreachable by default — ownership repair wins regardless

        var outcome = await lane.RunAsync(Req(), CancellationToken.None);

        await Assert.That(outcome).IsEqualTo(new MutationOutcome.AttentionRepair("daemon_running_outside_service"));

        await lane.DisposeAsync();
    }

    // ==== Task 9b: outcome classification (service verbs, coded nonzero exits) ====

    [Test]
    public async Task Exit28_with_a_takeover_routed_token_fails_with_Takeover_surface() {
        var cli = new FakeKcapCli { StartVerifiedBehavior = _ => Task.FromResult(new ProcessResult(28, "", "start_gate_reason=identity_mismatch\n", false)) };
        var factory = new RecordingExecutorFactory { Behavior = (_, _) => cli };
        var lane = MakeLane(factory);

        var outcome = await lane.RunAsync(Req(), CancellationToken.None);

        await Assert.That(outcome).IsEqualTo(new MutationOutcome.Failed(28, "identity_mismatch", RecoverySurface.Takeover));

        await lane.DisposeAsync();
    }

    [Test]
    public async Task Exit28_with_a_reinstall_routed_token_fails_with_Reinstall_surface() {
        var cli = new FakeKcapCli { StartVerifiedBehavior = _ => Task.FromResult(new ProcessResult(28, "", "start_gate_reason=package_inconsistent\n", false)) };
        var factory = new RecordingExecutorFactory { Behavior = (_, _) => cli };
        var lane = MakeLane(factory);

        var outcome = await lane.RunAsync(Req(), CancellationToken.None);

        await Assert.That(outcome).IsEqualTo(new MutationOutcome.Failed(28, "package_inconsistent", RecoverySurface.Reinstall));

        await lane.DisposeAsync();
    }

    [Test]
    public async Task Exit28_with_zero_reason_lines_fails_closed_to_Attention() {
        var cli = new FakeKcapCli { StartVerifiedBehavior = _ => Task.FromResult(new ProcessResult(28, "", "", false)) };
        var factory = new RecordingExecutorFactory { Behavior = (_, _) => cli };
        var lane = MakeLane(factory);

        var outcome = await lane.RunAsync(Req(), CancellationToken.None);

        await Assert.That(outcome).IsEqualTo(new MutationOutcome.Failed(28, null, RecoverySurface.Attention));

        await lane.DisposeAsync();
    }

    [Test]
    public async Task Exit28_with_duplicate_conflicting_reason_lines_fails_closed_to_Attention() {
        var cli = new FakeKcapCli {
            StartVerifiedBehavior = _ => Task.FromResult(new ProcessResult(
                28, "", "start_gate_reason=identity_mismatch\nstart_gate_reason=foreign_binary\n", false)),
        };
        var factory = new RecordingExecutorFactory { Behavior = (_, _) => cli };
        var lane = MakeLane(factory);

        var outcome = await lane.RunAsync(Req(), CancellationToken.None);

        await Assert.That(outcome).IsEqualTo(new MutationOutcome.Failed(28, null, RecoverySurface.Attention));

        await lane.DisposeAsync();
    }

    [Test]
    public async Task Exit28_with_the_known_but_unrouted_evidence_unreadable_token_fails_closed_to_Attention() {
        var cli = new FakeKcapCli { StartVerifiedBehavior = _ => Task.FromResult(new ProcessResult(28, "", "start_gate_reason=evidence_unreadable\n", false)) };
        var factory = new RecordingExecutorFactory { Behavior = (_, _) => cli };
        var lane = MakeLane(factory);

        var outcome = await lane.RunAsync(Req(), CancellationToken.None);

        await Assert.That(outcome).IsEqualTo(new MutationOutcome.Failed(28, "evidence_unreadable", RecoverySurface.Attention));

        await lane.DisposeAsync();
    }

    [Test]
    public async Task Exit29_is_Attention_and_the_lane_never_retries_the_mutation() {
        var cli = new FakeKcapCli { StartVerifiedBehavior = _ => Task.FromResult(new ProcessResult(29, "", "", false)) };
        var factory = new RecordingExecutorFactory { Behavior = (_, _) => cli };
        var lane = MakeLane(factory);

        var outcome = await lane.RunAsync(Req(), CancellationToken.None);

        await Assert.That(outcome).IsEqualTo(new MutationOutcome.Failed(29, null, RecoverySurface.Attention));
        await Assert.That(cli.StartVerifiedCallCount).IsEqualTo(1);

        await lane.DisposeAsync();
    }

    [Test]
    public async Task Exit29_with_a_reason_line_present_still_fails_with_Attention_and_carries_the_token() {
        var cli = new FakeKcapCli { StartVerifiedBehavior = _ => Task.FromResult(new ProcessResult(29, "", "start_gate_reason=identity_mismatch\n", false)) };
        var factory = new RecordingExecutorFactory { Behavior = (_, _) => cli };
        var lane = MakeLane(factory);

        var outcome = await lane.RunAsync(Req(), CancellationToken.None);

        await Assert.That(outcome).IsEqualTo(new MutationOutcome.Failed(29, "identity_mismatch", RecoverySurface.Attention));

        await lane.DisposeAsync();
    }

    [Test]
    public async Task Readiness_timeout_with_refusal_reason_is_Refused_with_Takeover() {
        var cli = new FakeKcapCli {
            StartVerifiedBehavior = _ => Task.FromResult(new ProcessResult(24, "", "refusal_reason=server_expectation_mismatch\n", false)),
        };
        var factory = new RecordingExecutorFactory { Behavior = (_, _) => cli };
        var lane = MakeLane(factory);

        var outcome = await lane.RunAsync(Req(), CancellationToken.None);

        await Assert.That(outcome).IsEqualTo(new MutationOutcome.Refused("server_expectation_mismatch", RecoverySurface.Takeover));

        await lane.DisposeAsync();
    }

    [Test]
    public async Task Readiness_timeout_without_refusal_reason_is_UnconfirmedNoAttach() {
        var cli = new FakeKcapCli { StartVerifiedBehavior = _ => Task.FromResult(new ProcessResult(24, "", "", false)) };
        var factory = new RecordingExecutorFactory { Behavior = (_, _) => cli };
        var lane = MakeLane(factory);

        var outcome = await lane.RunAsync(Req(), CancellationToken.None);

        await Assert.That(outcome).IsEqualTo(new MutationOutcome.UnconfirmedNoAttach());

        await lane.DisposeAsync();
    }

    [Test]
    public async Task Other_nonzero_exit_fails_closed_to_Attention_with_no_reason() {
        var cli = new FakeKcapCli { StartVerifiedBehavior = _ => Task.FromResult(new ProcessResult(21, "", "verify_viability", false)) };
        var factory = new RecordingExecutorFactory { Behavior = (_, _) => cli };
        var lane = MakeLane(factory);

        var outcome = await lane.RunAsync(Req(), CancellationToken.None);

        await Assert.That(outcome).IsEqualTo(new MutationOutcome.Failed(21, null, RecoverySurface.Attention));

        await lane.DisposeAsync();
    }

    // ==== Task 9b: outcome classification (DetachedStart) ====

    [Test]
    public async Task Exit43_with_a_routed_token_fails_with_Reinstall_surface() {
        var cli = new FakeKcapCli { DetachedStartBehavior = _ => Task.FromResult(new ProcessResult(43, "", "daemon_start_reason=package_inconsistent\n", false)) };
        var factory = new RecordingExecutorFactory { Behavior = (_, _) => cli };
        var lane = MakeLane(factory);

        var outcome = await lane.RunAsync(Req(verb: MutationVerb.DetachedStart), CancellationToken.None);

        await Assert.That(outcome).IsEqualTo(new MutationOutcome.Failed(43, "package_inconsistent", RecoverySurface.Reinstall));

        await lane.DisposeAsync();
    }

    [Test]
    public async Task Exit43_with_no_reason_line_fails_closed_to_Attention() {
        var cli = new FakeKcapCli { DetachedStartBehavior = _ => Task.FromResult(new ProcessResult(43, "", "", false)) };
        var factory = new RecordingExecutorFactory { Behavior = (_, _) => cli };
        var lane = MakeLane(factory);

        var outcome = await lane.RunAsync(Req(verb: MutationVerb.DetachedStart), CancellationToken.None);

        await Assert.That(outcome).IsEqualTo(new MutationOutcome.Failed(43, null, RecoverySurface.Attention));

        await lane.DisposeAsync();
    }

    [Test]
    public async Task DetachedStart_exit_zero_with_immediate_full_evidence_is_Succeeded_without_waiting() {
        var cli = new FakeKcapCli { DetachedStartBehavior = _ => Task.FromResult(new ProcessResult(0, "", "", false)) };
        var factory = new RecordingExecutorFactory { Behavior = (_, _) => cli };
        var observation = new ScriptedObservation { Behavior = (_, _) => Task.FromResult<ObservedEvidence?>(MatchingEvidence()) };
        var lane = MakeLane(factory, oneShotFactory: _ => observation);

        var outcome = await lane.RunAsync(Req(verb: MutationVerb.DetachedStart), CancellationToken.None);

        await Assert.That(outcome).IsEqualTo(new MutationOutcome.Succeeded());

        await lane.DisposeAsync();
    }

    [Test]
    public async Task DetachedStart_exit_zero_window_expiry_with_no_evidence_and_no_marker_is_UnconfirmedNoAttach() {
        var time = new FakeTimeProvider();
        var cli = new FakeKcapCli { DetachedStartBehavior = _ => Task.FromResult(new ProcessResult(0, "", "", false)) };
        var factory = new RecordingExecutorFactory { Behavior = (_, _) => cli };
        var lane = MakeLane(factory, time: time); // default observation returns null evidence forever

        var task = lane.RunAsync(Req(verb: MutationVerb.DetachedStart), CancellationToken.None);
        var outcome = await Drive(task, time, TimeSpan.FromMilliseconds(500));

        await Assert.That(outcome).IsEqualTo(new MutationOutcome.UnconfirmedNoAttach());

        await lane.DisposeAsync();
    }

    [Test]
    public async Task DetachedStart_wrapper_timeout_with_full_evidence_is_SucceededAfterTimeout() {
        var cli = new FakeKcapCli { DetachedStartBehavior = _ => Task.FromResult(new ProcessResult(0, "", "", true)) };
        var factory = new RecordingExecutorFactory { Behavior = (_, _) => cli };
        var observation = new ScriptedObservation { Behavior = (_, _) => Task.FromResult<ObservedEvidence?>(MatchingEvidence()) };
        var lane = MakeLane(factory, oneShotFactory: _ => observation);

        var outcome = await lane.RunAsync(Req(verb: MutationVerb.DetachedStart), CancellationToken.None);

        await Assert.That(outcome).IsEqualTo(new MutationOutcome.SucceededAfterTimeout());

        await lane.DisposeAsync();
    }

    [Test]
    public async Task DetachedStart_wrapper_timeout_with_incomplete_evidence_is_UnconfirmedNoAttach() {
        var time = new FakeTimeProvider();
        var cli = new FakeKcapCli { DetachedStartBehavior = _ => Task.FromResult(new ProcessResult(0, "", "", true)) };
        var factory = new RecordingExecutorFactory { Behavior = (_, _) => cli };
        var lane = MakeLane(factory, time: time);

        var task = lane.RunAsync(Req(verb: MutationVerb.DetachedStart), CancellationToken.None);
        var outcome = await Drive(task, time, TimeSpan.FromMilliseconds(500));

        await Assert.That(outcome).IsEqualTo(new MutationOutcome.UnconfirmedNoAttach());

        await lane.DisposeAsync();
    }

    // ---- CRITICAL 1 (round 1): DetachedStart shares the SAME evidence predicate as the service-verb
    // ladder — a legacy/below-floor/pid-less daemon must never read as Succeeded just because it's
    // reachable and identity-consistent. Regression tests for the three reviewer-executed counterexamples. ----

    [Test]
    public async Task DetachedStart_exit_zero_with_missing_consent_capability_yields_AttentionSkew_not_Succeeded() {
        var cli = new FakeKcapCli { DetachedStartBehavior = _ => Task.FromResult(new ProcessResult(0, "", "", false)) };
        var factory = new RecordingExecutorFactory { Behavior = (_, _) => cli };
        var noCapabilities = MatchingEvidence() with { Capabilities = [] };
        var lane = MakeLane(factory, oneShotFactory: _ => new ScriptedObservation { Behavior = (_, _) => Task.FromResult<ObservedEvidence?>(noCapabilities) });

        var outcome = await lane.RunAsync(Req(verb: MutationVerb.DetachedStart), CancellationToken.None);

        await Assert.That(outcome).IsEqualTo(new MutationOutcome.AttentionSkew("missing_capability_consent_3"));

        await lane.DisposeAsync();
    }

    [Test]
    public async Task DetachedStart_exit_zero_with_below_floor_daemon_version_yields_AttentionSkew_not_Succeeded() {
        var cli = new FakeKcapCli { DetachedStartBehavior = _ => Task.FromResult(new ProcessResult(0, "", "", false)) };
        var factory = new RecordingExecutorFactory { Behavior = (_, _) => cli };
        var oldVersion = MatchingEvidence() with { DaemonVersion = "0.1.0" };
        var lane = MakeLane(factory, oneShotFactory: _ => new ScriptedObservation { Behavior = (_, _) => Task.FromResult<ObservedEvidence?>(oldVersion) });

        var outcome = await lane.RunAsync(Req(verb: MutationVerb.DetachedStart), CancellationToken.None);

        await Assert.That(outcome).IsEqualTo(new MutationOutcome.AttentionSkew("daemon_below_floor"));

        await lane.DisposeAsync();
    }

    [Test]
    public async Task DetachedStart_exit_zero_with_preslice_evidence_yields_AttentionSkew_not_Succeeded() {
        // IdentityConsistent is deliberately (wrongly) true here — the structural pid/instance check
        // must fire regardless of what the observation's own IdentityConsistent flag claims.
        var cli = new FakeKcapCli { DetachedStartBehavior = _ => Task.FromResult(new ProcessResult(0, "", "", false)) };
        var factory = new RecordingExecutorFactory { Behavior = (_, _) => cli };
        var preslice = new ObservedEvidence(true, ["consent/3"], "1.0.0", "https://cap.example.test", "daemon-a", null, null, true);
        var lane = MakeLane(factory, oneShotFactory: _ => new ScriptedObservation { Behavior = (_, _) => Task.FromResult<ObservedEvidence?>(preslice) });

        var outcome = await lane.RunAsync(Req(verb: MutationVerb.DetachedStart), CancellationToken.None);

        await Assert.That(outcome).IsEqualTo(new MutationOutcome.AttentionSkew("pre_slice_evidence"));

        await lane.DisposeAsync();
    }

    // ---- TEST GAP (round 1): the loop actually re-observes, not just waits out a single check ----

    [Test]
    public async Task DetachedStart_evidence_appearing_on_the_third_poll_is_Succeeded() {
        var time = new FakeTimeProvider();
        var cli = new FakeKcapCli { DetachedStartBehavior = _ => Task.FromResult(new ProcessResult(0, "", "", false)) };
        var factory = new RecordingExecutorFactory { Behavior = (_, _) => cli };
        var pollCount = 0;
        var observation = new ScriptedObservation {
            Behavior = (_, _) => {
                pollCount++;
                return Task.FromResult<ObservedEvidence?>(pollCount >= 3 ? MatchingEvidence() : null);
            },
        };
        var lane = MakeLane(factory, oneShotFactory: _ => observation, time: time);

        var task = lane.RunAsync(Req(verb: MutationVerb.DetachedStart), CancellationToken.None);
        var outcome = await Drive(task, time, TimeSpan.FromMilliseconds(500));

        await Assert.That(outcome).IsEqualTo(new MutationOutcome.Succeeded());
        await Assert.That(pollCount).IsGreaterThanOrEqualTo(3);

        await lane.DisposeAsync();
    }

    // ---- MINOR 7 (round 1): window boundary, asserted against the real DetachedConfirmWindow const ----

    [Test]
    public async Task DetachedStart_evidence_arriving_just_inside_the_confirm_window_is_Succeeded() {
        var time = new FakeTimeProvider();
        var start = time.GetUtcNow();
        var cli = new FakeKcapCli { DetachedStartBehavior = _ => Task.FromResult(new ProcessResult(0, "", "", false)) };
        var factory = new RecordingExecutorFactory { Behavior = (_, _) => cli };
        var observation = new ScriptedObservation {
            Behavior = (_, _) => Task.FromResult<ObservedEvidence?>(
                time.GetUtcNow() - start >= DaemonMutationLane.DetachedConfirmWindow - TimeSpan.FromSeconds(1)
                    ? MatchingEvidence()
                    : null),
        };
        var lane = MakeLane(factory, oneShotFactory: _ => observation, time: time);

        var task = lane.RunAsync(Req(verb: MutationVerb.DetachedStart), CancellationToken.None);
        var outcome = await Drive(task, time, TimeSpan.FromMilliseconds(500));

        await Assert.That(outcome).IsEqualTo(new MutationOutcome.Succeeded());

        await lane.DisposeAsync();
    }

    [Test]
    public async Task DetachedStart_evidence_arriving_just_past_the_confirm_window_is_UnconfirmedNoAttach() {
        var time = new FakeTimeProvider();
        var start = time.GetUtcNow();
        var cli = new FakeKcapCli { DetachedStartBehavior = _ => Task.FromResult(new ProcessResult(0, "", "", false)) };
        var factory = new RecordingExecutorFactory { Behavior = (_, _) => cli };
        var observation = new ScriptedObservation {
            // Never observed within the window (the loop stops polling once the deadline passes), so
            // this proves expiry is enforced against the real const rather than a hardcoded 10s.
            Behavior = (_, _) => Task.FromResult<ObservedEvidence?>(
                time.GetUtcNow() - start > DaemonMutationLane.DetachedConfirmWindow ? MatchingEvidence() : null),
        };
        var lane = MakeLane(factory, oneShotFactory: _ => observation, time: time);

        var task = lane.RunAsync(Req(verb: MutationVerb.DetachedStart), CancellationToken.None);
        var outcome = await Drive(task, time, TimeSpan.FromMilliseconds(500));

        await Assert.That(outcome).IsEqualTo(new MutationOutcome.UnconfirmedNoAttach());

        await lane.DisposeAsync();
    }

    // ---- MINOR 6 (round 1): a null attemptId (only reachable via direct Classify-seam injection —
    // a real DetachedStart action never produces one) must never attribute a marker, even a
    // matching null-attempt one (which belongs to a service-verb refusal, not this action). ----

    [Test, NotInParallel(nameof(DaemonLockPaths) + ".OverrideDirectoryForTesting")]
    public async Task DetachedStart_classifier_with_a_null_attemptId_never_attributes_even_a_null_attempt_marker() {
        var dir = Directory.CreateTempSubdirectory("dml-marker-").FullName;
        DaemonLockPaths.OverrideDirectoryForTesting(dir);
        var time = new FakeTimeProvider();
        try {
            PlantMarker("daemon-a", MarkerJson("daemon-a", null)); // a null-attempt marker — belongs to a service verb, never this detached attempt
            var lane = MakeLane(new RecordingExecutorFactory(), time: time);
            var executor = new FakeKcapCli();
            var observation = new ScriptedObservation(); // never full evidence

            var task = lane.Classify(
                Req(verb: MutationVerb.DetachedStart), new ProcessResult(0, "", "", false), executor, observation, null, CancellationToken.None);
            var outcome = await Drive(task, time, TimeSpan.FromMilliseconds(500));

            await Assert.That(outcome).IsEqualTo(new MutationOutcome.UnconfirmedNoAttach());
            await Assert.That(File.Exists(BootRefusalMarker.MarkerPath("daemon-a"))).IsTrue(); // untouched — TryAttribute never called

            await lane.DisposeAsync();
        } finally {
            DaemonLockPaths.OverrideDirectoryForTesting(null);
        }
    }

    // ==== Task 9b: DetachedStart boot-refusal marker attribution (real filesystem — BootRefusalMarkerTests pattern) ====

    [Test, NotInParallel(nameof(DaemonLockPaths) + ".OverrideDirectoryForTesting")]
    public async Task DetachedStart_exit_zero_with_an_attributed_marker_is_Refused_and_consumes_the_marker() {
        var dir = Directory.CreateTempSubdirectory("dml-marker-").FullName;
        DaemonLockPaths.OverrideDirectoryForTesting(dir);
        var time = new FakeTimeProvider(); // MINOR 8: no 10s wall-clock worst case if attribution ever regresses
        var cli = new FakeKcapCli();
        cli.DetachedStartBehavior = _ => {
            PlantMarker("daemon-a", MarkerJson("daemon-a", cli.LastBootAttemptId!));
            return Task.FromResult(new ProcessResult(0, "", "", false));
        };
        var factory = new RecordingExecutorFactory { Behavior = (_, _) => cli };
        var lane = MakeLane(factory, time: time); // default observation never shows full evidence
        try {
            var task = lane.RunAsync(Req(verb: MutationVerb.DetachedStart), CancellationToken.None);
            var outcome = await Drive(task, time, TimeSpan.FromMilliseconds(500));

            await Assert.That(outcome).IsEqualTo(new MutationOutcome.Refused("server_expectation_mismatch", RecoverySurface.Takeover));
            await Assert.That(File.Exists(BootRefusalMarker.MarkerPath("daemon-a"))).IsFalse();
        } finally {
            await lane.DisposeAsync();
            DaemonLockPaths.OverrideDirectoryForTesting(null);
        }
    }

    // ---- IMPORTANT 3 (round 1): marker checked BEFORE evidence each iteration — a refusing daemon
    // never attaches, so marker-first can never produce a false Refused, while evidence-first could
    // let a pre-existing same-name daemon's evidence mask a real refusal. ----

    [Test, NotInParallel(nameof(DaemonLockPaths) + ".OverrideDirectoryForTesting")]
    public async Task DetachedStart_marker_and_full_evidence_both_present_from_the_start_marker_wins() {
        var dir = Directory.CreateTempSubdirectory("dml-marker-").FullName;
        DaemonLockPaths.OverrideDirectoryForTesting(dir);
        var cli = new FakeKcapCli();
        cli.DetachedStartBehavior = _ => {
            PlantMarker("daemon-a", MarkerJson("daemon-a", cli.LastBootAttemptId!));
            return Task.FromResult(new ProcessResult(0, "", "", false));
        };
        var factory = new RecordingExecutorFactory { Behavior = (_, _) => cli };
        // Full, matching evidence from t=0 — if evidence were checked first this would resolve Succeeded.
        var observation = new ScriptedObservation { Behavior = (_, _) => Task.FromResult<ObservedEvidence?>(MatchingEvidence()) };
        var lane = MakeLane(factory, oneShotFactory: _ => observation);
        try {
            var outcome = await lane.RunAsync(Req(verb: MutationVerb.DetachedStart), CancellationToken.None);

            await Assert.That(outcome).IsEqualTo(new MutationOutcome.Refused("server_expectation_mismatch", RecoverySurface.Takeover));
            await Assert.That(File.Exists(BootRefusalMarker.MarkerPath("daemon-a"))).IsFalse(); // consumed

            await lane.DisposeAsync();
        } finally {
            DaemonLockPaths.OverrideDirectoryForTesting(null);
        }
    }

    [Test, NotInParallel(nameof(DaemonLockPaths) + ".OverrideDirectoryForTesting")]
    public async Task DetachedStart_exit_zero_with_a_foreign_marker_is_UnconfirmedNoAttach_and_retains_the_marker() {
        var dir = Directory.CreateTempSubdirectory("dml-marker-").FullName;
        DaemonLockPaths.OverrideDirectoryForTesting(dir);
        var time = new FakeTimeProvider();
        PlantMarker("daemon-a", MarkerJson("daemon-a", "foreign-attempt-id"));
        var cli = new FakeKcapCli { DetachedStartBehavior = _ => Task.FromResult(new ProcessResult(0, "", "", false)) };
        var factory = new RecordingExecutorFactory { Behavior = (_, _) => cli };
        var lane = MakeLane(factory, time: time);
        try {
            var task = lane.RunAsync(Req(verb: MutationVerb.DetachedStart), CancellationToken.None);
            var outcome = await Drive(task, time, TimeSpan.FromMilliseconds(500));

            await Assert.That(outcome).IsEqualTo(new MutationOutcome.UnconfirmedNoAttach());
            await Assert.That(File.Exists(BootRefusalMarker.MarkerPath("daemon-a"))).IsTrue();
        } finally {
            await lane.DisposeAsync();
            DaemonLockPaths.OverrideDirectoryForTesting(null);
        }
    }
}
