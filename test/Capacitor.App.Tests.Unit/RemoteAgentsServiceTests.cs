using Capacitor.App.Services;
using Capacitor.Remote.Models;

namespace Capacitor.App.Tests.Unit;

public class RemoteAgentsServiceTests {
    static AgentInstanceDto Agent(string id, string status = "Running", string daemon = "work-mac") =>
        new() { AgentId = id, Status = status, DaemonName = daemon, OwnerUserId = "u1", Vendor = "claude" };

    static async Task Eventually(Func<bool> condition, int ms = 5000) {
        var deadline = DateTime.UtcNow.AddMilliseconds(ms);
        while (!condition()) {
            if (DateTime.UtcNow > deadline) throw new TimeoutException();
            await Task.Delay(10);
        }
    }

    [Test]
    public async Task ConnectSeedsAgentsAndDaemons() {
        var lane = new FakeServerLane {
            DaemonsHandler = () => Task.FromResult<IReadOnlyList<DaemonInfo>?>([new DaemonInfo { Name = "work-mac", Connected = true }]),
        };
        using var svc = new RemoteAgentsService(lane, _ => Task.FromResult(new RemoteFetch([Agent("a1")])), TimeSpan.Zero);

        lane.StatusSubject.OnNext(new(ServerLaneState.Connected));
        await Eventually(() => svc.Agents.Count == 1);
        IReadOnlyList<DaemonInfo>? seen = null;
        using var sub = svc.Daemons.Subscribe(d => seen = d);
        await Eventually(() => seen is { Count: 1 });
    }

    [Test]
    public async Task PingRefreshesAndRemovalsPropagate() {
        var results = new Queue<AgentInstanceDto[]?>([ [Agent("a1"), Agent("a2")], [Agent("a2")] ]);
        var lane = new FakeServerLane();
        using var svc = new RemoteAgentsService(
            lane, _ => Task.FromResult(new RemoteFetch(results.Count > 0 ? results.Dequeue() : null)), TimeSpan.Zero);

        lane.StatusSubject.OnNext(new(ServerLaneState.Connected));
        await Eventually(() => svc.Agents.Count == 2);
        lane.AgentsChangedSubject.OnNext(System.Reactive.Unit.Default);
        await Eventually(() => svc.Agents.Count == 1 && svc.Agents.Lookup("a2").HasValue);
    }

    [Test]
    public async Task NullFetchLeavesCacheUntouched() {
        var first = true;
        var lane = new FakeServerLane();
        using var svc = new RemoteAgentsService(lane, _ => {
            var r = first ? new[] { Agent("a1") } : null;
            first = false;
            return Task.FromResult(new RemoteFetch(r));
        }, TimeSpan.Zero);

        lane.StatusSubject.OnNext(new(ServerLaneState.Connected));
        await Eventually(() => svc.Agents.Count == 1);
        lane.AgentsChangedSubject.OnNext(System.Reactive.Unit.Default);
        await Task.Delay(100);
        await Assert.That(svc.Agents.Count).IsEqualTo(1);
    }

    [Test]
    public async Task IdentityChangeClearsCachesEvenWhenTheNewFetchReturnsNull() {
        var returnNull = false;
        var lane = new FakeServerLane {
            DaemonsHandler = () => Task.FromResult<IReadOnlyList<DaemonInfo>?>([new DaemonInfo { Name = "work-mac", Connected = true }]),
        };
        using var svc = new RemoteAgentsService(
            lane, _ => Task.FromResult(new RemoteFetch(returnNull ? null : [Agent("a1")])), TimeSpan.Zero);

        lane.StatusSubject.OnNext(new(ServerLaneState.Connected, Subject: "u1"));
        await Eventually(() => svc.Agents.Count == 1);
        IReadOnlyList<DaemonInfo>? seen = null;
        using var sub = svc.Daemons.Subscribe(d => seen = d);
        await Eventually(() => seen is { Count: 1 });

        returnNull = true;
        lane.StatusSubject.OnNext(new(ServerLaneState.Connected, Subject: "u2"));

        await Eventually(() => svc.Agents.Count == 0);
        await Eventually(() => seen is { Count: 0 });
    }

    // Pins the P1 fix: a fetch started under u1 that is still gated when u2's identity-change
    // clear lands must never repopulate the cache the clear just emptied.
    [Test]
    public async Task StaleFetchCompletingAfterIdentityChangeIsDiscarded() {
        var callCount = 0;
        var gate = new TaskCompletionSource<RemoteFetch>(TaskCreationOptions.RunContinuationsAsynchronously);
        var lane = new FakeServerLane();
        using var svc = new RemoteAgentsService(lane, async _ => {
            var n = Interlocked.Increment(ref callCount);
            return n == 1 ? await gate.Task : new RemoteFetch([Agent("a2")]);
        }, TimeSpan.Zero);

        lane.StatusSubject.OnNext(new(ServerLaneState.Connected, Subject: "u1")); // fetch #1 in flight
        await Eventually(() => Volatile.Read(ref callCount) == 1);

        lane.StatusSubject.OnNext(new(ServerLaneState.Connected, Subject: "u2")); // clear + generation bump

        gate.SetResult(new RemoteFetch([Agent("a1")])); // u1's stale rows land after the identity change

        await Task.Delay(150); // give the stale completion a chance to (wrongly) publish
        await Assert.That(svc.Agents.Count).IsEqualTo(0);

        lane.AgentsChangedSubject.OnNext(System.Reactive.Unit.Default); // a fresh fetch under u2
        await Eventually(() => svc.Agents.Count == 1 && svc.Agents.Lookup("a2").HasValue);
    }

    [Test]
    public async Task ReconnectWithTheSameSubjectDoesNotClearTheCache() {
        var lane = new FakeServerLane();
        using var svc = new RemoteAgentsService(lane, _ => Task.FromResult(new RemoteFetch([Agent("a1")])), TimeSpan.Zero);

        lane.StatusSubject.OnNext(new(ServerLaneState.Connected, Subject: "u1"));
        await Eventually(() => svc.Agents.Count == 1);

        lane.StatusSubject.OnNext(new(ServerLaneState.Connected, Subject: "u1"));
        await Task.Delay(100);
        await Assert.That(svc.Agents.Count).IsEqualTo(1);
    }

    // Drives the coalescing case through repeated Connected/Retrying status edges rather than
    // AgentInstancesChanged pings: the latter go through Throttle, whose real scheduler makes
    // whether a rapid burst reaches RefreshAgentsAsync as one or several attempts a timing race
    // — the same race either way it lands. Status flows through no Throttle, so each edge below
    // deterministically re-enters RefreshAgentsAsync on the calling thread while call #1 still
    // holds the busy flag, exercising the exact contended path the lock-guarded busy/rerun pair
    // exists for.
    [Test]
    public async Task OverlappingRefreshesCoalesceIntoOneTrailingRun() {
        var callCount = 0;
        var gate = new TaskCompletionSource<RemoteFetch>(TaskCreationOptions.RunContinuationsAsynchronously);
        var trailing = new RemoteFetch([Agent("a2")]);
        var lane = new FakeServerLane();
        using var svc = new RemoteAgentsService(lane, async _ => {
            var n = Interlocked.Increment(ref callCount);
            return n == 1 ? await gate.Task : trailing;
        }, TimeSpan.Zero);

        lane.StatusSubject.OnNext(new(ServerLaneState.Connected)); // call #1 starts, blocks on gate

        // Six edges while call #1 is still in flight: every one must coalesce into the same
        // pending rerun instead of queuing its own sequential re-fetch.
        for (var i = 0; i < 3; i++) {
            lane.StatusSubject.OnNext(new(ServerLaneState.Retrying));
            lane.StatusSubject.OnNext(new(ServerLaneState.Connected));
        }

        gate.SetResult(new RemoteFetch([Agent("a1")]));

        await Eventually(() => svc.Agents.Lookup("a2").HasValue);
        await Assert.That(Volatile.Read(ref callCount)).IsEqualTo(2);
    }

    // Pins finding 4a: an Unauthorized fetch must invoke onUnauthorized, once per occurrence,
    // without disturbing whatever rows the cache already holds.
    [Test]
    public async Task UnauthorizedFetchInvokesTheCallbackAndLeavesTheCacheUntouched() {
        var unauthorizedCalls = 0;
        var lane = new FakeServerLane();
        var first = true;
        using var svc = new RemoteAgentsService(
            lane,
            _ => {
                var result = first ? new RemoteFetch([Agent("a1")]) : new RemoteFetch(null, Unauthorized: true);
                first = false;
                return Task.FromResult(result);
            },
            TimeSpan.Zero, onUnauthorized: () => Interlocked.Increment(ref unauthorizedCalls));

        lane.StatusSubject.OnNext(new(ServerLaneState.Connected));
        await Eventually(() => svc.Agents.Count == 1);

        lane.AgentsChangedSubject.OnNext(System.Reactive.Unit.Default);
        await Eventually(() => Volatile.Read(ref unauthorizedCalls) == 1);
        await Assert.That(svc.Agents.Count).IsEqualTo(1);
    }
}
