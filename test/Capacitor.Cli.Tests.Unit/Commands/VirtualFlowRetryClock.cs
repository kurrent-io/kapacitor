using Capacitor.Cli.Commands;

namespace Capacitor.Cli.Tests.Unit.Commands;

/// <summary>
/// A fully virtual <see cref="FlowRetryClock"/>: time only moves when the code under test asks to
/// wait, or when the test explicitly advances it. Nothing here sleeps, so the settlement/poll retry
/// tests run instantly while still exercising the REAL deadline logic (timeout sources created
/// through the clock genuinely cancel once virtual time passes their expiry) rather than a
/// pre-cancelled token standing in for it.
/// </summary>
internal sealed class VirtualFlowRetryClock : FlowRetryClock {
    readonly object _gate = new();
    readonly List<(DateTimeOffset At, CancellationTokenSource Source)> _timers = [];
    DateTimeOffset _now = new(2026, 7, 27, 12, 0, 0, TimeSpan.Zero);

    /// <summary>Every delay the code under test asked for, in order — the assertable schedule.</summary>
    public List<TimeSpan> Delays { get; } = [];

    public DateTimeOffset StartedAt { get; }

    public VirtualFlowRetryClock() => StartedAt = _now;

    public TimeSpan Elapsed => _now - StartedAt;

    public override DateTimeOffset UtcNow { get { lock (_gate) return _now; } }

    public override Task DelayAsync(TimeSpan delay, CancellationToken ct = default) {
        ct.ThrowIfCancellationRequested();
        lock (_gate) Delays.Add(delay);
        Advance(delay);
        return Task.CompletedTask;
    }

    public override CancellationTokenSource CreateTimeoutSource(TimeSpan timeout) {
        var source = new CancellationTokenSource();

        if (timeout <= TimeSpan.Zero) {
            source.Cancel();
            return source;
        }

        lock (_gate) _timers.Add((_now + timeout, source));
        return source;
    }

    /// <summary>Move virtual time forward, firing any timeout source that comes due. Used by tests
    /// directly and by fake handlers simulating a request that holds server-side.</summary>
    public void Advance(TimeSpan by) {
        if (by <= TimeSpan.Zero) return;

        List<CancellationTokenSource> due = [];
        lock (_gate) {
            _now += by;
            for (var i = _timers.Count - 1; i >= 0; i--) {
                if (_timers[i].At > _now) continue;
                due.Add(_timers[i].Source);
                _timers.RemoveAt(i);
            }
        }

        // Cancel outside the lock: a cancellation callback must never re-enter Advance under _gate.
        foreach (var source in due) {
            try { source.Cancel(); } catch (ObjectDisposedException) { /* the scope already went away */ }
        }
    }
}
