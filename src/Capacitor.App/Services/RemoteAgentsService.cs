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

/// Remote registry caches: seeded on lane Connected, refreshed on the org-wide pings. A failed
/// or signed-out fetch returns null and leaves the caches as they were — lane loss is data.
public sealed class RemoteAgentsService : IRemoteAgentsService, IDisposable {
    readonly SourceCache<AgentInstanceDto, string> _agents = new(a => a.AgentId);
    readonly BehaviorSubject<IReadOnlyList<DaemonInfo>> _daemons = new([]);
    readonly IDisposable _subscriptions;
    readonly SemaphoreSlim _agentsFlight = new(1, 1);
    readonly SemaphoreSlim _daemonsFlight = new(1, 1);
    // Guards _generation and _lastSubject together with the identity-change clear, so a refresh
    // that captured its generation before the clear can never publish after it.
    readonly Lock _generationLock = new();
    int _generation;
    bool _agentsRerun;
    bool _daemonsRerun;
    string? _lastSubject;

    public RemoteAgentsService(
            IServerLane lane, Func<CancellationToken, Task<AgentInstanceDto[]?>> fetchAgents,
            TimeSpan? debounce = null) {
        var wait = debounce ?? TimeSpan.FromMilliseconds(250);
        var connected = lane.Status
            .Select(s => s.State == ServerLaneState.Connected)
            .DistinctUntilChanged()
            .Where(c => c)
            .Select(_ => System.Reactive.Unit.Default);

        // Subscribed before the refresh triggers below (same lane.Status), so on a Connected
        // status this runs first: nothing seeded under one identity survives into another, even
        // when the refresh that follows fetches null and would otherwise leave stale rows in
        // place.
        var identityChange = lane.Status
            .Where(s => s.State == ServerLaneState.Connected && s.Subject is not null)
            .Subscribe(s => {
                lock (_generationLock) {
                    if (_lastSubject is not null && _lastSubject != s.Subject) {
                        _agents.Clear();
                        _daemons.OnNext([]);
                        _generation++;
                    }
                    _lastSubject = s.Subject;
                }
            });

        // Merge, not Concat: triggers must reach the refresh methods concurrently so a busy
        // refresh's WaitAsync(0) can actually observe contention and coalesce.
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

    async Task RefreshAgentsAsync(Func<CancellationToken, Task<AgentInstanceDto[]?>> fetch) {
        if (!await _agentsFlight.WaitAsync(0)) { Volatile.Write(ref _agentsRerun, true); return; }
        try {
            do {
                Volatile.Write(ref _agentsRerun, false);
                int generation;
                lock (_generationLock) generation = _generation;
                var result = await fetch(CancellationToken.None).ConfigureAwait(false);
                // A completion from before an identity-change clear must never repopulate what
                // that clear just emptied — checked under the same lock the clear itself takes.
                lock (_generationLock) {
                    if (result is not null && generation == _generation)
                        _agents.EditDiff(result, EqualityComparer<AgentInstanceDto>.Default);
                }
            } while (Volatile.Read(ref _agentsRerun));
        } catch (Exception) {
            // Data-plane refresh: a throw here is a missed refresh, never an app fault.
        } finally {
            _agentsFlight.Release();
        }
    }

    async Task RefreshDaemonsAsync(IServerLane lane) {
        if (!await _daemonsFlight.WaitAsync(0)) { Volatile.Write(ref _daemonsRerun, true); return; }
        try {
            do {
                Volatile.Write(ref _daemonsRerun, false);
                int generation;
                lock (_generationLock) generation = _generation;
                var result = await lane.GetConnectedDaemonsAsync(CancellationToken.None).ConfigureAwait(false);
                lock (_generationLock) {
                    if (result is not null && generation == _generation)
                        _daemons.OnNext(result);
                }
            } while (Volatile.Read(ref _daemonsRerun));
        } catch (Exception) {
        } finally {
            _daemonsFlight.Release();
        }
    }

    /// Production fetch: authenticated GET {server}/api/agent-instances via the
    /// HttpClientExtensions choke point, client built per call, null on auth failure/unreachable.
    public static Func<CancellationToken, Task<AgentInstanceDto[]?>> HttpFetch(
            ConfigRoot config, ProfileContext? profiles) => async ct => {
        var serverUrl = profiles?.Resolution.ServerUrl;
        if (profiles is null || string.IsNullOrEmpty(serverUrl)) return null;
        try {
            var (client, status) = await HttpClientExtensions.CreateClientWithAuthStatusAsync(
                config, profiles, serverUrl, ct, autoRetryUnauthorized: true).ConfigureAwait(false);
            using (client) {
                if (status is not (AuthStatus.Ok or AuthStatus.NoAuthRequired)) return null;
                var url = $"{serverUrl.TrimEnd('/')}/{ApiRoutes.AgentInstances}";
                using var response = await client.GetAsync(url, ct).ConfigureAwait(false);
                if (!response.IsSuccessStatusCode) return null;
                return await response.Content.ReadFromJsonAsync(
                    RemoteModelsJsonContext.Default.AgentInstanceDtoArray, ct).ConfigureAwait(false);
            }
        } catch (Exception) {
            return null;
        }
    };

    public void Dispose() {
        _subscriptions.Dispose();
        _agents.Dispose();
        _daemons.Dispose();
        _agentsFlight.Dispose();
        _daemonsFlight.Dispose();
    }
}
