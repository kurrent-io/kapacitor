namespace Capacitor.App.Services;

/// One resolved CLI's identity: null Version disables skew detection for the run (multiline,
/// malformed, or "unknown" `--version` output, or the query itself failing).
public sealed record CliInfo(string? Path, string? Version);

/// Where the app finds `kcap` to shell out to (spec §3.1, decision 1: everything through the
/// CLI). Pure given its env/filesystem seams, so DaemonClientService.CreateDefaultAsync and every
/// later lifecycle feature resolve through the same logic.
public static class CliResolver {
    /// KCAP_APP_CLI_PATH (dev seam, app-shell design decision 6) → *(future: bundle-relative
    /// path arm lands here)* → "kcap" on PATH.
    ///
    /// Returns null ONLY when the override is set but the path it names does not exist — a broken
    /// override must not silently fall back to PATH resolution, since that would make the dev seam
    /// lie about which binary actually ran. Every other case (no override set, or an empty one)
    /// returns bare "kcap" unconditionally: PATH resolution, and "no CLI at all", are the OS's job
    /// at spawn time, surfaced by the caller's own RunAsync failure/timeout handling.
    public static string? ResolvePath(Func<string, string?> getEnv, Func<string, bool> fileExists) {
        var overridePath = getEnv("KCAP_APP_CLI_PATH");
        if (string.IsNullOrEmpty(overridePath)) return "kcap";

        return fileExists(overridePath) ? overridePath : null;
    }

    /// Strict: stdout must be exactly one non-empty line "kcap &lt;version&gt;"; multiline, a
    /// missing "kcap " prefix, or a bare/"unknown" version all disable skew detection (null)
    /// rather than let a malformed value flow into a version comparison.
    public static string? ParseVersion(string stdout) {
        var lines = stdout.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (lines.Length != 1) return null;

        const string prefix = "kcap ";
        if (!lines[0].StartsWith(prefix, StringComparison.Ordinal)) return null;

        var version = lines[0][prefix.Length..];
        return version is "" or "unknown" ? null : version;
    }
}
