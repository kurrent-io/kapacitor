using Capacitor.Cli.Core.Harness.OpenCode;

namespace Capacitor.Cli.Core.Tests.Unit.Harness.OpenCode;

public class OpenCodePathsTests {
    static OpenCodePaths Oc(string home, string? configDir = null,
                            string? xdgConfigHome = null, string? xdgDataHome = null) =>
        new(new(home), configDir, xdgConfigHome, xdgDataHome);

    [Test]
    public async Task Config_dir_precedence_is_override_then_xdg_then_home() {
        await Assert.That(Oc("/fake/home").ConfigDir)
            .IsEqualTo(Path.Combine("/fake/home", ".config", "opencode"));
        await Assert.That(Oc("/fake/home", xdgConfigHome: "/xdg").ConfigDir)
            .IsEqualTo(Path.Combine("/xdg", "opencode"));
        // OPENCODE_CONFIG_DIR wins over XDG, and is taken verbatim rather than as a parent.
        await Assert.That(Oc("/fake/home", configDir: "/relocated.Path/oc", xdgConfigHome: "/xdg").ConfigDir)
            .IsEqualTo("/relocated.Path/oc");
    }

    [Test]
    public async Task Data_dir_precedence_is_xdg_then_home() {
        await Assert.That(Oc("/h").DataDir).IsEqualTo(Path.Combine("/h", ".local", "share", "opencode"));
        await Assert.That(Oc("/h", xdgDataHome: "/xdgd").DataDir).IsEqualTo(Path.Combine("/xdgd", "opencode"));
    }

    [Test]
    public async Task Files_sit_under_the_config_dir() {
        var paths = Oc("/fake/home", configDir: "/oc");

        await Assert.That(paths.McpConfigJson).IsEqualTo(Path.Combine("/oc", "opencode.json"));
        await Assert.That(paths.AgentsMd).IsEqualTo(Path.Combine("/oc", "AGENTS.md"));
        await Assert.That(paths.KcapPlugin).IsEqualTo(Path.Combine("/oc", "plugins", "kcap.ts"));
    }

    // Bare: these three are inherited by any child a concurrent test spawns.
    [Test]
    [NotInParallel]
    public async Task FromEnvironment_reads_the_three_overrides() {
        using var relocated = new TempDir();

        using var cfg  = EnvScope.Exclusive("OPENCODE_CONFIG_DIR", relocated.Path);
        using var xdgC = EnvScope.Exclusive("XDG_CONFIG_HOME", "/xdg");
        using var xdgD = EnvScope.Exclusive("XDG_DATA_HOME", "/xdgd");

        var paths = OpenCodeHarness.FromEnvironment(new("/fake/home")).Paths;

        await Assert.That(paths.ConfigDir).IsEqualTo(relocated.Path);
        await Assert.That(paths.KcapPlugin).IsEqualTo(relocated.PathTo("plugins", "kcap.ts"));
        await Assert.That(paths.DataDir).IsEqualTo(Path.Combine("/xdgd", "opencode"));
    }
}
