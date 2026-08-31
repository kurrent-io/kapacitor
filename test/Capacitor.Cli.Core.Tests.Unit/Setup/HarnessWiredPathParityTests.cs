using Capacitor.Cli.Core.Harness.Antigravity;

namespace Capacitor.Cli.Core.Tests.Unit.Setup;

/// A wiring-probe path that diverges from the production layout silently reports a wired harness as
/// unwired (spurious nudge) while `kcap status` says it's configured.
public class HarnessWiredPathParityTests {
    [Test]
    public async Task Antigravity_hooks_path_is_under_the_gui_config_root_not_the_data_root() {
        const string home = "/fake/home";

        var p = new AntigravityPaths(new(home), null).GlobalHooksJson;

        await Assert.That(p).IsEqualTo(Path.Combine(home, ".gemini", "config", "plugins", "kcap", "hooks.json"));
        await Assert.That(p).DoesNotContain(Path.Combine(".gemini", "antigravity", "plugins"));
    }
}
