namespace Capacitor.Cli.Core.FirstRun;

/// <summary>
/// Says this machine is still here, on its own timer, for as long as a flow is being waited on.
///
/// <para><b>Deliberately not driven by the poll.</b> The poll stops for the whole of an import — the
/// loop blocks on it and adds the elapsed time back to its own deadline — so liveness derived from the
/// poll would declare the machine gone during the one stretch it is working hardest. A separate timer
/// measures the process, which is the only thing a beat can honestly claim.</para>
///
/// <para><b>Liveness of the process, never of the work.</b> A wedged leg goes on beating. What this
/// catches is the deaths that send nothing at all — SIGKILL, power loss, a shut lid, a dropped
/// network — which is exactly the class a relinquish notice structurally cannot reach.</para>
/// </summary>
public sealed class FirstRunHeartbeat : IDisposable {
    /// <summary>Comfortably inside the server's staleness window, so a single dropped beat is not a
    /// verdict. Lighter than the 2s poll it runs beside.</summary>
    public static readonly TimeSpan Interval = TimeSpan.FromSeconds(5);

    readonly CancellationTokenSource _stopping = new();
    readonly Task                    _beating;

    int _stopped;

    FirstRunHeartbeat(
            IFirstRunFlowChannel channel, string serverUrl, string flowId, TimeProvider clock,
            TimeSpan interval) =>
        _beating = BeatAsync(channel, serverUrl, flowId, clock, interval, _stopping);

    /// <summary>Starts beating immediately, so a flow becomes observably live without waiting out a
    /// first interval. Dispose to stop.</summary>
    public static FirstRunHeartbeat Start(
            IFirstRunFlowChannel channel, string serverUrl, string flowId, TimeProvider clock,
            TimeSpan? interval = null) =>
        new(channel, serverUrl, flowId, clock, interval ?? Interval);

    /// <summary>
    /// Stops scheduling. A beat already in flight is left to land, which is not a race worth closing:
    /// it was issued while the machine was alive, so it reports something that was true, and a relinquish
    /// arriving behind it closes the flow regardless — the browser reads a stated ending ahead of an
    /// inferred one either way. Waiting for it instead would put an await on the leg's way out for a
    /// difference nothing can observe.
    /// </summary>
    public void Dispose() {
        if (Interlocked.Exchange(ref _stopped, 1) != 0) return;

        _stopping.Cancel();

        // Observed so a fault cannot surface as an unobserved task exception. The loop swallows
        // everything a beat can throw, so there is only ever the cancel we just asked for.
        _ = _beating.ContinueWith(static t => _ = t.Exception, TaskScheduler.Default);
    }

    static async Task BeatAsync(
            IFirstRunFlowChannel channel, string serverUrl, string flowId, TimeProvider clock,
            TimeSpan interval, CancellationTokenSource stopping) {
        var ct = stopping.Token;

        using var timer = new PeriodicTimer(interval, clock);

        try {
            while (!ct.IsCancellationRequested) {
                await SendOneAsync(channel, serverUrl, flowId, ct);

                if (!await timer.WaitForNextTickAsync(ct)) return;
            }
        } catch (OperationCanceledException) {
        } finally {
            // Here rather than in Dispose, which returns while this is still using the token: the loop
            // ends only on the cancel, so this is provably the last read of it.
            stopping.Dispose();
        }
    }

    /// <summary>
    /// Swallows everything, including a cancel.
    ///
    /// <para>Unlike every other await in this feature, a cancel is NOT propagated: this runs on a
    /// detached task, so an escaping exception has no caller to reach and would surface as an unhandled
    /// one on a background thread. The loop reads the token itself, which is where stopping is decided.
    /// A status code is not inspected at all — the next beat is already due, and a run of them failing
    /// is the signal, which only the server is positioned to read.</para>
    /// </summary>
    static async Task SendOneAsync(
            IFirstRunFlowChannel channel, string serverUrl, string flowId, CancellationToken ct) {
        try {
            await channel.HeartbeatAsync(serverUrl, flowId, ct);
        } catch (Exception) {
            // Best effort, by construction.
        }
    }
}
