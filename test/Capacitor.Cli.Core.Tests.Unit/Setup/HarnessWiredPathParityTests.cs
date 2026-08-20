using Capacitor.Cli.Core.Harness.Antigravity;
using Capacitor.Cli.Core.Harness.Copilot;
using Capacitor.Cli.Core.Harness.Gemini;
using Capacitor.Cli.Core.Harness.Kiro;
using Capacitor.Cli.Core.Harness.OpenCode;
using Capacitor.Cli.Core.Harness.Pi;

namespace Capacitor.Cli.Core.Tests.Unit.Setup;

/// A pure wiring-probe path that diverges from the production layout silently reports a wired
/// harness as unwired (spurious nudge) while `kcap status` says it's configured. These pin each
/// pure path to the exact production composition so a wrong root/segment fails here.
public class HarnessWiredPathParityTests {
    const string H = "/fake/home";

    [Test]
    public async Task Antigravity_pure_hooks_path_is_under_gui_config_root_not_data_root() {
        var p = AntigravityPaths.GlobalHooksJsonPure(H, geminiCliHome: null);
        await Assert.That(p).IsEqualTo(Path.Combine(H, ".gemini", "config", "plugins", "kcap", "hooks.json"));
        await Assert.That(p).DoesNotContain(Path.Combine(".gemini", "antigravity", "plugins"));
    }

    [Test]
    public async Task Gemini_pure_settings_path_matches_root() {
        await Assert.That(GeminiPaths.SettingsJsonPure(H, null))
            .IsEqualTo(Path.Combine(GeminiPaths.RootPure(H, null), "settings.json"));
    }

    [Test]
    public async Task Copilot_pure_hooks_path_matches_root() {
        await Assert.That(CopilotPaths.KcapHooksJsonPure(H, null))
            .IsEqualTo(Path.Combine(CopilotPaths.RootPure(H, null), "hooks", "kcap.json"));
    }

    [Test]
    public async Task Kiro_pure_agent_path_matches_root() {
        await Assert.That(KiroPaths.KcapAgentJsonPure(H, null))
            .IsEqualTo(Path.Combine(KiroPaths.ConfigRootPure(H, null), "agents", "kcap.json"));
    }

    [Test]
    public async Task Pi_pure_extension_path_matches_root() {
        await Assert.That(PiPaths.KcapExtensionPure(H, null))
            .IsEqualTo(Path.Combine(PiPaths.AgentDirPure(H, null), "extensions", "kcap.ts"));
    }

    [Test]
    public async Task OpenCode_pure_plugin_path_matches_root() {
        await Assert.That(OpenCodePaths.KcapPluginPure(H, null, null))
            .IsEqualTo(Path.Combine(OpenCodePaths.ConfigDirPure(H, null, null), "plugins", "kcap.ts"));
    }
}
