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

    // The lane must route every mutation through the executor factory's IKcapCli, never the raw runner, in 9a.
    sealed class UnusedProcessRunner : IProcessRunner {
        public Task<ProcessResult> RunAsync(string fileName, string[] args, RunOptions options, CancellationToken ct) =>
            throw new InvalidOperationException("DaemonMutationLane must not call IProcessRunner directly.");
    }

    sealed class FakeObservation : IDaemonObservation {
        public Task<ObservedEvidence?> ObserveAsync(MutationRequest request, CancellationToken ct) => Task.FromResult<ObservedEvidence?>(null);
    }

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
            Func<string?>? cliOverride = null, ILoginShellProbe? shellProbe = null) =>
        new(
            new UnusedProcessRunner(),
            shellProbe ?? new FakeLoginShellProbe { KcapPathBehavior = _ => Task.FromResult<string?>(null) },
            channel ?? new OutcomeChannel(),
            cliOverride ?? (() => "/opt/kcap/bin/kcap"),
            factory.Invoke,
            _ => new FakeObservation(),
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
    public async Task Different_queued_request_gets_its_own_fresh_probe_only_after_the_first_admits_it() {
        var requestA = Req(daemonName: "daemon-a");
        var requestB = Req(daemonName: "daemon-b");
        var gateA = new TaskCompletionSource<string?>();
        var cliA = new FakeKcapCli { VersionBehavior = _ => gateA.Task };
        var cliB = new FakeKcapCli();
        var factory = new RecordingExecutorFactory { Behavior = (req, _) => req.DaemonName == "daemon-a" ? cliA : cliB };
        var lane = MakeLane(factory);

        var t1 = lane.RunAsync(requestA, CancellationToken.None);
        var t2 = lane.RunAsync(requestB, CancellationToken.None);

        // B is a DIFFERENT request: it queues, and must not probe while A is still in flight.
        await Assert.That(factory.Calls.Count).IsEqualTo(1);
        await Assert.That(cliB.VersionCallCount).IsEqualTo(0);

        gateA.SetResult("9.9.9");
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
        var lane = MakeLane(
            factory, cliOverride: () => null,
            shellProbe: new FakeLoginShellProbe { KcapPathBehavior = _ => Task.FromResult<string?>(null) });

        var outcome = await lane.RunAsync(Req(), CancellationToken.None);

        await Assert.That(outcome).IsEqualTo(new MutationOutcome.Refused("cli_not_found", RecoverySurface.Attention));
        await Assert.That(factory.Calls.Count).IsEqualTo(0);

        await lane.DisposeAsync();
    }

    [Test]
    public async Task Below_floor_version_refuses_without_a_mutation_call() {
        var cli = new FakeKcapCli { VersionBehavior = _ => Task.FromResult<string?>("0.1.0") };
        var factory = new RecordingExecutorFactory { Behavior = (_, _) => cli };
        var lane = MakeLane(factory);

        var outcome = await lane.RunAsync(Req(), CancellationToken.None);

        await Assert.That(outcome).IsEqualTo(new MutationOutcome.Refused("cli_below_floor", RecoverySurface.Attention));
        await Assert.That(cli.VersionCallCount).IsEqualTo(1);
        await Assert.That(cli.StartVerifiedCallCount).IsEqualTo(0);

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
        var lane = new DaemonMutationLane(
            new UnusedProcessRunner(),
            new FakeLoginShellProbe { KcapPathBehavior = _ => Task.FromResult<string?>(null) },
            new OutcomeChannel(),
            () => "/opt/kcap/bin/kcap",
            factory.Invoke,
            _ => { oneShotCalls++; return new FakeObservation(); },
            TimeProvider.System) {
            Classify = (request, result, executor, observation, attemptId, ct) => {
                seen = observation;
                return Task.FromResult<MutationOutcome>(new MutationOutcome.Succeeded());
            },
        };

        await lane.RunAsync(Req(), CancellationToken.None);

        await Assert.That(oneShotCalls).IsEqualTo(1);
        await Assert.That(seen).IsTypeOf<FakeObservation>();

        await lane.DisposeAsync();
    }
}
