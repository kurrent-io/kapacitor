using System.Collections.Concurrent;
using kapacitor.Eval;

namespace kapacitor.Daemon.Services;

/// <summary>
/// In-memory, per-evalRunId cache of prepared <see cref="EvalService.EvalContext"/> +
/// accumulated verdicts. Populated by <c>PrepareEval</c>, read by each
/// <c>RunQuestion</c>, evicted by <c>FinalizeEval</c> or <c>CancelEval</c>.
///
/// <para>Entries use sliding expiration keyed off last access so a long eval
/// (13 questions × up to 5 min each) can't exceed the window between phase
/// calls — only truly idle contexts expire. A background timer sweeps
/// abandoned entries so a server crash between Prepare and Finalize doesn't
/// leak <c>TraceJson</c> until process restart.</para>
/// </summary>
internal sealed class EvalContextCache : IDisposable {
    sealed record Entry(EvalService.EvalContext Context, DateTimeOffset LastAccessed);

    readonly ConcurrentDictionary<string, Entry> _entries = new();
    readonly Timer                               _sweepTimer;

    // Sliding expiration — an entry is evicted when MaxIdle elapses since
    // its last Get(). Per-question calls refresh the timestamp, so only
    // abandoned entries (server crashed mid-run) expire.
    static readonly TimeSpan MaxIdle       = TimeSpan.FromMinutes(30);
    static readonly TimeSpan SweepInterval = TimeSpan.FromMinutes(5);

    public EvalContextCache() {
        _sweepTimer = new Timer(_ => Sweep(), null, SweepInterval, SweepInterval);
    }

    public void Put(string evalRunId, EvalService.EvalContext ctx) =>
        _entries[evalRunId] = new Entry(ctx, DateTimeOffset.UtcNow);

    public EvalService.EvalContext? Get(string evalRunId) {
        if (!_entries.TryGetValue(evalRunId, out var entry)) return null;
        var now = DateTimeOffset.UtcNow;
        if (now - entry.LastAccessed > MaxIdle) {
            _entries.TryRemove(new KeyValuePair<string, Entry>(evalRunId, entry));
            return null;
        }
        // Refresh last-access so sequential per-question dispatches don't
        // age out a still-active run. Race-safe via TryUpdate — concurrent
        // Get or Remove just loses the refresh, which is benign.
        _entries.TryUpdate(evalRunId, entry with { LastAccessed = now }, entry);
        return entry.Context;
    }

    public void Remove(string evalRunId) => _entries.TryRemove(evalRunId, out _);

    public int Count => _entries.Count;

    void Sweep() {
        var now = DateTimeOffset.UtcNow;
        foreach (var kvp in _entries) {
            if (now - kvp.Value.LastAccessed > MaxIdle) {
                _entries.TryRemove(kvp);
            }
        }
    }

    public void Dispose() => _sweepTimer.Dispose();
}
