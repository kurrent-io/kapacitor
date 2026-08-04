using Capacitor.Cli.Core.LocalIpc;
using DynamicData;

namespace Capacitor.App.Services;

/// Rx/DynamicData adapter over the daemon's local-control event stream. Interface exists so
/// ViewModel tests can script the stream without a real daemon.
public interface IDaemonClientService {
    /// Replay-1; initial value (Connecting, null, null) published synchronously at construction.
    IObservable<AttachStatus> Status { get; }

    /// Replay-1; emits nothing until the first real snapshot arrives (no fabricated seed).
    IObservable<DaemonStatusDto> Snapshots { get; }

    /// Keyed by Id. Retained across disconnects — staleness is a presentation concern.
    SourceCache<AgentStatusDto, string> Agents { get; }

    /// Single-flight: cancels any in-flight enumeration, awaits its completion, then starts the
    /// next one. Concurrent calls coalesce onto the in-flight restart. No-op after shutdown.
    Task RestartLoopAsync();

    /// Spawns `kcap daemon start -d --name <DaemonName>`. On exit 0 immediately kicks
    /// RestartLoopAsync so the attach doesn't sit out a backoff.
    Task<StartDaemonResult> StartDaemonAsync(CancellationToken ct);

    string DaemonName { get; }
}
