using System.Diagnostics;

namespace Capacitor.Cli.Commands;

/// <summary>
/// Per-event hook-timeout ceilings (mirror kcap/hooks/hooks.json) and a
/// safety-adjusted "remaining" computed from a process-start timestamp, so the
/// hook always leaves time to spool + exit before Claude's kill.
///
/// <para>Claude honours the hooks.json timeout for every event except SessionEnd, which it
/// caps at 1.5 s for plugin hooks; that hook hands off to a detached continuation
/// (<see cref="Cli.Harness.Claude.ClaudeSessionEndHandoff"/>), and the 15 s here is the
/// continuation's budget, not the hook's.</para>
/// </summary>
public static class HookBudget {
    public static readonly TimeSpan Safety = TimeSpan.FromMilliseconds(1500);

    public static TimeSpan Ceiling(string command) => command switch {
        "session-end" => TimeSpan.FromSeconds(15),
        _             => TimeSpan.FromSeconds(5),
    };

    public static TimeSpan Remaining(long processStartTimestamp, string command) {
        var rem = Ceiling(command) - Stopwatch.GetElapsedTime(processStartTimestamp) - Safety;
        return rem > TimeSpan.Zero ? rem : TimeSpan.Zero;
    }
}
