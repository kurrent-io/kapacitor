namespace Capacitor.Cli.Commands;

/// <summary>
/// One hook invocation's wall-clock ceiling and a safety-adjusted "remaining", both measured from
/// process entry (<see cref="HookClock"/>) so the hook always leaves time to spool + exit before the
/// host's kill.
/// </summary>
public sealed class HookBudget(HookClock clock, TimeSpan ceiling) {
    public static readonly TimeSpan Safety = TimeSpan.FromMilliseconds(1500);

    /// <inheritdoc cref="HookClock.Time"/>
    public TimeProvider Time => clock.Time;

    /// <summary>What work may still be started: the ceiling less what has elapsed, less the
    /// reserve the hook needs to spool and exit before the host's kill.</summary>
    public TimeSpan Remaining => Floor(ceiling - clock.Elapsed - Safety);

    /// <summary>Time until the ceiling itself. Deliberately does NOT hold back <see cref="Safety"/>
    /// the way <see cref="Remaining"/> does: the reserve exists so WORK stops early enough to spool
    /// and exit, so arming the hard cap on it as well would spend the reserve on the cap instead of
    /// on the exit it was reserved for — and whatever the hook still had to finish would race a
    /// cancellation the moment its work budget ran out.</summary>
    public TimeSpan UntilCeiling => Floor(ceiling - clock.Elapsed);

    /// <summary>Cancels at the ceiling, on this budget's own clock. With
    /// <see cref="CeilingReached"/>, the two primitives a dispatcher needs to RACE an abandonable
    /// task against the cap rather than only ask what work it may still start — the Cursor hook's
    /// case, which must stay the sole writer of stdout and so cannot simply cancel and await.</summary>
    public CancellationTokenSource CancelAtCeiling() => new(UntilCeiling, Time);

    /// <inheritdoc cref="CancelAtCeiling"/>
    public Task CeilingReached(CancellationToken ct = default) => Task.Delay(UntilCeiling, Time, ct);

    static TimeSpan Floor(TimeSpan value) => value > TimeSpan.Zero ? value : TimeSpan.Zero;
}
