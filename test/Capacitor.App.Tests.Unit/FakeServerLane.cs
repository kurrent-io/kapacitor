using ReactiveUnit = System.Reactive.Unit;
using System.Reactive.Subjects;
using Capacitor.App.Services;
using Capacitor.Remote.Models;

namespace Capacitor.App.Tests.Unit;

sealed class FakeServerLane : IServerLane {
    public readonly BehaviorSubject<ServerLaneStatus> StatusSubject = new(new(ServerLaneState.Dormant));
    public readonly Subject<ReactiveUnit> AgentsChangedSubject = new();
    public readonly Subject<ReactiveUnit> DaemonsChangedSubject = new();
    public readonly Subject<LaunchFailure> LaunchFailuresSubject = new();
    public Func<Task<IReadOnlyList<DaemonInfo>?>> DaemonsHandler = () => Task.FromResult<IReadOnlyList<DaemonInfo>?>([]);

    public IObservable<ServerLaneStatus> Status => StatusSubject;
    public IObservable<ReactiveUnit> AgentInstancesChanged => AgentsChangedSubject;
    public IObservable<ReactiveUnit> DaemonsChanged => DaemonsChangedSubject;
    public IObservable<LaunchFailure> LaunchFailures => LaunchFailuresSubject;
    public Task<IReadOnlyList<DaemonInfo>?> GetConnectedDaemonsAsync(CancellationToken ct) => DaemonsHandler();
}
