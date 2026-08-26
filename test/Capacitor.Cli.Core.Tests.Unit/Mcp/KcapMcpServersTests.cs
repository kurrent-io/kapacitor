using Capacitor.Cli.Core.Mcp;

namespace Capacitor.Cli.Core.Tests.Unit.Mcp;

public class KcapMcpServersTests {
    [Test]
    public async Task All_contains_the_six_canonical_servers() {
        var names = KcapMcpServers.All.Select(s => s.Name).ToArray();
        await Assert.That(names).IsEquivalentTo(new[] { "kcap-review", "kcap-sessions", "kcap-flows", "kcap-memory", "kcap-workitems", "kcap-analytics" });
    }

    [Test]
    public async Task ForCodex_is_the_full_set_including_workitems() {
        // kcap-workitems is now registered on every harness, so the Codex subset is All.
        var names = KcapMcpServers.ForCodex.Select(s => s.Name).ToArray();
        await Assert.That(names).IsEquivalentTo(new[] { "kcap-review", "kcap-sessions", "kcap-flows", "kcap-memory", "kcap-workitems", "kcap-analytics" });
    }

    [Test]
    public async Task ForCursor_is_the_full_set_including_workitems() {
        // every non-Claude JSON harness now receives kcap-workitems too.
        var names = KcapMcpServers.ForCursor.Select(s => s.Name).ToArray();
        await Assert.That(names).IsEquivalentTo(new[] { "kcap-review", "kcap-sessions", "kcap-flows", "kcap-memory", "kcap-workitems", "kcap-analytics" });
    }

    [Test]
    public async Task ForHarness_stamps_only_the_flows_entry_with_the_driver_vendor() {
        var servers = KcapMcpServers.ForHarness("cursor");

        // Same servers, same order as the bare set — only flows' args change.
        await Assert.That(servers.Select(s => s.Name).ToArray())
            .IsEquivalentTo(KcapMcpServers.ForCursor.Select(s => s.Name).ToArray());

        var flows = servers.Single(s => s.Name == "kcap-flows");
        await Assert.That(flows.Args).IsEquivalentTo(new[] { "mcp", "flows", "--driver", "cursor" });

        // Every non-flows server is byte-identical to the bare set (no accidental stamp elsewhere).
        foreach (var s in servers.Where(s => s.Name != "kcap-flows")) {
            var bare = KcapMcpServers.ForCursor.Single(b => b.Name == s.Name);
            await Assert.That(s.Args).IsEquivalentTo(bare.Args);
        }
    }

    [Test]
    public async Task ForHarness_leaves_the_bare_All_list_unstamped() {
        // ForHarness must not mutate the shared descriptors — the audit/registry read All as the
        // canonical prefix, so a leaked stamp there would misclassify every unstamped entry.
        _ = KcapMcpServers.ForHarness("kiro");
        var flows = KcapMcpServers.All.Single(s => s.Name == "kcap-flows");
        await Assert.That(flows.Args).IsEquivalentTo(new[] { "mcp", "flows" });
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
