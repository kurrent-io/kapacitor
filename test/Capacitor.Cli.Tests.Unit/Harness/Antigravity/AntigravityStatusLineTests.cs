using Capacitor.Cli.Commands;

namespace Capacitor.Cli.Tests.Unit.Harness.Antigravity;

/// <summary>
/// The `kcap status` hooks line reports Antigravity alongside the other vendors.
/// </summary>
public class AntigravityStatusLineTests {
    [Test]
    public async Task Status_line_shows_antigravity_installed_state() {
        var installed = StatusCommand.BuildHooksStatusLine(
            claude: true, codex: true, cursor: true, copilot: true,
            gemini: true, kiro: true, pi: true, opencode: true, antigravity: true);
        await Assert.That(installed).Contains("Antigravity ✓");

        var missing = StatusCommand.BuildHooksStatusLine(
            claude: false, codex: false, cursor: false, copilot: false,
            gemini: false, kiro: false, pi: false, opencode: false, antigravity: false);
        await Assert.That(missing).Contains("Antigravity ✗");
    }
}
