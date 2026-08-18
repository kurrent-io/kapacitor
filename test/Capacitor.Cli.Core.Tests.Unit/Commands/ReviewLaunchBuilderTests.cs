using Capacitor.Cli.Core.Commands;

namespace Capacitor.Cli.Core.Tests.Unit.Commands;

public class ReviewLaunchBuilderTests {
    [Test]
    public async Task Claude_writes_mcp_config_file_and_populates_descriptor() {
        var launch = await ReviewLaunchBuilder.BuildAsync(
            vendor: "claude", cliPath: "/opt/kcap", baseUrl: "https://srv",
            owner: "acme", repo: "widgets", prNumber: 42);

        try {
            await Assert.That(launch.McpConfigPath).IsNotNull();
            await Assert.That(File.Exists(launch.McpConfigPath!)).IsTrue();
            var json = await File.ReadAllTextAsync(launch.McpConfigPath!);
            await Assert.That(json).Contains("kcap-review");
            await Assert.That(json).Contains("/opt/kcap");

            await Assert.That(launch.Mcp.Command).IsEqualTo("/opt/kcap");
            await Assert.That(launch.Mcp.Args).Contains("--owner");
            await Assert.That(launch.Mcp.Args).Contains("acme");
            await Assert.That(launch.Mcp.Args).Contains("42");
            await Assert.That(launch.Mcp.Env["KCAP_URL"]).IsEqualTo("https://srv");
            await Assert.That(launch.SystemPrompt).Contains("acme");
        } finally {
            if (launch.McpConfigPath is not null) File.Delete(launch.McpConfigPath);
        }
    }

    [Test]
    public async Task Codex_writes_no_file_and_populates_descriptor() {
        var launch = await ReviewLaunchBuilder.BuildAsync(
            vendor: "codex", cliPath: "/opt/kcap", baseUrl: "https://srv",
            owner: "acme", repo: "widgets", prNumber: 42);

        await Assert.That(launch.McpConfigPath).IsNull();
        await Assert.That(launch.Mcp.Command).IsEqualTo("/opt/kcap");
        await Assert.That(launch.Mcp.Args).Contains("review");
        await Assert.That(launch.Mcp.Args).Contains("widgets");
        await Assert.That(launch.Mcp.Env["KCAP_URL"]).IsEqualTo("https://srv");
        await Assert.That(launch.SystemPrompt).Contains("widgets");
    }
}
