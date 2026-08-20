namespace Capacitor.Cli.Core.Setup;

/// <summary>
/// One supported coding-agent harness: its bare vendor id (<c>"antigravity"</c>), its display
/// label, its <c>kcap plugin install</c> flag (<c>"--" + VendorId</c>, or <c>null</c> for flagless
/// Claude), and its <see cref="AgentDetectionResult"/> selector. This is the single Core-side table
/// binding label + flag + detection selector; the desktop app's own view table re-derives from it,
/// and the driver-schema conformance suite pins it against <c>VendorSelection.KnownVendorFlags</c>
/// so a tenth harness fails a test rather than silently missing every nudge surface.
/// </summary>
public sealed record KnownHarness(
    string VendorId, string Label, string? InstallFlag, Func<AgentDetectionResult, DetectedAgent> Select);

public static class HarnessCatalog {
    /// <summary>
    /// Every supported harness, in setup/display order (Claude first). <c>InstallFlag</c> is
    /// <c>"--" + VendorId</c> for the eight flagged vendors and <c>null</c> for flagless Claude —
    /// a relationship the conformance test asserts against the <c>--</c>-prefixed
    /// <c>KnownVendorFlags</c>.
    /// </summary>
    public static readonly IReadOnlyList<KnownHarness> All = [
        new("claude",      "Claude Code", null,            r => r.Claude),
        new("codex",       "Codex",       "--codex",       r => r.Codex),
        new("cursor",      "Cursor",      "--cursor",      r => r.Cursor),
        new("copilot",     "Copilot",     "--copilot",     r => r.Copilot),
        new("gemini",      "Gemini",      "--gemini",      r => r.Gemini),
        new("kiro",        "Kiro",        "--kiro",        r => r.Kiro),
        new("pi",          "Pi",          "--pi",          r => r.Pi),
        new("opencode",    "OpenCode",    "--opencode",    r => r.OpenCode),
        new("antigravity", "Antigravity", "--antigravity", r => r.Antigravity),
    ];

    public static KnownHarness? ById(string vendorId) =>
        All.FirstOrDefault(h => string.Equals(h.VendorId, vendorId, StringComparison.Ordinal));
}
