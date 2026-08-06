namespace Capacitor.App;

/// Compact agent-uptime text (spec §8): seconds-only under a minute, minutes-only under an hour,
/// hours+minutes under a day, days+hours at or above a day. A zero remainder in a two-unit bucket
/// drops the second unit ("2h", not "2h 0m"). Negative input (clock skew, or CreatedAt landing a
/// tick in the future) clamps to "0s" rather than printing a negative or garbage string.
public static class UptimeFormat {
    public static string Format(TimeSpan uptime) {
        if (uptime < TimeSpan.Zero) uptime = TimeSpan.Zero;

        var totalSeconds = (long)uptime.TotalSeconds;
        if (totalSeconds < 60) return $"{totalSeconds}s";

        var totalMinutes = totalSeconds / 60;
        if (totalMinutes < 60) return $"{totalMinutes}m";

        var totalHours = totalMinutes / 60;
        if (totalHours < 24) {
            var minutes = totalMinutes % 60;
            return minutes == 0 ? $"{totalHours}h" : $"{totalHours}h {minutes}m";
        }

        var days = totalHours / 24;
        var hours = totalHours % 24;
        return hours == 0 ? $"{days}d" : $"{days}d {hours}h";
    }
}
