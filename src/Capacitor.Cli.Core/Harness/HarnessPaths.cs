using Capacitor.Cli.Core.Harness.Antigravity;
using Capacitor.Cli.Core.Harness.Claude;
using Capacitor.Cli.Core.Harness.Codex;
using Capacitor.Cli.Core.Harness.Copilot;
using Capacitor.Cli.Core.Harness.Cursor;
using Capacitor.Cli.Core.Harness.Gemini;
using Capacitor.Cli.Core.Harness.Kiro;
using Capacitor.Cli.Core.Harness.OpenCode;
using Capacitor.Cli.Core.Harness.Pi;

namespace Capacitor.Cli.Core.Harness;

/// <summary>
/// Every vendor's layout as this process sees it, resolved once. Members are eager because each
/// vendor constructor is string composition with no I/O: a recomputing property would read its
/// override variable again per access, so two members of one vendor could name different roots.
///
/// <para><see cref="FromEnvironment"/> calls each vendor's own factory rather than reading the
/// variables here — the vendor owns which variable it honours. For the same reason there is no
/// constructor taking the overrides: that is the flattened bag this type replaces.</para>
///
/// <para>This is a view of the operator's machine, never a reviewer's. A launch that points a child
/// at an isolated vendor home builds its paths from that directory (see <c>KiroReviewerHome</c>,
/// <c>OpenCodeReviewerConfigDir</c>, <c>AntigravityReviewerHome</c>).</para>
///
/// <para>A record so one vendor can be substituted with a <c>with</c> expression; equality is by
/// member reference and nothing compares two bundles.</para>
/// </summary>
public sealed record HarnessPaths {
    public required ClaudePaths      Claude      { get; init; }
    public required CodexPaths       Codex       { get; init; }
    public required CursorPaths      Cursor      { get; init; }
    public required CopilotPaths     Copilot     { get; init; }
    public required GeminiPaths      Gemini      { get; init; }
    public required KiroPaths        Kiro        { get; init; }
    public required PiPaths          Pi          { get; init; }
    public required OpenCodePaths    OpenCode    { get; init; }
    public required AntigravityPaths Antigravity { get; init; }
    public required AgentsPaths      Agents      { get; init; }

    public static HarnessPaths FromEnvironment(UserHome home) {
        // Antigravity's whole layout hangs off Gemini's root, so it is composed from the SAME
        // instance: two derivations from one variable could otherwise disagree.
        var gemini = GeminiPaths.FromEnvironment(home);

        return new() {
            Claude      = ClaudePaths.FromEnvironment(home),
            Codex       = CodexPaths.FromEnvironment(home),
            Cursor      = CursorPaths.FromEnvironment(home),
            Copilot     = CopilotPaths.FromEnvironment(home),
            Gemini      = gemini,
            Antigravity = new AntigravityPaths(gemini),
            Kiro        = KiroPaths.FromEnvironment(home),
            Pi          = PiPaths.FromEnvironment(home),
            OpenCode    = OpenCodePaths.FromEnvironment(home),
            Agents      = new AgentsPaths(home),
        };
    }
}
