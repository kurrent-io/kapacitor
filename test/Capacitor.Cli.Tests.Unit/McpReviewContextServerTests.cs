using Capacitor.Cli.Commands;

namespace Capacitor.Cli.Tests.Unit;

public class McpReviewContextServerTests {
    [Test]
    [Arguments("http://127.0.0.1:1234/0123456789abcdef0123456789abcdef/review-context/workspace-mcp-configs", true)]
    [Arguments("http://localhost:1234/0123456789abcdef0123456789abcdef/review-context/workspace-mcp-configs", false)]
    [Arguments("https://127.0.0.1:1234/0123456789abcdef0123456789abcdef/review-context/workspace-mcp-configs", false)]
    [Arguments("http://127.0.0.1:1234/0123456789abcdef0123456789abcdef/review-context/workspace-mcp-configs?path=.mcp.json", false)]
    [Arguments("http://127.0.0.1:1234/0123456789abcdef0123456789abcdef/review-context/workspace-mcp-configs/extra", false)]
    public async Task Capability_url_validation_is_exact(string url, bool expected) {
        await Assert.That(McpReviewContextServer.TryValidateCapabilityUrl(url, out _))
            .IsEqualTo(expected);
    }

    [Test]
    public async Task Context_mode_exposes_exactly_one_argumentless_tool() {
        var tools = McpReviewContextServer.BuildToolsList();
        await Assert.That(tools.Length).IsEqualTo(1);
        await Assert.That(tools[0].Name)
            .IsEqualTo("get_branch_authored_mcp_configs");
        await Assert.That(tools[0].InputSchema.Properties).IsEmpty();
        await Assert.That(tools[0].InputSchema.Required).IsEmpty();
        await Assert.That(tools[0].Description).Contains("untrusted branch-authored");
    }
}
