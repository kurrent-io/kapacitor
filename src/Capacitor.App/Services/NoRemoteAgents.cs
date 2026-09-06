using System.Reactive;
using System.Reactive.Linq;
using Capacitor.Remote.Models;
using DynamicData;

namespace Capacitor.App.Services;

/// Local-only stand-in for the composition root until the remote lane is wired (rail
/// grouping/machine-badge wiring lands in a later task): never reports a remote agent or daemon.
internal sealed class NoRemoteAgents : IRemoteAgentsService {
    public IObservableCache<AgentInstanceDto, string> Agents { get; } =
        new SourceCache<AgentInstanceDto, string>(a => a.AgentId).AsObservableCache();
    public IObservable<IReadOnlyList<DaemonInfo>> Daemons { get; } = Observable.Return<IReadOnlyList<DaemonInfo>>([]);
}

/// Paired with <see cref="NoRemoteAgents"/>: a lane that never connects.
internal sealed class NoServerLane : IServerLane {
    public IObservable<ServerLaneStatus> Status { get; } = Observable.Return(new ServerLaneStatus(ServerLaneState.Dormant));
    public IObservable<Unit> AgentInstancesChanged => Observable.Never<Unit>();
    public IObservable<Unit> DaemonsChanged => Observable.Never<Unit>();
    public IObservable<LaunchFailure> LaunchFailures => Observable.Never<LaunchFailure>();
    public Task<IReadOnlyList<DaemonInfo>?> GetConnectedDaemonsAsync(CancellationToken ct) =>
        Task.FromResult<IReadOnlyList<DaemonInfo>?>(null);
}
