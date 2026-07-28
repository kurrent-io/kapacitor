namespace Capacitor.Cli.Daemon.Services;

/// <summary>
/// Runs a refresh at most once at a time, and coalesces every request that arrives while one is in
/// flight into exactly ONE further pass.
///
/// <para>Extracted because an unsynchronised background refresh reintroduced the very defect this
/// area exists to remove. Concurrent rejected launches each started an independent refresh; atomic
/// reference assignment prevents a torn pointer but not stale <em>completion order</em>. The failing
/// interleaving: refresh A starts on a loaded host and spends ~30s timing out; refresh B starts
/// later, probes successfully, publishes valid capabilities and re-registers; then A completes last
/// and overwrites the valid snapshot with its failed-probe result — durably disabling the reviewer
/// again, which is precisely the bug being fixed.</para>
///
/// <para>Serialising alone is not enough: a burst of N rejections must not queue N refreshes. A
/// request arriving mid-flight sets a rerun flag, so the in-flight pass loops exactly once more and
/// publishes a computation made <em>after</em> that request — the freshest result wins, and the
/// last write is always the newest.</para>
/// </summary>
internal sealed class SingleFlightRefresh {
    readonly SemaphoreSlim _gate = new(1, 1);
    int _rerunRequested;

    /// <summary>Requests a refresh. Returns when this call's work is done, or immediately when
    /// another pass is already running (having asked it to run once more). Never throws: a refresh is
    /// a self-heal, and a failure must not become a second, different error for whatever triggered
    /// it. <paramref name="onError"/> observes the failure.</summary>
    public async Task RequestAsync(Func<Task> refresh, Action<Exception>? onError = null) {
        if (!await _gate.WaitAsync(0).ConfigureAwait(false)) {
            // Someone is mid-flight. Ask them to do one more pass after they finish — their current
            // pass may have started before our trigger and so may not observe what we just changed.
            Interlocked.Exchange(ref _rerunRequested, 1);
            return;
        }

        try {
            do {
                // Cleared BEFORE the work, so a request arriving during it sets the flag again and
                // earns another pass. Clearing after would swallow it.
                Interlocked.Exchange(ref _rerunRequested, 0);

                try {
                    await refresh().ConfigureAwait(false);
                } catch (Exception ex) {
                    onError?.Invoke(ex);
                }
            } while (Interlocked.CompareExchange(ref _rerunRequested, 0, 1) == 1);
        } finally {
            _gate.Release();
        }
    }
}
