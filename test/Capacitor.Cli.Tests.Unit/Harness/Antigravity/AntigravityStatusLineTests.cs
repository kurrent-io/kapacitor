using Capacitor.Cli.Commands;
using Capacitor.Cli.Core.Harness;

namespace Capacitor.Cli.Tests.Unit.Harness.Antigravity;

/// <summary>
/// The `kcap status` hooks line reports Antigravity alongside the other vendors.
/// </summary>
public class AntigravityStatusLineTests {
    [Test]
    public async Task Status_line_shows_antigravity_installed_state() {
        var installed = StatusCommand.BuildHooksStatusLine([(HarnessId.Antigravity, true)]);
        await Assert.That(installed).Contains("Antigravity ✓");

        var missing = StatusCommand.BuildHooksStatusLine([(HarnessId.Antigravity, false)]);
        await Assert.That(missing).Contains("Antigravity ✗");
    }
}
