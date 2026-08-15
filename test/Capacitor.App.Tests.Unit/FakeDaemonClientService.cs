using System.Reactive.Subjects;
using Capacitor.App.Services;
using Capacitor.Cli.Core.LocalIpc;
using DynamicData;

namespace Capacitor.App.Tests.Unit;

/// Scripted IDaemonClientService — subject-backed Status/Snapshots/Agents — shared by
/// MainWindowViewModelTests and MainWindowSmokeTests so both drive exact event sequences
/// without a real daemon.
sealed class FakeDaemonClientService : IDaemonClientService {
    public readonly BehaviorSubject<AttachStatus> StatusSubject = new(new(AttachState.Connecting, null, null));
    public readonly ReplaySubject<DaemonStatusDto> SnapshotsSubject = new(1);

    public IObservable<AttachStatus> Status => StatusSubject;
    public IObservable<DaemonStatusDto> Snapshots => SnapshotsSubject;
    public SourceCache<AgentStatusDto, string> Agents { get; } = new(a => a.Id);
    public string DaemonName { get; set; } = "daemon-a";
    // Matches the "default" profile every test's MutationRequest.Profile literal already uses, so
    // existing LiveGraphObservation tests keep passing without also having to set this explicitly.
    public string? ProfileName { get; set; } = "default";

    public int RestartCount;
    public Task RestartLoopAsync() {
        RestartCount++;
        return Task.CompletedTask;
    }

    public int StartDaemonCallCount;
    public Func<CancellationToken, Task<StartDaemonResult>>? StartBehavior;
    public Task<StartDaemonResult> StartDaemonAsync(CancellationToken ct) {
        StartDaemonCallCount++;
        return (StartBehavior ?? (_ => Task.FromResult(new StartDaemonResult(true, null))))(ct);
    }

    public static DaemonStatusDto Snap(
            string daemon = "daemon-a", string version = "1.2.3", string serverUrl = "http://localhost:9999",
            string connection = "connected", int active = 0, int max = 5, int? pid = null, string? instanceId = null) {
        var agents = Enumerable.Range(0, active).Select(i => new AgentStatusDto(
            Id: $"a{i}", Kind: "agent", Vendor: "claude", RepoPath: null, Status: "Running",
            FlowRunId: null, FlowRole: null, Requester: null, CreatedAt: DateTime.UtcNow, Model: null,
            RequesterDisplay: null
        )).ToList();
        return new DaemonStatusDto(
            new DaemonInfoDto(daemon, version, serverUrl, connection, max, active, pid, instanceId), agents);
    }
}
