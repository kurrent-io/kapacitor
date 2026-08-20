using Capacitor.Cli.Core.Setup;

namespace Capacitor.Cli.Core.Tests.Unit.Setup;

public class HarnessIntegrationProbeTests {
    static AgentDetectionInputs Home(string home) =>
        new(PathEnv: null, PathExt: null, IsWindows: false, Home: home);

    [Test]
    public async Task Unknown_vendor_is_not_wired() {
        using var tmp = new TempDir();
        await Assert.That(HarnessIntegrationProbe.IsWired("nope", Home(tmp.Path))).IsFalse();
    }

    // Cursor: the hooks installer treats the version marker next to hooks.json as "installed".
    [Test]
    public async Task Cursor_wired_when_hooks_marker_present() {
        using var tmp = new TempDir();
        tmp.CreateDir(".cursor");
        await Assert.That(HarnessIntegrationProbe.IsWired("cursor", Home(tmp.Path))).IsFalse();

        tmp.CreateFile([".cursor", ".kcap-hooks-version"], "0.1.0");
        await Assert.That(HarnessIntegrationProbe.IsWired("cursor", Home(tmp.Path))).IsTrue();
    }

    // Claude: the wired-check is the enabled-plugin flag in settings.json (the status-line semantics).
    [Test]
    public async Task Claude_wired_when_plugin_enabled_in_settings() {
        using var tmp = new TempDir();
        tmp.CreateDir(".claude");

        tmp.CreateFile([".claude", "settings.json"], """{"enabledPlugins":{"kcap@kcap":false}}""");
        await Assert.That(HarnessIntegrationProbe.IsWired("claude", Home(tmp.Path))).IsFalse();

        tmp.CreateFile([".claude", "settings.json"], """{"enabledPlugins":{"kcap@kcap":true}}""");
        await Assert.That(HarnessIntegrationProbe.IsWired("claude", Home(tmp.Path))).IsTrue();
    }

    [Test]
    public async Task Claude_not_wired_when_settings_absent() {
        using var tmp = new TempDir();
        await Assert.That(HarnessIntegrationProbe.IsWired("claude", Home(tmp.Path))).IsFalse();
    }

    // Detection and the wired-probe must consume the SAME injected snapshot: an injected KiroHome
    // override is honored by the wired-probe (via the pure path helper), not silently replaced by an
    // ambient KIRO_HOME. Home points elsewhere with no marker, so a true result proves the override
    // (not Home) drove the probe.
    [Test]
    public async Task Kiro_wired_probe_honors_injected_kiro_home_override() {
        using var tmp = new TempDir();
        tmp.CreateDir("kh");
        tmp.CreateDir(["kh", "agents"]);
        var inputs = Home("/nonexistent-home") with { KiroHome = tmp.PathTo("kh") };
        await Assert.That(HarnessIntegrationProbe.IsWired("kiro", inputs)).IsFalse();

        tmp.CreateFile(["kh", "agents", ".kcap-hooks-version"], "0.1.0");
        await Assert.That(HarnessIntegrationProbe.IsWired("kiro", inputs)).IsTrue();
    }
}
