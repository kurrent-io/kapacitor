using Capacitor.Cli.Commands;

namespace Capacitor.Cli.Tests.Unit;

public class StatusCommandVersionLineTests {
    [Test]
    public async Task Bundled_line_carries_the_app_marker_and_no_advisory() {
        await Assert.That(StatusCommand.FormatBundledVersionLine("0.12.0-beta.2"))
            .IsEqualTo("kcap 0.12.0-beta.2 (bundled with Kurrent Capacitor)");
    }
}
