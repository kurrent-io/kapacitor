using Capacitor.Cli.Core.Harness;
using Capacitor.Cli.Core.Setup;

namespace Capacitor.Cli.Core.Tests.Unit.Setup;

public class HarnessWiringTests {
    static HarnessPaths Home(string home) => TestHarnessPaths.NoOverrides(new(home));

    [Test]
    public async Task Unknown_vendor_is_not_wired() {
        using var tmp = new TempDir();
        await Assert.That(Home(tmp.Path).IsWired("nope")).IsFalse();
    }

    // Cursor: the hooks installer treats the version marker next to hooks.json as "installed".
    [Test]
    public async Task Cursor_wired_when_hooks_marker_present() {
        using var tmp = new TempDir();
        tmp.CreateDir(".cursor");
        await Assert.That(Home(tmp.Path).IsWired("cursor")).IsFalse();

        tmp.CreateFile([".cursor", ".kcap-hooks-version"], "0.1.0");
        await Assert.That(Home(tmp.Path).IsWired("cursor")).IsTrue();
    }

    // Claude: the wired-check is the enabled-plugin flag in settings.json (the status-line semantics).
    [Test]
    public async Task Claude_wired_when_plugin_enabled_in_settings() {
        using var tmp = new TempDir();
        tmp.CreateDir(".claude");

        tmp.CreateFile([".claude", "settings.json"], """{"enabledPlugins":{"kcap@kcap":false}}""");
        await Assert.That(Home(tmp.Path).IsWired("claude")).IsFalse();

        tmp.CreateFile([".claude", "settings.json"], """{"enabledPlugins":{"kcap@kcap":true}}""");
        await Assert.That(Home(tmp.Path).IsWired("claude")).IsTrue();
    }

    [Test]
    public async Task Claude_not_wired_when_settings_absent() {
        using var tmp = new TempDir();
        await Assert.That(Home(tmp.Path).IsWired("claude")).IsFalse();
    }

    // Detection and the wired-probe consume the SAME snapshot, so a Kiro root taken from the
    // override is what the probe reads, never an ambient KIRO_HOME. Home points elsewhere with no
    // marker, so a true result proves the override drove the probe.
    [Test]
    public async Task Kiro_wired_probe_honors_injected_kiro_home_override() {
        using var tmp = new TempDir();
        tmp.CreateDir("kh");
        tmp.CreateDir(["kh", "agents"]);
        var home  = new UserHome("/nonexistent-home");
        var paths = TestHarnessPaths.NoOverrides(home) with { Kiro = new(home, tmp.PathTo("kh")) };
        await Assert.That(paths.IsWired("kiro")).IsFalse();

        tmp.CreateFile(["kh", "agents", ".kcap-hooks-version"], "0.1.0");
        await Assert.That(paths.IsWired("kiro")).IsTrue();
    }
}
