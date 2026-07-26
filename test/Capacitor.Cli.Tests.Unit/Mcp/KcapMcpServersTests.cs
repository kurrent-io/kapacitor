using Capacitor.Cli.Core.Mcp;

namespace Capacitor.Cli.Tests.Unit.Mcp;

public class KcapMcpServersTests {
    [Test]
    public async Task All_contains_the_six_canonical_servers() {
        var names = KcapMcpServers.All.Select(s => s.Name).ToArray();
        await Assert.That(names).IsEquivalentTo(new[] { "kcap-review", "kcap-sessions", "kcap-flows", "kcap-memory", "kcap-workitems", "kcap-analytics" });
    }

    [Test]
    public async Task ForCodex_excludes_only_workitems() {
        var names = KcapMcpServers.ForCodex.Select(s => s.Name).ToArray();
        await Assert.That(names).IsEquivalentTo(new[] { "kcap-review", "kcap-sessions", "kcap-flows", "kcap-memory", "kcap-analytics" });
    }

    [Test]
    public async Task ForCursor_excludes_only_workitems() {
        var names = KcapMcpServers.ForCursor.Select(s => s.Name).ToArray();
        await Assert.That(names).IsEquivalentTo(new[] { "kcap-review", "kcap-sessions", "kcap-flows", "kcap-memory", "kcap-analytics" });
    }

    [Test]
    public async Task Review_is_the_only_non_repo_scoped_server() {
        var repoScoped = KcapMcpServers.All.Where(s => s.NeedsProjectCwd).Select(s => s.Name).ToArray();
        await Assert.That(repoScoped).IsEquivalentTo(new[] { "kcap-sessions", "kcap-flows", "kcap-memory", "kcap-workitems", "kcap-analytics" });
    }

    [Test]
    public async Task Analytics_is_read_only() {
        // ReadOnly drives Codex per-server trust (auto-approval) — analytics tools are pure reads.
        var analytics = KcapMcpServers.All.Single(s => s.Name == "kcap-analytics");
        await Assert.That(analytics.ReadOnly).IsTrue();
    }
}
