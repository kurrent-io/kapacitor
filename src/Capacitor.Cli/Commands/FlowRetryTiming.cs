namespace Capacitor.Cli.Commands;

/// <summary>
/// The injectable timing seam shared by the flows MCP server's two retry lanes —
/// <c>SendWithSettlementRetryAsync</c> (POST) and <c>PollUntilTerminalAsync</c> (poll). Every clock
/// read, delay and timeout source routes through here so tests drive a virtual clock instead of
/// wall-clock sleeps or pre-cancelled tokens standing in for the real deadline logic. Production is
/// backed by <see cref="TimeProvider.System"/>. See
/// docs/superpowers/specs/2026-07-25-ai1526-concurrent-launch-settlement-admission-design.md (3.2 G).
/// </summary>
internal class FlowRetryClock {
    readonly TimeProvider _time;

    public FlowRetryClock(TimeProvider? time = null) => _time = time ?? TimeProvider.System;

    /// <summary>The production clock — real time, real timers.</summary>
    public static FlowRetryClock System { get; } = new();

    public virtual DateTimeOffset UtcNow => _time.GetUtcNow();

    /// <summary>A non-positive delay completes synchronously (a fully truncated backoff, or a budget
    /// that expired while the previous attempt was in flight, must not schedule a timer).</summary>
    public virtual Task DelayAsync(TimeSpan delay, CancellationToken ct = default) =>
        delay <= TimeSpan.Zero ? Task.CompletedTask : Task.Delay(delay, _time, ct);

    /// <summary>A standalone timeout source — the per-GET bound in the poll lane, and the raw
    /// deadline half of <see cref="CreateDeadline"/>.</summary>
    public virtual CancellationTokenSource CreateTimeoutSource(TimeSpan timeout) => new(timeout, _time);

    /// <summary>A deadline linked to the caller's token. The returned scope keeps the two sources
    /// distinguishable, which is the whole point: the retry helper must return its
    /// deadline-exhausted result ONLY when its own deadline fired, and rethrow caller cancellation
    /// untouched.</summary>
    public FlowDeadlineScope CreateDeadline(TimeSpan remaining, CancellationToken caller) =>
        new(CreateTimeoutSource(remaining), caller);
}

/// <summary>A per-attempt deadline token linked to the caller's token, which stays able to say
/// WHICH of the two fired. Disposing releases both sources.</summary>
internal sealed class FlowDeadlineScope : IDisposable {
    readonly CancellationTokenSource _deadline;
    readonly CancellationTokenSource _linked;

    internal FlowDeadlineScope(CancellationTokenSource deadline, CancellationToken caller) {
        _deadline = deadline;
        _linked   = CancellationTokenSource.CreateLinkedTokenSource(caller, deadline.Token);
    }

    /// <summary>Hand this to the request — it cancels on either the caller's token or the deadline.</summary>
    public CancellationToken Token => _linked.Token;

    /// <summary>True only when THIS scope's own deadline elapsed, never when the caller cancelled.</summary>
    public bool DeadlineFired => _deadline.IsCancellationRequested;

    public void Dispose() {
        _linked.Dispose();
        _deadline.Dispose();
    }
}

/// <summary>
/// The delay SCHEDULE shared by both flow retry lanes. Pinned formula, so the schedule is
/// assertable rather than incidental: for retry <c>n</c> (1-based)
/// <c>raw(n) = min(10s, 500ms · 2^(n−1))</c> — the cap applies BEFORE jitter — then equal jitter
/// <c>delay(n) = raw(n)/2 + U(0, raw(n)/2)</c>, then truncation to the caller's remaining budget.
/// First retry is therefore 250–500ms and steady state 5–10s.
///
/// <para>Only the schedule is shared: each lane keeps its own budget semantics and passes its own
/// remaining budget in, so a policy delay can never overshoot either. See
/// docs/superpowers/specs/2026-07-25-ai1526-concurrent-launch-settlement-admission-design.md (3.2 G).</para>
/// </summary>
internal sealed class SettlementBackoff {
    internal static readonly TimeSpan BaseDelay = TimeSpan.FromMilliseconds(500);
    internal static readonly TimeSpan MaxDelay  = TimeSpan.FromSeconds(10);

    // Bounded exponent: retry ordinals are unbounded (the POST lane stops on elapsed time, the poll
    // lane on PollCap), and 2^n overflows to Infinity long before either budget runs out. Clamping
    // keeps Raw() total — the min() below would still cap it, but only if the multiply stays finite.
    const int MaxExponent = 30;

    readonly Func<double> _jitter;

    /// <param name="jitter">Uniform sample in [0,1). Defaults to <see cref="Random.Shared"/>.</param>
    public SettlementBackoff(Func<double>? jitter = null) => _jitter = jitter ?? Random.Shared.NextDouble;

    public static SettlementBackoff Default { get; } = new();

    /// <summary>Deterministic instance for tests — <see cref="Random"/> is not thread-safe, but a
    /// backoff is only ever consulted from one lane at a time.</summary>
    internal static SettlementBackoff Seeded(int seed) {
        var rng = new Random(seed);
        return new SettlementBackoff(rng.NextDouble);
    }

    /// <summary>The un-jittered, already-capped base for retry <paramref name="retry"/> (1-based).</summary>
    internal static TimeSpan Raw(int retry) {
        if (retry < 1) retry = 1;
        var scaled = BaseDelay * Math.Pow(2, Math.Min(retry - 1, MaxExponent));
        return scaled > MaxDelay ? MaxDelay : scaled;
    }

    /// <summary>The delay before retry <paramref name="retry"/> (1-based), truncated to
    /// <paramref name="remainingBudget"/>. A non-positive budget yields zero — the caller is expected
    /// to have already decided to stop, and must never be pushed past its own bound by this schedule.</summary>
    public TimeSpan Delay(int retry, TimeSpan remainingBudget) {
        var raw      = Raw(retry);
        var half     = raw.Ticks / 2;
        var jittered = half + (long)(_jitter() * half);

        if (remainingBudget <= TimeSpan.Zero) return TimeSpan.Zero;

        return TimeSpan.FromTicks(Math.Min(jittered, remainingBudget.Ticks));
    }
}
