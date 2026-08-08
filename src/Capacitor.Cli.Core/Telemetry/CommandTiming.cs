using System.Diagnostics;

namespace Capacitor.Cli.Core.Telemetry;

/// <summary>Wall-clock duration of a command, in milliseconds, from a
/// <see cref="Stopwatch.GetTimestamp"/> reading. Clamped at zero so a clock adjustment can
/// never produce a negative duration in the data.</summary>
public static class CommandTiming {
    public static long ElapsedMs(long startTimestamp) {
        var elapsed = Stopwatch.GetElapsedTime(startTimestamp, Stopwatch.GetTimestamp());

        return Math.Max(0, (long)elapsed.TotalMilliseconds);
    }
}
