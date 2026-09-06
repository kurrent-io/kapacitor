using System.Reactive.Disposables;
using System.Reactive.Disposables.Fluent;
using System.Reactive.Linq;
using Capacitor.App.ViewModels;
using Capacitor.Cli.Core.LocalIpc;
using Capacitor.Remote.Models;
using DynamicData;

namespace Capacitor.App.Services;

public interface IAgentDirectory {
    IObservableCache<AgentRow, string> Rows { get; }
    /// True while the server lane is not Connected — rail rows grey out on it.
    IObservable<bool> RemoteStale { get; }
}

/// Merges the local daemon's agents with the server registry's into source-scoped rows.
/// Suppression is daemon-level and evidence-based: only the proven twin's agents are hidden,
/// and only while the local socket is Connected — everything uncertain renders twice rather
/// than not at all.
public sealed class AgentDirectory : IAgentDirectory, IDisposable {
    readonly SourceCache<AgentRow, string> _rows = new(r => r.Key);
    readonly CompositeDisposable _subscriptions = new();
    readonly IDaemonClientService _local;
    readonly RepoIdentityResolver _repoIdentity;
    readonly Func<string, string> _resolveLocalRepoRoot;
    readonly string? _localMachineId;
    readonly string? _appServerUrl;
    readonly object _lock = new();

    IReadOnlyList<DaemonInfo> _daemons = [];
    bool _localConnected;
    string? _localServerUrl;
    List<AgentInstanceDto> _remoteAgents = [];

    public AgentDirectory(
            IDaemonClientService local, IRemoteAgentsService remote, IServerLane lane,
            RepoIdentityResolver repoIdentity, Func<string, string> resolveLocalRepoRoot,
            string? localMachineId, string? appServerUrl) {
        _local = local;
        _repoIdentity = repoIdentity;
        _resolveLocalRepoRoot = resolveLocalRepoRoot;
        _localMachineId = localMachineId;
        _appServerUrl = appServerUrl;

        RemoteStale = lane.Status.Select(s => s.State != ServerLaneState.Connected).DistinctUntilChanged();

        local.Agents.Connect().Subscribe(changes => {
            foreach (var change in changes) {
                switch (change.Reason) {
                    case ChangeReason.Add or ChangeReason.Update or ChangeReason.Refresh:
                        _rows.AddOrUpdate(ProjectLocal(change.Current));
                        break;
                    case ChangeReason.Remove:
                        _rows.RemoveKey($"local:{change.Current.Id}");
                        break;
                }
            }
        }).DisposeWith(_subscriptions);

        remote.Agents.Connect().ToCollection()
            .Subscribe(items => { lock (_lock) _remoteAgents = [.. items]; RecomputeRemote(); })
            .DisposeWith(_subscriptions);
        remote.Daemons
            .Subscribe(d => { lock (_lock) _daemons = d; RecomputeRemote(); })
            .DisposeWith(_subscriptions);
        local.Status
            .Select(s => s.State == AttachState.Connected).DistinctUntilChanged()
            .Subscribe(c => { lock (_lock) _localConnected = c; RecomputeRemote(); })
            .DisposeWith(_subscriptions);
        local.Snapshots
            .Select(s => s.Daemon.ServerUrl).DistinctUntilChanged()
            .Subscribe(u => { lock (_lock) _localServerUrl = u; RecomputeRemote(); })
            .DisposeWith(_subscriptions);
    }

    public IObservableCache<AgentRow, string> Rows => _rows.AsObservableCache();
    public IObservable<bool> RemoteStale { get; }

    AgentRow ProjectLocal(AgentStatusDto dto) {
        var repo = dto.RepoPath is { Length: > 0 } path
            ? _repoIdentity.ForLocalRoot(PlatformPaths.Normalize(_resolveLocalRepoRoot(path)))
            : new RepoIdentity("path:", "No repository");
        return AgentRow.FromLocal(dto, repo);
    }

    // The compute-then-edit pair must be one atomic unit under _lock: two triggers (e.g. a
    // socket-thread Status flip racing a SignalR-thread Daemons refresh) that read-then-edit as
    // separate critical sections can land their _rows.Edit calls out of read order, letting a
    // stale edit overwrite a fresher one. DynamicData's own cache lock is separate, so nesting
    // _rows.Edit inside _lock is deadlock-free.
    void RecomputeRemote() {
        lock (_lock) {
            var twin = LocalDaemonTwin.Find(_daemons, _localMachineId, _local.DaemonName, _localServerUrl, _appServerUrl);
            var next = _remoteAgents
                .Where(a => a.Status is "Starting" or "Running")
                .Where(a => !(twin is { } t && _localConnected
                              && a.OwnerUserId == t.OwnerUserId && a.DaemonName == t.DaemonName))
                .Select(AgentRow.FromRemote)
                .ToList();
            _rows.Edit(cache => {
                foreach (var key in cache.Keys.Where(k => k.StartsWith("remote:", StringComparison.Ordinal)).ToList())
                    if (!next.Any(r => r.Key == key)) cache.RemoveKey(key);
                foreach (var row in next)
                    if (cache.Lookup(row.Key) is not { HasValue: true, Value: var existing } || existing != row)
                        cache.AddOrUpdate(row);
            });
        }
    }

    public void Dispose() {
        _subscriptions.Dispose();
        _rows.Dispose();
    }
}
