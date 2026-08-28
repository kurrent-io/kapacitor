using Capacitor.Cli.Core.Harness.Copilot;

namespace Capacitor.Cli.Core.Tests.Unit.Harness.Copilot;

public class CopilotPathsTests {
    static CopilotPaths Under(string home, string? copilotHome = null) => new(new(home), copilotHome);

    [Test]
    public async Task Files_resolve_under_the_copilot_root() {
        var paths = Under("/h");

        await Assert.That(paths.McpConfigJson).IsEqualTo(Path.Combine("/h", ".copilot", "mcp-config.json"));
        await Assert.That(paths.InstructionsMd)
            .IsEqualTo(Path.Combine("/h", ".copilot", "copilot-instructions.md"));
        await Assert.That(paths.KcapHooksJson).IsEqualTo(Path.Combine("/h", ".copilot", "hooks", "kcap.json"));
    }

    [Test]
    public async Task Copilot_home_override_replaces_the_whole_root() {
        // $COPILOT_HOME replaces the entire ~/.copilot path, so the files sit directly under it.
        var paths = Under("/h", "/custom/loc");

        await Assert.That(paths.McpConfigJson).IsEqualTo(Path.Combine("/custom/loc", "mcp-config.json"));
        await Assert.That(paths.KcapHooksJson).IsEqualTo(Path.Combine("/custom/loc", "hooks", "kcap.json"));
    }
}
