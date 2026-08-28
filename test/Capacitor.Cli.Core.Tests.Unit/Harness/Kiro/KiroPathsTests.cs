using Capacitor.Cli.Core.Harness.Kiro;

namespace Capacitor.Cli.Core.Tests.Unit.Harness.Kiro;

public class KiroPathsTests {
    static KiroPaths Under(string home, string? kiroHome = null) => new(new(home), kiroHome);

    // Asserts the sibling relationships rather than the composed strings, so they hold however the
    // config root resolved (KIRO_HOME formatting included, e.g. a trailing separator).
    [Test]
    public async Task Settings_mcp_json_is_a_sibling_of_cli_json() {
        var paths = Under("/fake/home");

        await Assert.That(Path.GetFileName(paths.SettingsMcpJson)).IsEqualTo("mcp.json");
        await Assert.That(Path.GetDirectoryName(paths.SettingsMcpJson))
            .IsEqualTo(Path.GetDirectoryName(paths.SettingsFile));
    }

    // This is the kcap-owned Kiro skills dir — NOT the agent-agnostic ~/.agents/skills.
    [Test]
    public async Task Skills_dir_is_a_sibling_of_agents_under_the_kiro_root() {
        var paths = Under("/fake/home");

        await Assert.That(Path.GetFileName(paths.SkillsDir)).IsEqualTo("skills");
        await Assert.That(Path.GetDirectoryName(paths.SkillsDir))
            .IsEqualTo(Path.GetDirectoryName(paths.AgentsDir));
    }

    [Test]
    public async Task Kiro_home_override_replaces_the_config_root() {
        await Assert.That(Under("/h", "/custom/kiro").ConfigRoot).IsEqualTo("/custom/kiro");
        await Assert.That(Under("/h").ConfigRoot).IsEqualTo(Path.Combine("/h", ".kiro"));
    }
}
