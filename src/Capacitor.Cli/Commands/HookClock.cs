namespace Capacitor.Cli.Commands;

/// <summary>
/// When this hook invocation started, on the clock its deadlines are measured by.
/// </summary>
/// <remarks>
/// Construct once at process entry and pass it down. Every ceiling is relative to it, and anchoring
/// inside a handler would restart the clock after the pre-dispatch work — config load, the git probe
/// behind ResolveForRepo, the global spool drain, the stdin read — has already been paid for, so
/// every budget would be over-generous by exactly the part of a hook that is slowest.
/// </remarks>
public sealed class HookClock(TimeProvider time) {
    readonly long _start = time.GetTimestamp();

    /// <summary>The clock this hook is timed by — the one its other deadlines must share, or a test
    /// can fake one and still be timed by the other.</summary>
    public TimeProvider Time => time;

    /// <summary>
    /// What this invocation may spend end to end. The ceiling belongs to the hook that named it:
    /// event names are the vendor's to choose, so a table here keyed on them would hand one
    /// vendor's timeout to another vendor's identically-named event.
    /// </summary>
    public HookBudget Budget(TimeSpan ceiling) => new(this, ceiling);

    internal TimeSpan Elapsed => time.GetElapsedTime(_start);
}
