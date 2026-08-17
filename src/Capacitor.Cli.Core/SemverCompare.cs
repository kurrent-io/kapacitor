namespace Capacitor.Cli.Core;

/// <summary>
/// Strict-greater semver-ish comparator. Strips <c>-prerelease</c> and
/// <c>+buildmetadata</c> from both sides before parsing the remaining
/// dotted triplet with <see cref="System.Version.TryParse(string?, out System.Version?)"/>. Any
/// unparseable, <c>null</c>, empty, whitespace, or literal
/// <c>"unknown"</c> input returns <c>false</c> — i.e. "we don't know,
/// so don't claim newer". Authoritative for both the in-agent upgrade
/// nudge and the stderr update hint.
/// </summary>
public static class SemverCompare {
    public static bool IsNewer(string? latest, string? current) {
        var l = ParseCore(latest);
        var c = ParseCore(current);
        if (l is null || c is null) return false;
        return l > c;
    }

    static Version? ParseCore(string? raw) {
        if (string.IsNullOrWhiteSpace(raw)) return null;
        if (string.Equals(raw, "unknown", StringComparison.Ordinal)) return null;

        var s = raw;

        var plus = s.IndexOf('+');
        if (plus >= 0) s = s[..plus];

        var dash = s.IndexOf('-');
        if (dash >= 0) s = s[..dash];

        return Version.TryParse(s, out var parsed) ? parsed : null;
    }
}
