using System.Collections.Concurrent;
using kapacitor.Eval;

namespace kapacitor.Daemon.Services;

/// <summary>
/// In-memory, per-evalRunId cache of prepared <see cref="EvalService.EvalContext"/> +
/// accumulated verdicts. Populated by <c>PrepareEval</c>, read by each
/// <c>RunQuestion</c>, evicted by <c>FinalizeEval</c> or <c>CancelEval</c>.
/// A hard TTL guards against daemons holding a context indefinitely if
/// the server crashes between Prepare and Finalize.
/// </summary>
internal sealed class EvalContextCache {
    sealed record Entry(EvalService.EvalContext Context, DateTimeOffset CreatedAt);

    readonly ConcurrentDictionary<string, Entry> _entries = new();

    static readonly TimeSpan MaxAge = TimeSpan.FromMinutes(15);

    public void Put(string evalRunId, EvalService.EvalContext ctx) =>
        _entries[evalRunId] = new Entry(ctx, DateTimeOffset.UtcNow);

    public EvalService.EvalContext? Get(string evalRunId) {
        if (!_entries.TryGetValue(evalRunId, out var entry)) return null;
        if (DateTimeOffset.UtcNow - entry.CreatedAt > MaxAge) {
            _entries.TryRemove(evalRunId, out _);
            return null;
        }
        return entry.Context;
    }

    public void Remove(string evalRunId) => _entries.TryRemove(evalRunId, out _);

    public int Count => _entries.Count;
}
