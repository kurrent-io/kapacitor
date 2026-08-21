namespace Capacitor.Cli.Core.Tests.Unit;

public class KcapMcpRegistryTests {
    [Test]
    public async Task Resolve_KnownId_ReturnsDescriptor() {
        var result = KcapMcpRegistry.Resolve("kcap-review");

        await Assert.That(result).IsNotNull();
    }

    [Test]
    public async Task Resolve_KnownId_CanonicalLowercaseId() {
        var result = KcapMcpRegistry.Resolve("kcap-review");

        await Assert.That(result!.Id).IsEqualTo("kcap-review");
    }

    [Test]
    public async Task Resolve_KnownId_CorrectArgs() {
        var result = KcapMcpRegistry.Resolve("kcap-review");

        await Assert.That(result!.Args).IsEquivalentTo(new[] { "mcp", "review" });
    }

    [Test]
    public async Task Resolve_KcapReview_StartsFlowsFalse() {
        var result = KcapMcpRegistry.Resolve("kcap-review");

        await Assert.That(result!.StartsFlows).IsFalse();
    }

    [Test]
    public async Task Resolve_KcapSessions_ReturnsDescriptor() {
        var result = KcapMcpRegistry.Resolve("kcap-sessions");

        await Assert.That(result).IsNotNull();
    }

    [Test]
    public async Task Resolve_KcapSessions_CorrectArgs() {
        var result = KcapMcpRegistry.Resolve("kcap-sessions");

        await Assert.That(result!.Args).IsEquivalentTo(new[] { "mcp", "sessions" });
    }

    [Test]
    public async Task Resolve_KcapSessions_StartsFlowsFalse() {
        var result = KcapMcpRegistry.Resolve("kcap-sessions");

        await Assert.That(result!.StartsFlows).IsFalse();
    }

    [Test]
    public async Task Resolve_KcapMemory_ReturnsDescriptor() {
        var result = KcapMcpRegistry.Resolve("kcap-memory");

        await Assert.That(result).IsNotNull();
    }

    [Test]
    public async Task Resolve_KcapMemory_CorrectArgs() {
        var result = KcapMcpRegistry.Resolve("kcap-memory");

        await Assert.That(result!.Args).IsEquivalentTo(new[] { "mcp", "memory" });
    }

    [Test]
    public async Task Resolve_KcapMemory_StartsFlowsFalse() {
        var result = KcapMcpRegistry.Resolve("kcap-memory");

        await Assert.That(result!.StartsFlows).IsFalse();
    }

    [Test]
    public async Task Resolve_KcapWorkItems_ReturnsDescriptor() {
        var result = KcapMcpRegistry.Resolve("kcap-workitems");

        await Assert.That(result).IsNotNull();
    }

    [Test]
    public async Task Resolve_KcapWorkItems_CorrectArgs() {
        var result = KcapMcpRegistry.Resolve("kcap-workitems");

        await Assert.That(result!.Args).IsEquivalentTo(new[] { "mcp", "workitems" });
    }

    [Test]
    public async Task Resolve_KcapWorkItems_StartsFlowsFalse() {
        var result = KcapMcpRegistry.Resolve("kcap-workitems");

        await Assert.That(result!.StartsFlows).IsFalse();
    }

    [Test]
    public async Task Resolve_KcapFlows_ReturnsDescriptor() {
        var result = KcapMcpRegistry.Resolve("kcap-flows");

        await Assert.That(result).IsNotNull();
    }

    [Test]
    public async Task Resolve_KcapFlows_CorrectArgs() {
        var result = KcapMcpRegistry.Resolve("kcap-flows");

        await Assert.That(result!.Args).IsEquivalentTo(new[] { "mcp", "flows" });
    }

    [Test]
    public async Task Resolve_KcapFlows_StartsFlowsTrue() {
        var result = KcapMcpRegistry.Resolve("kcap-flows");

        await Assert.That(result!.StartsFlows).IsTrue();
    }

    [Test]
    public async Task Resolve_CaseInsensitive_UpperCase() {
        var result = KcapMcpRegistry.Resolve("KCAP-REVIEW");

        await Assert.That(result).IsNotNull();
        await Assert.That(result!.Id).IsEqualTo("kcap-review");
    }

    [Test]
    public async Task Resolve_CaseInsensitive_MixedCase() {
        var result = KcapMcpRegistry.Resolve("KcAp-ReViEw");

        await Assert.That(result).IsNotNull();
        await Assert.That(result!.Id).IsEqualTo("kcap-review");
    }

    [Test]
    public async Task Resolve_Trimmed_WithLeadingWhitespace() {
        var result = KcapMcpRegistry.Resolve("  kcap-review");

        await Assert.That(result).IsNotNull();
    }

    [Test]
    public async Task Resolve_Trimmed_WithTrailingWhitespace() {
        var result = KcapMcpRegistry.Resolve("kcap-review  ");

        await Assert.That(result).IsNotNull();
    }

    [Test]
    public async Task Resolve_Unknown_ReturnsNull() {
        var result = KcapMcpRegistry.Resolve("unknown-server");

        await Assert.That(result).IsNull();
    }

    [Test]
    public async Task Resolve_KcapFlowResult_ReturnsNull() {
        var result = KcapMcpRegistry.Resolve("kcap-flow-result");

        await Assert.That(result).IsNull();
    }

    [Test]
    public async Task Resolve_KcapFlowResult_InjectionOnly() {
        // kcap-flow-result is deliberately NOT in the registry
        // It is injection-only, never allowlistable
        var result = KcapMcpRegistry.Resolve("kcap-flow-result");

        await Assert.That(result).IsNull();
    }

    [Test]
    public async Task Resolve_EmptyString_ReturnsNull() {
        var result = KcapMcpRegistry.Resolve("");

        await Assert.That(result).IsNull();
    }

    [Test]
    public async Task Resolve_Whitespace_ReturnsNull() {
        var result = KcapMcpRegistry.Resolve("   ");

        await Assert.That(result).IsNull();
    }

    [Test]
    public async Task Resolve_Null_ReturnsNull() {
        var result = KcapMcpRegistry.Resolve(null);

        await Assert.That(result).IsNull();
    }

    [Test]
    public async Task Resolve_TwoSpaceWhitespace_ReturnsNull() {
        var result = KcapMcpRegistry.Resolve("  ");

        await Assert.That(result).IsNull();
    }
}
