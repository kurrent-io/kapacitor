using Capacitor.Cli.Core.Auth;
using Capacitor.Cli.Core.LocalIpc;

namespace Capacitor.App.Services.Mutation;

/// Evidence about one daemon; IdentityConsistent is fail-closed — pid+instance must be present and agree on BOTH correlated sides.
public sealed record ObservedEvidence(
    bool Reachable, IReadOnlyList<string>? Capabilities, string? DaemonVersion,
    string? ServerUrl, string? DaemonName, int? Pid, string? InstanceId,
    bool IdentityConsistent);

/// One evidence source for the mutation lane; null means "cannot observe this target", never Reachable=false.
public interface IDaemonObservation {
    Task<ObservedEvidence?> ObserveAsync(MutationRequest request, CancellationToken ct);
}

/// Bounded diagnostic dial via LocalControlProbe — correct for any target, at the cost of a fresh socket round trip per call.
public sealed class OneShotObservation(TimeSpan timeout) : IDaemonObservation {
    // Test seam: production always resolves to LocalControlProbe.ProbeAsync itself.
    internal Func<string, TimeSpan, CancellationToken, Task<ProbeResult>> Probe { get; init; } = LocalControlProbe.ProbeAsync;

    public async Task<ObservedEvidence?> ObserveAsync(MutationRequest request, CancellationToken ct) {
        var result = await Probe(request.DaemonName, timeout, ct).ConfigureAwait(false);

        if (!result.Reachable) return new ObservedEvidence(false, null, null, null, null, null, null, false);

        return new ObservedEvidence(
            Reachable: true,
            Capabilities: result.Hello?.Capabilities,
            DaemonVersion: result.Hello?.DaemonVersion,
            ServerUrl: result.Snapshot?.Daemon.ServerUrl,
            DaemonName: result.Snapshot?.Daemon.Name,
            Pid: result.Hello?.Pid,
            InstanceId: result.Hello?.InstanceId,
            IdentityConsistent: result.IdentityConsistent);
    }
}

/// A generation barrier: discards whatever Status/Snapshots replay on subscribe, waits for the next post-subscription emission of each; a timeout degrades to null.
public sealed class LiveGraphObservation(IDaemonClientService client, TimeProvider time) : IDaemonObservation {
    internal static readonly TimeSpan FreshEmissionTimeout = TimeSpan.FromSeconds(2);

    public async Task<ObservedEvidence?> ObserveAsync(MutationRequest request, CancellationToken ct) {
        // Identity gate first: a client bound to a different daemon name, or a different profile,
        // can never stand in for the requested one (spec §4: "the client's daemon name + its
        // profile/server" must match), regardless of what its own attach state currently shows.
        if (client.DaemonName != request.DaemonName) return null;
        if (client.ProfileName != request.Profile) return null;

        var statusTcs = new TaskCompletionSource<AttachStatus>(TaskCreationOptions.RunContinuationsAsynchronously);
        var duringStatusReplay = true;
        using var statusSub = client.Status.Subscribe(s => { if (!duringStatusReplay) statusTcs.TrySetResult(s); });
        duringStatusReplay = false;

        var snapshotTcs = new TaskCompletionSource<DaemonStatusDto>(TaskCreationOptions.RunContinuationsAsynchronously);
        var duringSnapshotReplay = true;
        using var snapshotSub = client.Snapshots.Subscribe(s => { if (!duringSnapshotReplay) snapshotTcs.TrySetResult(s); });
        duringSnapshotReplay = false;

        using var timeoutCts = new CancellationTokenSource();
        try {
            var timeoutTask = Task.Delay(FreshEmissionTimeout, time, timeoutCts.Token);
            var freshPair = Task.WhenAll(statusTcs.Task, snapshotTcs.Task);
            var winner = await Task.WhenAny(freshPair, timeoutTask).WaitAsync(ct).ConfigureAwait(false);
            if (winner == timeoutTask) return null; // no fresh post-subscription emission within the bound
        } finally {
            timeoutCts.Cancel();
        }

        var status   = statusTcs.Task.Result;
        var snapshot = snapshotTcs.Task.Result;

        if (!ServerIdentity.Matches(snapshot.Daemon.ServerUrl, request.CanonicalServer)) return null;

        if (status.State != AttachState.Connected)
            return new ObservedEvidence(false, null, null, null, null, null, null, false);

        var daemon = snapshot.Daemon;
        var identityConsistent = status.Identity is { Pid: { } idPid, InstanceId: { } idInstance }
            && daemon is { Pid: { } snapPid, InstanceId: { } snapInstance }
            && idPid == snapPid && idInstance == snapInstance;

        return new ObservedEvidence(
            Reachable: true,
            Capabilities: status.Capabilities,
            DaemonVersion: status.Identity?.DaemonVersion,
            ServerUrl: daemon.ServerUrl,
            DaemonName: daemon.Name,
            Pid: status.Identity?.Pid,
            InstanceId: status.Identity?.InstanceId,
            IdentityConsistent: identityConsistent);
    }
}
