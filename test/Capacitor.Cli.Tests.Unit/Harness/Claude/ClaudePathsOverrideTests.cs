using Capacitor.Cli.Core.Harness.Claude;

namespace Capacitor.Cli.Tests.Unit.Harness.Claude;

public class ClaudePathsOverrideTests {
    [Test]
    public async Task Config_dir_override_replaces_the_whole_root() {
        var paths = new ClaudePaths(new("/fake/home"), "/relocated/claude");

        await Assert.That(paths.Home).IsEqualTo("/relocated/claude");
        await Assert.That(paths.Projects).IsEqualTo(Path.Combine("/relocated/claude", "projects"));
        await Assert.That(paths.Plans).IsEqualTo(Path.Combine("/relocated/claude", "plans"));
        await Assert.That(paths.UserSettings).IsEqualTo(Path.Combine("/relocated/claude", "settings.json"));
    }

    // The config file follows the override INTO the config dir, unlike its default placement.
    [Test]
    public async Task Config_dir_override_moves_the_user_config_json_inside_it() {
        var paths = new ClaudePaths(new("/fake/home"), "/relocated/claude");

        await Assert.That(paths.UserConfigJson)
            .IsEqualTo(Path.Combine("/relocated/claude", ".claude.json"));
    }

    /// <summary>
    /// By default <c>.claude.json</c> is a SIBLING of the <c>.claude</c> directory, not a child:
    /// <c>&lt;home&gt;/.claude.json</c>, never <c>&lt;home&gt;/.claude/.claude.json</c>.
    /// </summary>
    [Test]
    public async Task Default_layout_puts_the_user_config_json_beside_the_claude_dir() {
        var paths = new ClaudePaths(new("/fake/home"), null);

        await Assert.That(paths.Home).IsEqualTo(Path.Combine("/fake/home", ".claude"));
        await Assert.That(paths.UserConfigJson).IsEqualTo(Path.Combine("/fake/home", ".claude.json"));
    }

    // Bare: CLAUDE_CONFIG_DIR is inherited by any child a concurrent test spawns.
    [Test]
    [NotInParallel]
    public async Task FromEnvironment_reads_CLAUDE_CONFIG_DIR() {
        var relocated = Path.Combine(Path.GetTempPath(), "kcap-claude-cfg");

        using var env = EnvScope.Exclusive("CLAUDE_CONFIG_DIR", relocated);

        var paths = ClaudePaths.FromEnvironment(new("/fake/home"));

        await Assert.That(paths.Home).IsEqualTo(relocated);
        await Assert.That(paths.Projects).IsEqualTo(Path.Combine(relocated, "projects"));
        await Assert.That(paths.Plans).IsEqualTo(Path.Combine(relocated, "plans"));
        await Assert.That(paths.UserSettings).IsEqualTo(Path.Combine(relocated, "settings.json"));
        await Assert.That(paths.UserConfigJson).IsEqualTo(Path.Combine(relocated, ".claude.json"));
    }

    [Test]
    [NotInParallel]
    public async Task FromEnvironment_without_the_override_falls_back_to_the_home() {
        using var env = EnvScope.Exclusive("CLAUDE_CONFIG_DIR", null);

        var paths = ClaudePaths.FromEnvironment(new("/fake/home"));

        await Assert.That(paths.Home).IsEqualTo(Path.Combine("/fake/home", ".claude"));
        await Assert.That(paths.UserConfigJson).IsEqualTo(Path.Combine("/fake/home", ".claude.json"));
    }

}
