namespace Capacitor.Cli.Harness.Codex;

/// <summary>
/// Dating a Codex rollout the way its <c>--since</c> does — by the <c>YYYY/MM/DD</c> directory it sits
/// in, which <c>CodexPaths.Discover</c> prunes on and which never consults the file itself.
/// </summary>
internal static class CodexDiscoveryAge {
    /// <summary>Null when the path is not that shape, leaving the caller to fall back.</summary>
    public static DateTimeOffset? DayFromPath(string filePath) {
        var day   = Path.GetDirectoryName(filePath);
        var month = Path.GetDirectoryName(day);
        var year  = Path.GetDirectoryName(month);

        if (day is null || month is null || year is null) return null;

        return int.TryParse(Path.GetFileName(year),  out var y)
            && int.TryParse(Path.GetFileName(month), out var m)
            && int.TryParse(Path.GetFileName(day),   out var d)
            && y is >= 1 and <= 9999 && m is >= 1 and <= 12 && d >= 1 && d <= DateTime.DaysInMonth(y, m)
            ? new DateTimeOffset(new DateTime(y, m, d, 0, 0, 0, DateTimeKind.Utc))
            : null;
    }
}
