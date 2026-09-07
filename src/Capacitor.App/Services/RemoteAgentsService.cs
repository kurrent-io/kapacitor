using System.Net.Http.Json;
using System.Reactive.Linq;
using System.Reactive.Subjects;
using Capacitor.Cli.Core;
using Capacitor.Cli.Core.Config;
using Capacitor.Remote.Models;
using DynamicData;

namespace Capacitor.App.Services;

public interface IRemoteAgentsService {
    /// Keyed by AgentId. Retained across lane loss — staleness is presentation.
    IObservableCache<AgentInstanceDto, string> Agents { get; }
    /// Replay-1, seeded with an empty list.
    IObservable<IReadOnlyList<DaemonInfo>> Daemons { get; }
}

/// An agents fetch's outcome: Rows is null on any failure (including Unauthorized), so a caller
/// that only cares about data can ignore Unauthorized entirely; Unauthorized additionally
/// distinguishes the one failure kind that needs to reach the sign-in surface rather than being
/// swallowed as ordinary lane loss.
public sealed record RemoteFetch(AgentInstanceDto[]? Rows, bool Unauthorized = false);

/// Remote registry caches: seeded on lane Connected, refreshed on the org-wide pings. A failed
/// fetch returns null Rows and leaves the caches as they were — lane loss is data — except
/// Unauthorized, which additionally invokes onUnauthorized so the sign-in surface hears about it.
public sealed class RemoteAgentsService : IRemoteAgentsService, IDisposable {
    readonly SourceCache<AgentInstanceDto, string> _agents = new(a => a.AgentId);
    readonly BehaviorSubject<IReadOnlyList<DaemonInfo>> _daemons = new([]);
    readonly IDisposable _subscriptions;
    // Guards _generation/_lastSubject and each refresh kind's busy/rerun pair. _generation bumps
    // on every Connected status carrying a subject, not only on a subject change, so any fetch
    // issued before the latest connect is stale for both the cache-publish and the onUnauthorized
    // check below.
    // Admission (busy check / set rerun) and completion (rerun check / clear busy) share this
    // same critical section, so a losing admission can never land between a drain loop reading
    // rerun false and clearing busy.
    readonly Lock _lock = new();
    int _generation;
    string? _lastSubject;
    // The lane's own Epoch off the latest Connected status seen — captured per fetch and handed
    // to onUnauthorized, so ServerConnectionService.ParkSignedOut(int) can re-validate a delayed
    // park decision against the lane's CURRENT epoch rather than trusting this generation check
    // alone, whose lock releases before the callback runs.
    int _lastLaneEpoch;
    bool _agentsBusy;
    bool _agentsRerun;
    bool _daemonsBusy;
    bool _daemonsRerun;
    readonly Action<int>? _onUnauthorized;

    public RemoteAgentsService(
            IServerLane lane, Func<CancellationToken, Task<RemoteFetch>> fetchAgents,
            TimeSpan? debounce = null, Action<int>? onUnauthorized = null) {
        _onUnauthorized = onUnauthorized;
        var wait = debounce ?? TimeSpan.FromMilliseconds(250);
        var connected = lane.Status
            .Select(s => s.State == ServerLaneState.Connected)
            .DistinctUntilChanged()
            .Where(c => c)
            .Select(_ => System.Reactive.Unit.Default);

        // Subscribed before the refresh triggers below (same lane.Status), so on a Connected
        // status this runs first: nothing seeded under one identity survives into another, even
        // when the refresh that follows fetches null and would otherwise leave stale rows in
        // place. Caches clear only when the token subject changes; every Connected carrying a
        // subject advances the generation, so an older fetch can neither publish nor report an
        // auth failure.
        var identityChange = lane.Status
            .Where(s => s.State == ServerLaneState.Connected)
            .Subscribe(s => {
                lock (_lock) {
                    if (s.Subject is not null) {
                        if (_lastSubject is not null && _lastSubject != s.Subject) {
                            _agents.Clear();
                            _daemons.OnNext([]);
                        }
                        _generation++;
                        _lastSubject = s.Subject;
                    }
                    _lastLaneEpoch = s.Epoch;
                }
            });

        // Merge, not Concat: triggers must reach the refresh methods concurrently so a busy
        // refresh's admission check can actually observe contention and coalesce.
        var refreshAgents = connected.Merge(lane.AgentInstancesChanged.Throttle(wait))
            .Select(_ => Observable.FromAsync(async () => await RefreshAgentsAsync(fetchAgents)))
            .Merge()
            .Subscribe();
        var refreshDaemons = connected.Merge(lane.DaemonsChanged.Throttle(wait))
            .Select(_ => Observable.FromAsync(async () => await RefreshDaemonsAsync(lane)))
            .Merge()
            .Subscribe();
        _subscriptions = new System.Reactive.Disposables.CompositeDisposable(identityChange, refreshAgents, refreshDaemons);
    }

    public IObservableCache<AgentInstanceDto, string> Agents => _agents.AsObservableCache();
    public IObservable<IReadOnlyList<DaemonInfo>> Daemons => _daemons.AsObservable();

    async Task RefreshAgentsAsync(Func<CancellationToken, Task<RemoteFetch>> fetch) {
        lock (_lock) {
            if (_agentsBusy) { _agentsRerun = true; return; }
            _agentsBusy = true;
        }
        while (true) {
            int generation, laneEpoch;
            lock (_lock) { generation = _generation; laneEpoch = _lastLaneEpoch; }
            RemoteFetch? result = null;
            try {
                result = await fetch(CancellationToken.None).ConfigureAwait(false);
            } catch (Exception) {
                // Data-plane refresh: a throw here is a missed refresh, never an app fault.
            }
            var notifyUnauthorized = false;
            bool rerun;
            lock (_lock) {
                // A completion from before the latest connect must never repopulate the cache or
                // report an auth failure a fresher connect may already have superseded — checked
                // under the same lock a connect's generation bump takes.
                if (generation == _generation) {
                    if (result?.Rows is { } rows)
                        _agents.EditDiff(rows, EqualityComparer<AgentInstanceDto>.Default);
                    if (result is { Unauthorized: true }) notifyUnauthorized = true;
                }
                rerun = _agentsRerun;
                if (rerun) _agentsRerun = false;
                else _agentsBusy = false;
            }
            // Invoked outside this lock (arbitrary caller code — ServerConnectionService.
            // ParkSignedOut in production), carrying the lane epoch this fetch started under so
            // the lane itself re-validates the park at the moment it actually runs, rather than
            // trusting a decision this method already committed to before releasing its own lock.
            if (notifyUnauthorized) _onUnauthorized?.Invoke(laneEpoch);
            if (!rerun) return;
        }
    }

    async Task RefreshDaemonsAsync(IServerLane lane) {
        lock (_lock) {
            if (_daemonsBusy) { _daemonsRerun = true; return; }
            _daemonsBusy = true;
        }
        while (true) {
            int generation;
            lock (_lock) generation = _generation;
            IReadOnlyList<DaemonInfo>? result = null;
            try {
                result = await lane.GetConnectedDaemonsAsync(CancellationToken.None).ConfigureAwait(false);
            } catch (Exception) {
            }
            lock (_lock) {
                if (result is not null && generation == _generation)
                    _daemons.OnNext(result);
                if (_daemonsRerun) { _daemonsRerun = false; continue; }
                _daemonsBusy = false;
                return;
            }
        }
    }

    /// Production fetch: authenticated GET {server}/api/agent-instances via the
    /// HttpClientExtensions choke point, client built per call. Unreachable/non-auth failures
    /// come back as an empty (non-Unauthorized) RemoteFetch — lane loss is data, not a sign-in
    /// problem.
    public static Func<CancellationToken, Task<RemoteFetch>> HttpFetch(
            ConfigRoot config, ProfileContext? profiles) => async ct => {
        var serverUrl = profiles?.Resolution.ServerUrl;
        if (profiles is null || string.IsNullOrEmpty(serverUrl)) return new RemoteFetch(null);
        try {
            var (client, status) = await HttpClientExtensions.CreateClientWithAuthStatusAsync(
                config, profiles, serverUrl, ct, autoRetryUnauthorized: true).ConfigureAwait(false);
            using (client) {
                if (status is not (AuthStatus.Ok or AuthStatus.NoAuthRequired))
                    return new RemoteFetch(null, Unauthorized: true);
                var url = $"{serverUrl.TrimEnd('/')}/{ApiRoutes.AgentInstances}";
                using var response = await client.GetAsync(url, ct).ConfigureAwait(false);
                if (response.StatusCode == System.Net.HttpStatusCode.Unauthorized)
                    return new RemoteFetch(null, Unauthorized: true);
                if (!response.IsSuccessStatusCode) return new RemoteFetch(null);
                var rows = await response.Content.ReadFromJsonAsync(
                    RemoteModelsJsonContext.Default.AgentInstanceDtoArray, ct).ConfigureAwait(false);
                return new RemoteFetch(rows);
            }
        } catch (Exception) {
            return new RemoteFetch(null);
        }
    };

    public void Dispose() {
        _subscriptions.Dispose();
        _agents.Dispose();
        _daemons.Dispose();
    }
}
