using System.Runtime.CompilerServices;
using System.Threading.Channels;
using Capacitor.App.Services;
using Capacitor.Cli.Core.LocalIpc;
using TUnit.Assertions.Enums;

namespace Capacitor.App.Tests.Unit;

/// Plain TUnit tests — no Avalonia session, nothing touches Avalonia/Rx globals. Each test
/// scripts LocalControlEvent through its own Script/DaemonClientService pair, so tests do not
/// need [NotInParallel]. They ARE concurrency-sensitive (single-flight restart, disposal
/// races), hence the "run the class 3x" step in the task brief.
public class DaemonClientServiceTests {
    /// Feeds a scripted event stream through a shared channel: each DaemonClientService
    /// enumeration ("attach attempt") is one Run() call reading from the SAME channel, so a
    /// manual restart resumes the same event source rather than starting a fresh one — the
    /// test writes whatever the next enumeration should see (including a fresh `Connecting`)
    /// before triggering the restart. PeakLiveEnumerations is updated INSIDE Run() itself (not
    /// via external polling), so a single-flight violation of any duration is caught for sure.
    sealed class Script {
        public readonly Channel<LocalControlEvent> Events = Channel.CreateUnbounded<LocalControlEvent>();
        public int LiveEnumerations;
        public int PeakLiveEnumerations;
        public int StartCount;

        public async IAsyncEnumerable<LocalControlEvent> Run([EnumeratorCancellation] CancellationToken ct) {
            Interlocked.Increment(ref StartCount);
            var live = Interlocked.Increment(ref LiveEnumerations);
            InterlockedMax(ref PeakLiveEnumerations, live);
            try {
                await foreach (var e in Events.Reader.ReadAllAsync(ct)) yield return e;
            } finally {
                Interlocked.Decrement(ref LiveEnumerations);
            }
        }

        public void Feed(LocalControlEvent e) {
            if (!Events.Writer.TryWrite(e)) throw new InvalidOperationException("unbounded channel write must never fail");
        }

        static void InterlockedMax(ref int target, int value) {
            int initial, computed;
            do {
                initial = target;
                computed = Math.Max(initial, value);
            } while (Interlocked.CompareExchange(ref target, computed, initial) != initial);
        }
    }

    sealed class FakeProcessRunner : IProcessRunner {
        public string? SeenFileName;
        public string[]? SeenArgs;
        public Func<CancellationToken, Task<(int ExitCode, string Stderr)>>? Behavior;

        public Task<(int ExitCode, string Stderr)> RunAsync(string fileName, string[] args, CancellationToken ct) {
            SeenFileName = fileName;
            SeenArgs     = args;
            return (Behavior ?? (_ => Task.FromResult((0, ""))))(ct);
        }
    }

    static DaemonStatusDto Snap(string daemon, params string[] ids) {
        var agents = ids.Select(id => new AgentStatusDto(
            Id: id, Kind: "agent", Vendor: "claude", RepoPath: null, Status: "Running",
            FlowRunId: null, FlowRole: null, Requester: null, CreatedAt: DateTime.UtcNow, Model: null
        )).ToList();
        return new DaemonStatusDto(
            new DaemonInfoDto(daemon, "1.0.0", "http://localhost:9999", "connected", MaxAgents: 10, ActiveAgents: agents.Count),
            agents);
    }

    static async Task WaitUntilAsync(Func<bool> condition, TimeSpan? timeout = null, string what = "condition") {
        var deadline = DateTime.UtcNow + (timeout ?? TimeSpan.FromSeconds(5));
        while (!condition()) {
            if (DateTime.UtcNow > deadline) throw new TimeoutException($"Timed out waiting for: {what}");
            await Task.Delay(10);
        }
    }

    /// Polls the cache to its expected key set rather than gating on an unrelated Rx event —
    /// EditDiff runs on the background pump thread and Apply() does not always follow it with
    /// an event a test can synchronize on (a plain Status snapshot never republishes AttachStatus).
    static Task WaitForAgentsAsync(IDaemonClientService svc, params string[] expectedSortedIds) =>
        WaitUntilAsync(
            () => svc.Agents.Keys.OrderBy(k => k, StringComparer.Ordinal).SequenceEqual(expectedSortedIds, StringComparer.Ordinal),
            what: $"Agents cache to equal [{string.Join(", ", expectedSortedIds)}]");

    [Test]
    public async Task Initial_status_replays_connecting() {
        var script = new Script();
        await using var svc = new DaemonClientService("daemon-a", script.Run, new FakeProcessRunner(), "kcap");
        svc.Start();
        await Task.Delay(20); // let the loop actually start before subscribing "late"

        var seen = new List<AttachStatus>();
        using var sub = svc.Status.Subscribe(seen.Add);

        await Assert.That(seen).Count().IsEqualTo(1);
        await Assert.That(seen[0]).IsEqualTo(new AttachStatus(AttachState.Connecting, null, null));
    }

    [Test]
    public async Task Event_mapping_is_complete_and_atomic() {
        var script = new Script();
        await using var svc = new DaemonClientService("daemon-a", script.Run, new FakeProcessRunner(), "kcap");
        svc.Start();

        var seen = new List<AttachStatus>();
        using var sub = svc.Status.Subscribe(seen.Add);
        await WaitUntilAsync(() => seen.Count >= 1, what: "initial Connecting status"); // initial Connecting

        var capsA = new List<string> { "status/1" };
        var snapA = Snap("daemon-a", "a1");
        script.Feed(new LocalControlEvent.Connected(capsA, snapA));
        await WaitUntilAsync(() => seen.Count >= 2, what: "Connected status after Connected event");

        script.Feed(new LocalControlEvent.Unreachable("daemon_unreachable"));
        await WaitUntilAsync(() => seen.Count >= 3, what: "Unreachable status after Unreachable event");

        // Restart begins a fresh enumeration on the SAME script channel; the test feeds the
        // Connecting event the fresh enumeration should see before it starts reading.
        script.Feed(new LocalControlEvent.Connecting());
        await svc.RestartLoopAsync();
        await WaitUntilAsync(() => seen.Count >= 4, what: "Connecting status after restart");

        await Assert.That(seen).Count().IsEqualTo(4);
        await Assert.That(seen[0]).IsEqualTo(new AttachStatus(AttachState.Connecting, null, null));
        await Assert.That(seen[1]).IsEqualTo(new AttachStatus(AttachState.Connected, null, capsA));
        await Assert.That(seen[2]).IsEqualTo(new AttachStatus(AttachState.Unreachable, "daemon_unreachable", null));
        await Assert.That(seen[3]).IsEqualTo(new AttachStatus(AttachState.Connecting, null, null));
    }

    [Test]
    public async Task No_stale_reconnect() {
        var script = new Script();
        await using var svc = new DaemonClientService("daemon-a", script.Run, new FakeProcessRunner(), "kcap");
        svc.Start();

        // Captures (status, latest Snapshots value, current Agents keys) all read SYNCHRONOUSLY
        // from inside the Status subscription callback, i.e. at the exact moment Apply()
        // publishes AttachStatus — the point after which Snapshots/EditDiff are pinned to have
        // already run (no-stale pin, spec §5). Sampling Agents.Keys from OUTSIDE this callback
        // (e.g. after a separate poll) would not prove the ordering — only this synchronous
        // read does, and it's cheap enough to take on every status transition.
        var pairs = new List<(AttachState Status, DaemonStatusDto? LatestSnapshot, string[] AgentKeys)>();
        DaemonStatusDto? latestSnapshot = null;
        using var subSnap = svc.Snapshots.Subscribe(s => latestSnapshot = s);
        using var subStatus = svc.Status.Subscribe(s => pairs.Add((
            s.State,
            latestSnapshot,
            svc.Agents.Keys.OrderBy(k => k, StringComparer.Ordinal).ToArray())));

        await WaitUntilAsync(() => pairs.Count >= 1, what: "initial Connecting pair"); // initial Connecting

        var capsA = new List<string> { "status/1" };
        var snapA = Snap("daemon-a", "a1");
        script.Feed(new LocalControlEvent.Connected(capsA, snapA));
        await WaitUntilAsync(() => pairs.Count >= 2, what: "Connected pair after first Connected event");

        // First connect: cache already holds exactly A's keys at the Connected moment.
        var firstConnectedMoment = pairs[1];
        await Assert.That(firstConnectedMoment.Status).IsEqualTo(AttachState.Connected);
        await Assert.That(firstConnectedMoment.AgentKeys).IsEquivalentTo(["a1"], CollectionOrdering.Matching);

        script.Feed(new LocalControlEvent.Unreachable("daemon_unreachable"));
        await WaitUntilAsync(() => pairs.Count >= 3, what: "Unreachable pair after Unreachable event");

        var capsB = new List<string> { "status/1" };
        var snapB = Snap("daemon-a", "b1");
        script.Feed(new LocalControlEvent.Connected(capsB, snapB));
        await WaitUntilAsync(() => pairs.Count >= 4, what: "Connected pair after second Connected event");

        // At the moment status flips to Connected the second time, BOTH the snapshot AND the
        // cache observed alongside it must ALREADY be B's — never a stale A snapshot/keys, and
        // never a "connected but still showing A" moment.
        var connectedMoment = pairs[3];
        await Assert.That(connectedMoment.Status).IsEqualTo(AttachState.Connected);
        await Assert.That(connectedMoment.LatestSnapshot).IsEqualTo(snapB);
        await Assert.That(connectedMoment.AgentKeys).IsEquivalentTo(["b1"], CollectionOrdering.Matching);
    }

    [Test]
    public async Task Pump_fault_does_not_brick_restart_or_disposal() {
        // A scripted event stream whose enumeration throws mid-stream — representative of
        // Apply() throwing from a downstream Rx/DynamicData observer (the same PumpAsync
        // catch site sees both, since Apply() runs inside the awaited foreach body). Before the
        // fix, this would fault `_loop` forever: every later RestartLoopAsync would rethrow at
        // `await _loop` without ever reaching reassignment, and DisposeAsync would throw at its
        // `await _loop`, skipping subject/cache disposal.
        var faultCount = 0;
        var script = new Script();

        async IAsyncEnumerable<LocalControlEvent> FaultingThenNormalRun([EnumeratorCancellation] CancellationToken ct) {
            if (Interlocked.Increment(ref faultCount) == 1) {
                Interlocked.Increment(ref script.LiveEnumerations);
                Interlocked.Increment(ref script.StartCount);
                try {
                    yield return new LocalControlEvent.Connecting();
                    await Task.Yield();
                    throw new InvalidOperationException("simulated pump fault");
                } finally {
                    Interlocked.Decrement(ref script.LiveEnumerations);
                }
            } else {
                await foreach (var e in script.Run(ct)) yield return e;
            }
        }

        // Not `await using` — DisposeAsync is called explicitly below as the assertion under
        // test, so a second implicit call at scope exit is avoided rather than relied upon to
        // be idempotent.
        var svc = new DaemonClientService("daemon-a", FaultingThenNormalRun, new FakeProcessRunner(), "kcap");
        svc.Start();

        // Let the faulting first enumeration run to completion (fault contained, no crash).
        await WaitUntilAsync(() => faultCount >= 1, what: "faulting enumeration to run");
        await WaitUntilAsync(() => script.LiveEnumerations == 0, TimeSpan.FromSeconds(5), what: "faulted enumeration to end");

        // RestartLoopAsync must still work after a faulted pump — this is the regression pin:
        // it must not rethrow, and it must reach a fresh, live enumeration.
        await svc.RestartLoopAsync();
        await WaitUntilAsync(() => script.LiveEnumerations >= 1, TimeSpan.FromSeconds(5), what: "fresh enumeration after restart");

        var statuses = new List<AttachStatus>();
        using var sub = svc.Status.Subscribe(statuses.Add);
        script.Feed(new LocalControlEvent.Connecting());
        await WaitUntilAsync(() => statuses.Count >= 1, what: "Connecting status from the fresh enumeration");
        await Assert.That(statuses[^1]).IsEqualTo(new AttachStatus(AttachState.Connecting, null, null));

        // DisposeAsync must also complete cleanly (not throw, not skip cleanup) even though a
        // fault occurred earlier in this service's lifetime.
        await svc.DisposeAsync();
        await Assert.That(script.LiveEnumerations).IsEqualTo(0);
    }

    [Test]
    public async Task Snapshots_is_empty_until_first_and_cache_diffs() {
        var script = new Script();
        await using var svc = new DaemonClientService("daemon-a", script.Run, new FakeProcessRunner(), "kcap");
        svc.Start();

        var snapshots = new List<DaemonStatusDto>();
        using var sub = svc.Snapshots.Subscribe(snapshots.Add);
        await Task.Delay(50); // give the (empty) loop a chance to publish anything it might
        await Assert.That(snapshots).IsEmpty();

        var caps = new List<string> { "status/1" };
        var snap1 = Snap("daemon-a", "a1", "a2");
        script.Feed(new LocalControlEvent.Connected(caps, snap1));
        await WaitUntilAsync(() => snapshots.Count >= 1, what: "first snapshot");
        // Apply() publishes Snapshots BEFORE running EditDiff, so waiting on the snapshot count
        // alone races the cache write — poll the cache to its expected state directly instead.
        await WaitForAgentsAsync(svc, "a1", "a2");
        await Assert.That(svc.Agents.Keys.OrderBy(k => k, StringComparer.Ordinal).ToArray())
            .IsEquivalentTo(["a1", "a2"], CollectionOrdering.Matching);

        var snap2 = Snap("daemon-a", "a2", "a3");
        script.Feed(new LocalControlEvent.Status(snap2));
        await WaitUntilAsync(() => snapshots.Count >= 2, what: "second snapshot");
        await WaitForAgentsAsync(svc, "a2", "a3");
        await Assert.That(svc.Agents.Keys.OrderBy(k => k, StringComparer.Ordinal).ToArray())
            .IsEquivalentTo(["a2", "a3"], CollectionOrdering.Matching);
    }

    [Test]
    public async Task Cache_retained_on_disconnect() {
        var script = new Script();
        await using var svc = new DaemonClientService("daemon-a", script.Run, new FakeProcessRunner(), "kcap");
        svc.Start();

        var statuses = new List<AttachStatus>();
        using var sub = svc.Status.Subscribe(statuses.Add);
        await WaitUntilAsync(() => statuses.Count >= 1, what: "initial Connecting status");

        var caps = new List<string> { "status/1" };
        var snap = Snap("daemon-a", "a1", "a2");
        script.Feed(new LocalControlEvent.Connected(caps, snap));
        await WaitUntilAsync(() => statuses.Count >= 2, what: "Connected status");
        await WaitForAgentsAsync(svc, "a1", "a2");
        await Assert.That(svc.Agents.Keys.OrderBy(k => k, StringComparer.Ordinal).ToArray())
            .IsEquivalentTo(["a1", "a2"], CollectionOrdering.Matching);

        script.Feed(new LocalControlEvent.Unreachable("daemon_unreachable"));
        await WaitUntilAsync(() => statuses.Count >= 3, what: "Unreachable status");

        await Assert.That(svc.Agents.Keys.OrderBy(k => k, StringComparer.Ordinal).ToArray())
            .IsEquivalentTo(["a1", "a2"], CollectionOrdering.Matching);
    }

    [Test]
    public async Task RestartLoop_is_single_flight() {
        var script = new Script();
        await using var svc = new DaemonClientService("daemon-a", script.Run, new FakeProcessRunner(), "kcap");
        svc.Start();
        await WaitUntilAsync(() => script.LiveEnumerations >= 1, what: "first enumeration to start");

        for (var i = 0; i < 10; i++) {
            var t1 = svc.RestartLoopAsync();
            var t2 = svc.RestartLoopAsync();
            await Task.WhenAll(t1, t2);
            await Assert.That(t1.IsCompletedSuccessfully).IsTrue();
            await Assert.That(t2.IsCompletedSuccessfully).IsTrue();
        }

        await Assert.That(script.PeakLiveEnumerations).IsLessThanOrEqualTo(1);
        // The new pump starts via Task.Run — poll for the steady state instead of asserting
        // immediately (the restart's await covers the OLD loop's completion, not the new
        // loop's scheduling; Windows CI exposed the gap).
        await WaitUntilAsync(() => script.LiveEnumerations == 1, what: "steady-state single enumeration after restarts");
    }

    [Test]
    public async Task StartDaemon_success_argv_and_restart_kick() {
        var script = new Script();
        var runner = new FakeProcessRunner { Behavior = _ => Task.FromResult((0, "")) };
        await using var svc = new DaemonClientService("daemon-a", script.Run, runner, "/opt/kcap");
        svc.Start();
        await WaitUntilAsync(() => script.LiveEnumerations >= 1, what: "enumeration to start");
        var startCountBefore = script.StartCount;

        var result = await svc.StartDaemonAsync(CancellationToken.None);

        await Assert.That(result.Ok).IsTrue();
        await Assert.That(result.Message).IsNull();
        await Assert.That(runner.SeenFileName).IsEqualTo("/opt/kcap");
        await Assert.That(runner.SeenArgs)
            .IsEquivalentTo(["daemon", "start", "-d", "--name", "daemon-a"], CollectionOrdering.Matching);

        // Exit 0 immediately kicks RestartLoopAsync — a NEW enumeration begins without a
        // manual restart call and without waiting for backoff (StartCount strictly increases,
        // not just LiveEnumerations dipping and recovering, which would be racy to observe).
        await WaitUntilAsync(() => script.StartCount > startCountBefore, TimeSpan.FromSeconds(2), what: "restart kick after successful start");
        await WaitUntilAsync(() => script.LiveEnumerations == 1, what: "single live enumeration after restart kick");
        await Assert.That(script.PeakLiveEnumerations).IsLessThanOrEqualTo(1);
    }

    [Test]
    public async Task StartDaemon_failures_produce_messages() {
        var script = new Script();

        var throwingRunner = new FakeProcessRunner {
            Behavior = _ => throw new InvalidOperationException("spawn failed")
        };
        await using (var svc1 = new DaemonClientService("daemon-a", script.Run, throwingRunner, "kcap")) {
            var r1 = await svc1.StartDaemonAsync(CancellationToken.None);
            await Assert.That(r1.Ok).IsFalse();
            await Assert.That(string.IsNullOrWhiteSpace(r1.Message)).IsFalse();
        }

        var nonZeroWithStderr = new FakeProcessRunner {
            Behavior = _ => Task.FromResult((1, "boom: could not bind socket"))
        };
        await using (var svc2 = new DaemonClientService("daemon-b", script.Run, nonZeroWithStderr, "kcap")) {
            var r2 = await svc2.StartDaemonAsync(CancellationToken.None);
            await Assert.That(r2.Ok).IsFalse();
            await Assert.That(string.IsNullOrWhiteSpace(r2.Message)).IsFalse();
        }

        var nonZeroEmptyStderr = new FakeProcessRunner {
            Behavior = _ => Task.FromResult((1, ""))
        };
        await using (var svc3 = new DaemonClientService("daemon-c", script.Run, nonZeroEmptyStderr, "kcap")) {
            var r3 = await svc3.StartDaemonAsync(CancellationToken.None);
            await Assert.That(r3.Ok).IsFalse();
            await Assert.That(string.IsNullOrWhiteSpace(r3.Message)).IsFalse();
        }
    }

    [Test]
    public async Task Dispose_ends_the_loop_and_disposes_subjects() {
        var script = new Script();
        var svc = new DaemonClientService("daemon-a", script.Run, new FakeProcessRunner(), "kcap");
        svc.Start();
        await WaitUntilAsync(() => script.LiveEnumerations >= 1, what: "enumeration to start");

        var statusValues = new List<AttachStatus>();
        var snapshotValues = new List<DaemonStatusDto>();
        using var subStatus = svc.Status.Subscribe(statusValues.Add);
        using var subSnap = svc.Snapshots.Subscribe(snapshotValues.Add);
        var statusCountBeforeDispose = statusValues.Count;

        await svc.DisposeAsync();

        await Assert.That(script.LiveEnumerations).IsEqualTo(0);

        // Subsequent RestartLoopAsync is a no-op.
        await svc.RestartLoopAsync();
        await Assert.That(script.LiveEnumerations).IsEqualTo(0);

        // Status/Snapshots publish nothing after disposal.
        var caps = new List<string> { "status/1" };
        script.Feed(new LocalControlEvent.Connected(caps, Snap("daemon-a", "a1")));
        await Task.Delay(50);
        await Assert.That(statusValues.Count).IsEqualTo(statusCountBeforeDispose);
        await Assert.That(snapshotValues).IsEmpty();
    }

    [Test]
    public async Task Shutdown_during_start_abandons_the_wait() {
        var script = new Script();
        var startCts = new CancellationTokenSource();
        var runnerEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var runner = new FakeProcessRunner {
            Behavior = async ct => {
                runnerEntered.TrySetResult();
                var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                await using var reg = ct.Register(() => tcs.TrySetResult());
                await tcs.Task; // blocks until ITS ct (the one StartDaemonAsync was called with) fires
                throw new OperationCanceledException(ct);
            }
        };

        var svc = new DaemonClientService("daemon-a", script.Run, runner, "kcap");
        svc.Start();
        await WaitUntilAsync(() => script.LiveEnumerations >= 1, what: "enumeration to start");

        var startTask = svc.StartDaemonAsync(startCts.Token);
        await runnerEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));

        // App shutdown proceeds without waiting on the outstanding start — the start's own
        // caller-supplied ct is what abandons ITS wait, not the service's dispose.
        var disposeTask = svc.DisposeAsync().AsTask();
        var completed = await Task.WhenAny(disposeTask, Task.Delay(TimeSpan.FromSeconds(5)));
        await Assert.That(completed).IsEqualTo(disposeTask);

        startCts.Cancel();
        await Assert.ThrowsAsync<OperationCanceledException>(() => startTask);
    }
}
