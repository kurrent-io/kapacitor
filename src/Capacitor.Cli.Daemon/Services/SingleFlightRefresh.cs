namespace Capacitor.Cli.Daemon.Services;

/// <summary>
/// Runs a refresh at most once at a time, off the caller's stack, coalescing every request that
/// arrives while one is in flight into exactly ONE further pass.
///
/// <para>Two defects shaped this, both of them mine, both found in review.</para>
///
/// <para><b>Stale completion order.</b> An unsynchronised background refresh reintroduced the very
/// bug this area exists to remove: refresh A starts on a loaded host and spends ~30s timing out;
/// refresh B starts later, probes successfully, publishes valid capabilities and re-registers; then
/// A completes last and overwrites the valid snapshot with its failed-probe result — durably
/// disabling the reviewer again. Atomic reference assignment prevents a torn pointer, not stale
/// ordering.</para>
///
/// <para><b>A discarded task is not an asynchronous boundary.</b> <c>_ = SomethingAsync()</c> still
/// runs the method's synchronous prefix on the caller's stack, up to its first incomplete await. The
/// refresh delegate here computes capabilities <em>synchronously</em> (it shells out to probe CLI
/// versions) before awaiting anything, so a fire-and-forget call still blocked the launch path for
/// the full probe budget. Hence <see cref="Trigger"/> is deliberately NOT async and schedules the
/// work itself — the caller cannot accidentally inherit it.</para>
///
/// <para>Serialising alone is not enough: a burst of N rejections must not queue N refreshes. A
/// request arriving mid-flight sets a rerun flag, so the in-flight pass loops exactly once more and
/// publishes a computation made <em>after</em> that request — the freshest result wins.</para>
/// </summary>
internal sealed class SingleFlightRefresh {
    readonly SemaphoreSlim _gate = new(1, 1);
    int  _rerunRequested;
    Task _current = Task.CompletedTask;

    /// <summary>Requests a refresh and returns IMMEDIATELY — always, whether or not this call won
    /// the gate. Returns void rather than a Task on purpose: a task here would carry two
    /// incompatible meanings (the winner would observe the pass, a coalesced caller would observe
    /// only that it was recorded), and a future caller awaiting it would get a guarantee that does
    /// not hold. Nothing may block on a self-heal.</summary>
    public void Trigger(Func<Task> refresh, Action<Exception>? onError = null) {
        // Wait(0) is the synchronous try-enter: it never blocks, and it either wins the gate or
        // tells us a pass is already running.
        if (!_gate.Wait(0)) {
            // A pass is mid-flight. It may have started before whatever we are reacting to, so ask
            // for one more afterwards rather than assuming it covers us.
            Interlocked.Exchange(ref _rerunRequested, 1);
            return;
        }

        // Won the gate — hand the work to the pool so the delegate's synchronous prefix never runs
        // on the caller's stack. The gate is released inside the run, not here.
        Volatile.Write(ref _current, Task.Run(() => RunHoldingGateAsync(refresh, onError)));
    }

    /// <summary>Test seam: the most recently scheduled pass, for awaiting quiescence. Not part of
    /// the production contract — production never observes a refresh.</summary>
    internal Task Current => Volatile.Read(ref _current);

    async Task RunHoldingGateAsync(Func<Task> refresh, Action<Exception>? onError) {
        try {
            do {
                // Cleared BEFORE the work, so a request arriving during it sets the flag again and
                // earns another pass. Clearing after would swallow it.
                Interlocked.Exchange(ref _rerunRequested, 0);

                try {
                    await refresh().ConfigureAwait(false);
                } catch (Exception ex) {
                    // A refresh is a self-heal; a failure must never become a second, different
                    // error for whatever triggered it.
                    onError?.Invoke(ex);
                }
            } while (Interlocked.CompareExchange(ref _rerunRequested, 0, 1) == 1);
        } finally {
            _gate.Release();
        }
    }
}
