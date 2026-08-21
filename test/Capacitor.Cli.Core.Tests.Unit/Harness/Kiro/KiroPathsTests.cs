using Capacitor.Cli.Core.Harness.Kiro;

namespace Capacitor.Cli.Core.Tests.Unit.Harness.Kiro;

public class KiroPathsTests {
    // Parallel-safe: asserts an invariant relationship (mcp.json is a sibling of cli.json under
    // settings/), so it holds regardless of how ConfigRoot resolves (KIRO_HOME / home).
    [Test]
    public async Task SettingsMcpJson_is_mcp_json_sibling_of_cli_json() {
        var mcp = KiroPaths.SettingsMcpJson(home: "/fake/home");
        var cli = KiroPaths.SettingsFile(home: "/fake/home");

        await Assert.That(Path.GetFileName(mcp)).IsEqualTo("mcp.json");
        await Assert.That(Path.GetDirectoryName(mcp)).IsEqualTo(Path.GetDirectoryName(cli));
    }

    // Parallel-safe: asserts an invariant relationship (skills/ is a sibling of agents/ under the Kiro
    // config root), so it holds regardless of how ConfigRoot resolves (KIRO_HOME / home). Comparing the
    // GetDirectoryName of two composed paths avoids depending on KIRO_HOME formatting (e.g. a trailing
    // separator). This is the kcap-owned Kiro skills dir — NOT the agent-agnostic ~/.agents/skills.
    [Test]
    public async Task SkillsDir_is_skills_sibling_of_agents_under_kiro_root() {
        var skills = KiroPaths.SkillsDir(home: "/fake/home");
        var agents = KiroPaths.AgentsDir(home: "/fake/home");

        await Assert.That(Path.GetFileName(skills)).IsEqualTo("skills");
        await Assert.That(Path.GetDirectoryName(skills)).IsEqualTo(Path.GetDirectoryName(agents));  // same Kiro root, not ~/.agents
    }
}
