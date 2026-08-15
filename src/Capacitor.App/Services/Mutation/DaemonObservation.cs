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

/// Zero-cost evidence from the already-live attach stream — valid ONLY for the client's own pinned daemon on the request's own server.
public sealed class LiveGraphObservation(IDaemonClientService client) : IDaemonObservation {
    public Task<ObservedEvidence?> ObserveAsync(MutationRequest request, CancellationToken ct) {
        // Identity gate first: a client bound to a different daemon name, or a different profile,
        // can never stand in for the requested one (spec §4: "the client's daemon name + its
        // profile/server" must match), regardless of what its own attach state currently shows.
        if (client.DaemonName != request.DaemonName) return Task.FromResult<ObservedEvidence?>(null);
        if (client.ProfileName != request.Profile) return Task.FromResult<ObservedEvidence?>(null);

        // Bounded immediate take — Status/Snapshots replay their latest value synchronously on
        // subscribe (same seed-capture pattern as TrayViewModel), so this never waits.
        AttachStatus? status = null;
        using (client.Status.Subscribe(s => status = s)) { }
        DaemonStatusDto? snapshot = null;
        using (client.Snapshots.Subscribe(s => snapshot = s)) { }

        if (status is null) return Task.FromResult<ObservedEvidence?>(null); // defensive: Status always replays

        if (!ServerIdentity.Matches(snapshot?.Daemon.ServerUrl, request.CanonicalServer))
            return Task.FromResult<ObservedEvidence?>(null);

        if (status.State != AttachState.Connected)
            return Task.FromResult<ObservedEvidence?>(new ObservedEvidence(false, null, null, null, null, null, null, false));

        var daemon = snapshot?.Daemon;
        var identityConsistent = status.Identity is { Pid: { } idPid, InstanceId: { } idInstance }
            && daemon is { Pid: { } snapPid, InstanceId: { } snapInstance }
            && idPid == snapPid && idInstance == snapInstance;

        return Task.FromResult<ObservedEvidence?>(new ObservedEvidence(
            Reachable: true,
            Capabilities: status.Capabilities,
            DaemonVersion: status.Identity?.DaemonVersion,
            ServerUrl: daemon?.ServerUrl,
            DaemonName: daemon?.Name,
            Pid: status.Identity?.Pid,
            InstanceId: status.Identity?.InstanceId,
            IdentityConsistent: identityConsistent));
    }
}
