using System.Globalization;

namespace Capacitor.App.ViewModels;

public static class RelativeTime {
    /// "just now", then minutes, hours and days for a week, then the date. Never negative: a stamp
    /// ahead of the clock reads as now.
    public static string Format(DateTimeOffset at, DateTimeOffset now) {
        var age = now - at;
        if (age < TimeSpan.FromMinutes(1)) return "just now";
        if (age < TimeSpan.FromHours(1))   return $"{(int)age.TotalMinutes}m ago";
        if (age < TimeSpan.FromDays(1))    return $"{(int)age.TotalHours}h ago";
        if (age < TimeSpan.FromDays(7))    return $"{(int)age.TotalDays}d ago";
        return at.ToString(at.Year == now.Year ? "MMM d" : "MMM d, yyyy", CultureInfo.InvariantCulture);
    }
}
