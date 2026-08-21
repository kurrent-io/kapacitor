namespace Capacitor.Cli.Commands;

/// <summary>Best-effort inference of the DRIVER harness — the coding agent running this MCP server —
/// from the per-process env a harness exports into its children. Only harnesses with a verified,
/// distinctive marker are inferred; anything else returns null so the skill's unknown-driver fallback
/// never claims a "different model". No marker is invented: an unverified or ambiguous (nested)
/// harness stays null rather than risk naming the wrong vendor. Uses the same env evidence as
/// <see cref="Capacitor.Cli.HarnessRequesterContext"/>, kept here so the reviewer-vendor tool can
/// echo <c>driver_vendor</c> without widening that type's contract.</summary>
public static class DriverVendor {
    public static string? Infer() => Infer(Environment.GetEnvironmentVariable);

    internal static string? Infer(Func<string, string?> getEnv) {
        var claude = !string.IsNullOrWhiteSpace(getEnv(HarnessRequesterContext.ClaudeSessionIdVar));
        var codex  = !string.IsNullOrWhiteSpace(getEnv(HarnessRequesterContext.CodexThreadIdVar));

        // Co-present markers mean one harness is nested inside another — neither is provably the
        // driver, so decline to guess (mirrors HarnessRequesterContext's own nesting stance).
        if (claude && !codex) return "claude";
        if (codex && !claude) return "codex";
        return null;
    }
}
