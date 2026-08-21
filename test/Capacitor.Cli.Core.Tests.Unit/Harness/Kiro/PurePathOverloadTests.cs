using Capacitor.Cli.Core.Harness.Kiro;
using Capacitor.Cli.Core.Harness.OpenCode;

namespace Capacitor.Cli.Core.Tests.Unit.Harness.Kiro;

public class PurePathOverloadTests {
    [Test]
    public async Task KiroPaths_honors_injected_kiro_home_without_env() {
        var root = KiroPaths.ConfigRoot(home: "/h", kiroHome: "/custom/kiro");
        await Assert.That(root).IsEqualTo("/custom/kiro");
        await Assert.That(KiroPaths.ConfigRoot(home: "/h", kiroHome: null))
            .IsEqualTo(Path.Combine("/h", ".kiro"));
    }

    [Test]
    public async Task OpenCodePaths_honors_injected_xdg_values_without_env() {
        await Assert.That(OpenCodePaths.ConfigDir(home: "/h", configDir: null, xdgConfigHome: "/xdgc"))
            .IsEqualTo(Path.Combine("/xdgc", "opencode"));
        await Assert.That(OpenCodePaths.DataDir(home: "/h", xdgDataHome: "/xdgd"))
            .IsEqualTo(Path.Combine("/xdgd", "opencode"));
        await Assert.That(OpenCodePaths.DataDir(home: "/h", xdgDataHome: null))
            .IsEqualTo(Path.Combine("/h", ".local", "share", "opencode"));
    }

    [Test]
    public async Task Injected_values_run_in_parallel_without_process_env_mutation() {
        var results = await Task.WhenAll(Enumerable.Range(0, 32).Select(i => Task.Run(() =>
            KiroPaths.IsInstalled(home: $"/nonexistent-{i}", kiroHome: null))));
        await Assert.That(results.All(r => r == false)).IsTrue();
    }
}
