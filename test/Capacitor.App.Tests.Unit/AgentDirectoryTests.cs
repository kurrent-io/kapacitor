using Capacitor.App.Services;
using Capacitor.Cli.Core.LocalIpc;
using Capacitor.Remote.Models;
using DynamicData;

namespace Capacitor.App.Tests.Unit;

public class AgentDirectoryTests {
    const string Server = "http://localhost:9999"; // FakeDaemonClientService.Snap's default ServerUrl

    sealed class FakeRemoteAgents : IRemoteAgentsService, IDisposable {
        public readonly SourceCache<AgentInstanceDto, string> Cache = new(a => a.AgentId);
        public readonly System.Reactive.Subjects.BehaviorSubject<IReadOnlyList<DaemonInfo>> DaemonsSubject = new([]);
        public IObservableCache<AgentInstanceDto, string> Agents => Cache.AsObservableCache();
        public IObservable<IReadOnlyList<DaemonInfo>> Daemons => DaemonsSubject;
        public void Dispose() => Cache.Dispose();
    }

    static AgentInstanceDto Remote(string id, string daemon = "work-mac", string owner = "u1", string status = "Running") =>
        new() { AgentId = id, Status = status, DaemonName = daemon, OwnerUserId = owner, Vendor = "claude", RepoOwner = "o", RepoName = "r" };

    static (FakeDaemonClientService Local, FakeRemoteAgents Remote, FakeServerLane Lane, AgentDirectory Dir) Build(
            string? machineId = "m1") {
        var local = new FakeDaemonClientService();
        var remote = new FakeRemoteAgents();
        var lane = new FakeServerLane();
        var dir = new AgentDirectory(
            local, remote, lane, new RepoIdentityResolver(_ => null), p => p,
            machineId, Server);
        return (local, remote, lane, dir);
    }

    static AgentStatusDto LocalAgent(string id) => new(
        Id: id, Kind: "agent", Vendor: "claude", RepoPath: "/r", Status: "Running",
        FlowRunId: null, FlowRole: null, Requester: null, CreatedAt: DateTime.UtcNow, Model: null,
        RequesterDisplay: null);

    [Test]
    public async Task LocalAndRemoteRowsMerge() {
        var (local, remote, _, dir) = Build();
        using var _d = dir;
        local.Agents.AddOrUpdate(LocalAgent("a1"));
        remote.Cache.AddOrUpdate(Remote("b1"));
        await Assert.That(dir.Rows.Count).IsEqualTo(2);
        await Assert.That(dir.Rows.Lookup("local:a1").HasValue).IsTrue();
        await Assert.That(dir.Rows.Lookup("remote:b1").HasValue).IsTrue();
    }

    [Test]
    public async Task TwinAgentsSuppressWhileLocalConnected() {
        var (local, remote, _, dir) = Build();
        using var _d = dir;
        // Local daemon "daemon-a" on machine m1, connected, reporting Server.
        local.SnapshotsSubject.OnNext(FakeDaemonClientService.Snap());
        local.StatusSubject.OnNext(new(AttachState.Connected, null, ["status/1"]));
        remote.DaemonsSubject.OnNext([new DaemonInfo { Name = "daemon-a", MachineId = "m1", OwnerUserId = "u1", Connected = true }]);
        remote.Cache.AddOrUpdate(Remote("b1", daemon: "daemon-a"));
        remote.Cache.AddOrUpdate(Remote("b2", daemon: "home-pc"));

        await Assert.That(dir.Rows.Lookup("remote:b1").HasValue).IsFalse(); // twin's agent suppressed
        await Assert.That(dir.Rows.Lookup("remote:b2").HasValue).IsTrue();  // other machine stands
    }

    [Test]
    public async Task SuppressionLiftsWhenLocalUnreachable() {
        var (local, remote, _, dir) = Build();
        using var _d = dir;
        local.SnapshotsSubject.OnNext(FakeDaemonClientService.Snap());
        local.StatusSubject.OnNext(new(AttachState.Connected, null, ["status/1"]));
        remote.DaemonsSubject.OnNext([new DaemonInfo { Name = "daemon-a", MachineId = "m1", OwnerUserId = "u1", Connected = true }]);
        remote.Cache.AddOrUpdate(Remote("b1", daemon: "daemon-a"));
        await Assert.That(dir.Rows.Lookup("remote:b1").HasValue).IsFalse();

        local.StatusSubject.OnNext(new(AttachState.Unreachable, "daemon_unreachable", null));
        await Assert.That(dir.Rows.Lookup("remote:b1").HasValue).IsTrue();
    }

    [Test]
    public async Task UncertainTwinFailsOpenToDuplicates() {
        var (local, remote, _, dir) = Build(machineId: null); // no persisted machine id
        using var _d = dir;
        local.SnapshotsSubject.OnNext(FakeDaemonClientService.Snap());
        local.StatusSubject.OnNext(new(AttachState.Connected, null, ["status/1"]));
        local.Agents.AddOrUpdate(LocalAgent("a1"));
        remote.DaemonsSubject.OnNext([new DaemonInfo { Name = "daemon-a", MachineId = "m1", OwnerUserId = "u1", Connected = true }]);
        remote.Cache.AddOrUpdate(Remote("a1", daemon: "daemon-a")); // same agent id, both lanes

        await Assert.That(dir.Rows.Lookup("local:a1").HasValue).IsTrue();
        await Assert.That(dir.Rows.Lookup("remote:a1").HasValue).IsTrue(); // two rows, never hidden
    }

    [Test]
    public async Task EndedRemoteAgentsAreNotRows() {
        var (_, remote, _, dir) = Build();
        using var _d = dir;
        remote.Cache.AddOrUpdate(Remote("b1", status: "Completed"));
        await Assert.That(dir.Rows.Count).IsEqualTo(0);
    }

    [Test]
    public async Task NoOpRecomputeSkipsUpdateChanges() {
        var (_, remote, _, dir) = Build();
        using var _d = dir;
        remote.Cache.AddOrUpdate(Remote("b1"));
        await Assert.That(dir.Rows.Lookup("remote:b1").HasValue).IsTrue();

        var updateCount = 0;
        using var sub = dir.Rows.Connect().Subscribe(changes => {
            foreach (var change in changes)
                if (change.Reason == ChangeReason.Update) updateCount++;
        });

        remote.DaemonsSubject.OnNext([]); // recompute runs again; b1's row is unchanged

        await Assert.That(updateCount).IsEqualTo(0);
    }

    [Test]
    public async Task RemoteStaleTracksLane() {
        var (_, _, lane, dir) = Build();
        using var _d = dir;
        bool? stale = null;
        using var sub = dir.RemoteStale.Subscribe(s => stale = s);
        await Assert.That(stale).IsTrue();
        lane.StatusSubject.OnNext(new(ServerLaneState.Connected));
        await Assert.That(stale).IsFalse();
    }
}
