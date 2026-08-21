using Capacitor.Cli.Core.Harness.Copilot;

namespace Capacitor.Cli.Core.Tests.Unit.Harness.Copilot;

public class CopilotPathsTests {
    [Test]
    public async Task McpConfigJson_is_mcp_config_json_under_copilot_root() {
        await Assert.That(CopilotPaths.McpConfigJson("/h"))
            .IsEqualTo(Path.Combine("/h", ".copilot", "mcp-config.json"));
    }

    [Test]
    public async Task McpConfigJson_honors_copilot_home_override() {
        // $COPILOT_HOME (passed here as the copilotHome arg) replaces the entire
        // ~/.copilot path, so the file sits directly under it.
        await Assert.That(CopilotPaths.McpConfigJson("/h", "/custom/loc"))
            .IsEqualTo(Path.Combine("/custom/loc", "mcp-config.json"));
    }

    [Test]
    public async Task InstructionsMd_is_copilot_instructions_under_root() {
        await Assert.That(CopilotPaths.InstructionsMd("/h"))
            .IsEqualTo(Path.Combine("/h", ".copilot", "copilot-instructions.md"));
    }
}
