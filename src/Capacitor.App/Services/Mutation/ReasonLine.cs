namespace Capacitor.App.Services.Mutation;

/// Extracts a single machine-readable reason token from daemon/CLI stderr.
public static class ReasonLine {
    // Exactly one matching line with a non-empty token wins; anything else fails closed to null.
    public static string? TrySingle(string stderr, string prefix) {
        string? token = null;
        var matchCount = 0;
        foreach (var rawLine in stderr.Split('\n')) {
            var line = rawLine.TrimEnd('\r');
            if (!line.StartsWith(prefix, StringComparison.Ordinal)) continue;
            matchCount++;
            token = line[prefix.Length..].Trim();
        }
        return matchCount == 1 && !string.IsNullOrEmpty(token) ? token : null;
    }
}
