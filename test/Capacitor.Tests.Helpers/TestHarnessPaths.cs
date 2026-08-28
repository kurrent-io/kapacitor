using Capacitor.Cli.Core;
using Capacitor.Cli.Core.Harness;
using Capacitor.Cli.Core.Harness.Antigravity;
using Capacitor.Cli.Core.Harness.Cursor;
using Capacitor.Cli.Core.Harness.Gemini;

namespace Capacitor.Tests.Helpers;

public static class TestHarnessPaths {
    /// <summary>
    /// Every vendor rooted at <paramref name="home"/> with no override exported — the bundle a
    /// machine with a bare environment resolves. Substitute one vendor with a <c>with</c>
    /// expression: <c>NoOverrides(home) with { Kiro = new(home, elsewhere) }</c>.
    /// </summary>
    /// <remarks>Cursor takes the host's platform, as <c>CursorPaths.FromEnvironment</c> does; a test
    /// pinning another OS's layout passes its own <see cref="CursorPaths"/>.</remarks>
    public static HarnessPaths NoOverrides(UserHome home) {
        var gemini = new GeminiPaths(home, null);

        return new() {
            Claude      = new(home, null),
            Codex       = new(home, null),
            Cursor      = CursorHarness.FromEnvironment(home).Paths,
            Copilot     = new(home, null),
            Gemini      = gemini,
            Antigravity = new AntigravityPaths(gemini),
            Kiro        = new(home, null),
            Pi          = new(home, null),
            OpenCode    = new(home, null, null, null),
            Agents      = new AgentsPaths(home),
        };
    }
}
