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
