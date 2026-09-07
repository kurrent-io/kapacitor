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
/// Suppression is evidence-based, symmetric and pairwise — a row hides only where the twin daemon's
/// other lane holds a row for the SAME agent. While the twin is proven, its remote row hides
/// whenever the local socket is Connected (the local view is current), and the local row hides once
/// it isn't (the twin's own server row wins). Another daemon's rows never take part: an unproven
/// identity, an unpaired agent and a same-id agent elsewhere all keep both lanes' rows.
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
    List<AgentStatusDto> _localAgents = [];

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

        // ToCollection (not the raw changeset), matching remote.Agents below: whether a local row
        // shows depends on the whole remote set, so Recompute needs the full current local set on
        // every change — an incremental Add/Update/Remove-per-key handler can't express that.
        local.Agents.Connect().ToCollection()
            .Subscribe(items => { lock (_lock) _localAgents = [.. items]; Recompute(); })
            .DisposeWith(_subscriptions);

        remote.Agents.Connect().ToCollection()
            .Subscribe(items => { lock (_lock) _remoteAgents = [.. items]; Recompute(); })
            .DisposeWith(_subscriptions);
        remote.Daemons
            .Subscribe(d => { lock (_lock) _daemons = d; Recompute(); })
            .DisposeWith(_subscriptions);
        local.Status
            .Select(s => s.State == AttachState.Connected).DistinctUntilChanged()
            .Subscribe(c => { lock (_lock) _localConnected = c; Recompute(); })
            .DisposeWith(_subscriptions);
        local.Snapshots
            .Select(s => s.Daemon.ServerUrl).DistinctUntilChanged()
            .Subscribe(u => { lock (_lock) _localServerUrl = u; Recompute(); })
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
    // _rows.Edit inside _lock is deadlock-free. Owns BOTH lanes' rows in one pass, because
    // precedence is pairwise: while the twin is proven and the local socket is down, a local row
    // yields to the twin's own row for the SAME agent — but only to that row. Absent server data is
    // not evidence an agent ended (a private agent is never registered, and the registry has a
    // seed gap after every connect), so an unpaired local row stands as display-only history.
    void Recompute() {
        lock (_lock) {
            var twin = LocalDaemonTwin.Find(_daemons, _localMachineId, _local.DaemonName, _localServerUrl, _appServerUrl);
            var twinProven = twin is not null;
            bool OnTwin(AgentInstanceDto a) =>
                twinProven && a.OwnerUserId == twin!.Value.OwnerUserId && a.DaemonName == twin.Value.DaemonName;

            var remote = _remoteAgents
                .Where(a => a.Status is "Starting" or "Running")
                .Where(a => !(_localConnected && OnTwin(a)))
                .ToList();
            // The counterpart set is the TWIN's own rows, never every remote row: proving this
            // daemon's registry twin establishes no correspondence with an agent of the same id
            // running on some other daemon, which is a different agent entirely.
            var twinIds = remote.Where(OnTwin).Select(a => a.AgentId).ToHashSet(StringComparer.Ordinal);
            var localRows = _localAgents
                .Where(a => !(twinProven && !_localConnected && twinIds.Contains(a.Id)))
                .Select(ProjectLocal);
            var next = localRows.Concat(remote.Select(AgentRow.FromRemote)).ToList();

            _rows.Edit(cache => {
                foreach (var key in cache.Keys.Where(k => !next.Any(r => r.Key == k)).ToList())
                    cache.RemoveKey(key);
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
