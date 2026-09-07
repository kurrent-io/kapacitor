using System.Reactive.Subjects;
using Capacitor.App.Services;
using Capacitor.Remote.Models;
using DynamicData;

namespace Capacitor.App.Tests.Unit;

/// Scripted IRemoteAgentsService — a SourceCache the test edits directly plus a BehaviorSubject
/// for Daemons — shared by AgentDirectoryTests and the rail tests that merge remote rows in.
sealed class FakeRemoteAgents : IRemoteAgentsService, IDisposable {
    public readonly SourceCache<AgentInstanceDto, string> Cache = new(a => a.AgentId);
    public readonly BehaviorSubject<IReadOnlyList<DaemonInfo>> DaemonsSubject = new([]);
    public IObservableCache<AgentInstanceDto, string> Agents => Cache.AsObservableCache();
    public IObservable<IReadOnlyList<DaemonInfo>> Daemons => DaemonsSubject;
    public void Dispose() => Cache.Dispose();
}
