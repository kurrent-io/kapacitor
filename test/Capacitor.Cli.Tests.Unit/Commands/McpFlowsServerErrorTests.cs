using Capacitor.Cli.Commands;

namespace Capacitor.Cli.Tests.Unit.Commands;

public class McpFlowsServerErrorTests {
    [Test]
    public async Task FormatFlowStartError_renders_catching_up_guidance() {
        var body = """{"error":"server_catching_up","message":"Flows are temporarily unavailable while the server rebuilds its read models (42% complete). Retry later or ask the user how to proceed."}""";

        var text = McpFlowsServer.FormatFlowStartError(409, body, wasDynamicStart: false);

        await Assert.That(text).Contains("server_catching_up");
        await Assert.That(text).Contains("42%");
        await Assert.That(text).Contains("try again in a few minutes");
    }
}
