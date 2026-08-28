namespace Capacitor.Cli.Core.Harness;

/// <summary>
/// The closed set of harnesses kcap supports, in setup and display order. Closed on purpose: our
/// own per-vendor decisions — install flags, wizard order, which import source to build — are
/// exhaustive switches over this, so adding a harness fails the build in each place that owes a
/// decision (CS8509 is an error here) instead of silently defaulting.
/// </summary>
public enum HarnessId {
    Claude,
    Codex,
    Cursor,
    Copilot,
    Gemini,
    Kiro,
    Pi,
    OpenCode,
    Antigravity,
}
