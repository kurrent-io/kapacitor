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

    /// <summary>How long to go quiet on a throttle that names no delay.</summary>
    static readonly TimeSpan ThrottleBackoff = TimeSpan.FromSeconds(30);

    /// <summary>
    /// The longest a throttle may silence the machine, however long the server asks for.
    ///
    /// <para>The heartbeat has its own limiter, so the poll can be succeeding every 2s while this route
    /// is refused — and an unclamped <c>Retry-After: 3600</c> would tell the browser the machine had gone
    /// for longer than the whole leg, with a working connection either side of it.</para>
    /// </summary>
    static readonly TimeSpan MaxThrottleBackoff = TimeSpan.FromMinutes(2);

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
                if (clock.GetUtcNow() >= quietUntil) {
                    var verdict = await SendOneAsync(channel, serverUrl, flowId, clock, interval, ct);

                    // A route this server does not have answers the same way for the rest of the leg, so
                    // beating on is ~360 authenticated no-ops that can trip the very limiter the throttle
                    // handling above exists to keep clear. Same oracle the create uses.
                    if (verdict.Unavailable) return;

                    quietUntil = verdict.QuietUntil;
                }

                if (!await timer.WaitForNextTickAsync(ct)) return;
            }
        } catch (OperationCanceledException) {
        } finally {
            // Here rather than in Dispose, which returns while this is still using the token: the loop
            // ends only on the cancel, so this is provably the last read of it.
            stopping.Dispose();
        }
    }

    /// <summary>What one beat leaves behind: when the next may be sent, and whether to send any more.</summary>
    readonly record struct BeatVerdict(DateTimeOffset QuietUntil, bool Unavailable) {
        public static readonly BeatVerdict Continue = new(DateTimeOffset.MinValue, false);
    }

    /// <summary>
    /// One beat. The loop stops WAITING after an interval; it never cancels the request.
    ///
    /// <para><b>This must not hard-cancel, and that is the load-bearing rule here.</b> The beat rides the
    /// setup client, whose 401 handler recovers the credential — and that recovery rotates a single-use
    /// refresh token before persisting it, with the rotation itself uncancellable. A token tripping
    /// between the two spends the credential server-side and never writes the replacement, logging the
    /// user out mid-setup. Abandoning the wait costs an overlapping beat; cancelling costs the session.
    /// </para>
    ///
    /// <para>So the bound is on the loop's patience rather than on the request, which also lets a beat
    /// that legitimately needs longer than one interval — cold TLS after a wake, a tethered link — still
    /// land, instead of being cancelled just before it would have succeeded.</para>
    ///
    /// <para>Success is not inspected. A throttle and an absent route are, because both say something no
    /// later beat can discover for itself.</para>
    /// </summary>
    static async Task<BeatVerdict> SendOneAsync(
            IFirstRunFlowChannel channel, string serverUrl, string flowId, TimeProvider clock,
            TimeSpan interval, CancellationToken ct) {
        var beat = Observed(channel.HeartbeatAsync(serverUrl, flowId, ct));

        var waited = await Task.WhenAny(beat, Task.Delay(interval, clock, ct));

        // Still in flight: leave it running and take the next tick. It will finish or time out on the
        // client's own deadline, and a beat that lands late is still a beat.
        if (waited != beat) return BeatVerdict.Continue;

        var outcome = await beat;

        if (outcome.StatusCode is 404 or 405) return new(DateTimeOffset.MinValue, Unavailable: true);

        if (outcome.StatusCode is not 429) return BeatVerdict.Continue;

        var asked = outcome.RetryAfter ?? ThrottleBackoff;

        return new(clock.GetUtcNow() + (asked > MaxThrottleBackoff ? MaxThrottleBackoff : asked), false);
    }

    /// <summary>Swallows everything a beat can throw, including a cancel: it runs detached, so an
    /// escaping exception has no caller to reach and a request abandoned by the loop above must not
    /// surface as an unobserved fault.</summary>
    static async Task<FirstRunHeartbeatOutcome> Observed(Task<FirstRunHeartbeatOutcome> beat) {
        try {
            return await beat;
        } catch (Exception) {
            return new(0);
        }
    }
}
