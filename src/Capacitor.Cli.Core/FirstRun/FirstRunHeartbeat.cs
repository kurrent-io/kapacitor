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

    /// <summary>How long to go quiet on a throttle that names no delay. Well past the staleness window,
    /// deliberately: a throttled tenant reading as silent is honest — the machine cannot be heard — and
    /// beating through the refusal to avoid saying so would spend the budget the poll needs.</summary>
    static readonly TimeSpan ThrottleBackoff = TimeSpan.FromSeconds(60);

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
    /// Stops scheduling, and does not wait. A beat in flight is aborted by the cancel rather than
    /// finished — nothing is owed to it: the relinquish that follows states the ending, and the browser
    /// reads a stated ending ahead of an inferred one. Waiting instead would put an await on the leg's
    /// way out for a difference nothing can observe.
    /// </summary>
    public void Dispose() {
        if (Interlocked.Exchange(ref _stopped, 1) != 0) return;

        try {
            _stopping.Cancel();
        } catch (ObjectDisposedException) {
            // The loop disposes the source once it ends, and it ends only on this cancel — so this is
            // unreachable while that holds. Guarded anyway: an await added to the loop that can throw
            // something other than the cancel would break the ordering, and the cost of finding out
            // would be this throwing out of the leg's `using` and masking its result.
        }

        // Observed so a fault cannot surface as an unobserved task exception. The loop swallows
        // everything a beat can throw, so there is only ever the cancel we just asked for.
        _ = _beating.ContinueWith(static t => _ = t.Exception, TaskScheduler.Default);
    }

    static async Task BeatAsync(
            IFirstRunFlowChannel channel, string serverUrl, string flowId, TimeProvider clock,
            TimeSpan interval, CancellationTokenSource stopping) {
        var ct = stopping.Token;

        using var timer = new PeriodicTimer(interval, clock);

        var quietUntil = DateTimeOffset.MinValue;

        try {
            while (!ct.IsCancellationRequested) {
                if (clock.GetUtcNow() >= quietUntil)
                    quietUntil = await SendOneAsync(channel, serverUrl, flowId, clock, interval, ct);

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
    /// One beat, bounded by the interval, returning the instant to stay quiet until.
    ///
    /// <para><b>Bounded, or one black-holed connection costs the client's whole HTTP timeout.</b> That
    /// timeout is three intervals, so a single wake-from-sleep or NAT rebind — the conditions this
    /// feature exists for — would produce a gap of several missed beats from one request.</para>
    ///
    /// <para><b>Success is not inspected; a throttle is.</b> A failing beat needs no handling, because
    /// the next is already due and a run of them is the signal. A 429 is an instruction rather than a
    /// failure, and the poll shares this tenant's budget.</para>
    ///
    /// <para>Swallows everything, including a cancel: this runs on a detached task, so an escaping
    /// exception has no caller to reach. The loop reads the token itself.</para>
    /// </summary>
    static async Task<DateTimeOffset> SendOneAsync(
            IFirstRunFlowChannel channel, string serverUrl, string flowId, TimeProvider clock,
            TimeSpan interval, CancellationToken ct) {
        try {
            using var bound   = new CancellationTokenSource(interval, clock);
            using var either  = CancellationTokenSource.CreateLinkedTokenSource(ct, bound.Token);

            var outcome = await channel.HeartbeatAsync(serverUrl, flowId, either.Token);

            return outcome.StatusCode is 429
                ? clock.GetUtcNow() + (outcome.RetryAfter ?? ThrottleBackoff)
                : DateTimeOffset.MinValue;
        } catch (Exception) {
            // Best effort, by construction.
            return DateTimeOffset.MinValue;
        }
    }
}
