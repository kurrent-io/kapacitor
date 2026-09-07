namespace Capacitor.App.Services;

public enum ServerLaneState { Dormant, Connecting, Connected, Retrying, SignedOut }

/// Diagnostic is the silent-deafness notice text, or null. Subject is the signed-in account's
/// "sub" claim on a Connected status, for detecting a re-auth as a different account.
public sealed record ServerLaneStatus(
    ServerLaneState State, string? Detail = null, string? Diagnostic = null, string? Subject = null);

public sealed record LaunchFailure(string AgentId, string Reason);

public interface IServerLane {
    /// Replay-1; initial value (Dormant) published synchronously at construction.
    IObservable<ServerLaneStatus> Status { get; }
    IObservable<System.Reactive.Unit> AgentInstancesChanged { get; }
    IObservable<System.Reactive.Unit> DaemonsChanged { get; }
    IObservable<LaunchFailure> LaunchFailures { get; }
    /// Null when the lane has no live connection right now.
    Task<IReadOnlyList<Capacitor.Remote.Models.DaemonInfo>?> GetConnectedDaemonsAsync(CancellationToken ct);
}
