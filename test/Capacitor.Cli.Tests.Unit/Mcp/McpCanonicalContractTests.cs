using System.Text.Json.Nodes;
using Capacitor.Cli.Core.Mcp;

namespace Capacitor.Cli.Tests.Unit.Mcp;

public class McpCanonicalContractTests {
    // Resolve the repo's kcap/ dir relative to the test assembly (…/src/cli).
    static string KcapDir() {
        var d = new DirectoryInfo(AppContext.BaseDirectory);
        for (; d is not null; d = d.Parent)
            if (Directory.Exists(Path.Combine(d.FullName, "kcap")) &&
                File.Exists(Path.Combine(d.FullName, "kcap", ".mcp.json")))
                return Path.Combine(d.FullName, "kcap");
        throw new DirectoryNotFoundException("kcap/ not found above test base dir");
    }

    static string[] Keys(string file) =>
        [.. ((JsonObject)JsonNode.Parse(File.ReadAllText(Path.Combine(KcapDir(), file)))!["mcpServers"]!)
            .Select(kv => kv.Key)];

    [Test]
    public async Task Bundled_claude_mcp_json_matches_the_canonical_list() {
        await Assert.That(Keys(".mcp.json"))
            .IsEquivalentTo(KcapMcpServers.All.Select(s => s.Name).ToArray());
    }

    [Test]
    public async Task Every_canonical_server_has_a_description() {
        // kcap-sessions previously had a null Description. Pin that every canonical server
        // carries one so the routing/discoverability gap can't silently reopen.
        foreach (var s in KcapMcpServers.All)
            await Assert.That(string.IsNullOrWhiteSpace(s.Description))
                .IsFalse().Because($"{s.Name} must have a non-empty Description");
    }

    [Test]
    public async Task Every_bundled_claude_mcp_server_has_a_description() {
        var servers = (JsonObject)JsonNode.Parse(File.ReadAllText(Path.Combine(KcapDir(), ".mcp.json")))!["mcpServers"]!;
        foreach (var (name, node) in servers)
            await Assert.That(string.IsNullOrWhiteSpace(node?["description"]?.GetValue<string>()))
                .IsFalse().Because($"{name} in .mcp.json must have a non-empty description");
    }

    [Test]
    public async Task Bundled_codex_mcp_json_matches_the_codex_subset() {
        await Assert.That(Keys(".codex-mcp.json"))
            .IsEquivalentTo(KcapMcpServers.ForCodex.Select(s => s.Name).ToArray());
    }

    [Test]
    public async Task Codex_subset_keeps_flows_and_memory() {
        var names = KcapMcpServers.ForCodex.Select(s => s.Name).ToArray();
        await Assert.That(names).Contains("kcap-flows");
        await Assert.That(names).Contains("kcap-memory");
    }

    [Test]
    public async Task Codex_subset_excludes_workitems() {
        var names = KcapMcpServers.ForCodex.Select(s => s.Name).ToArray();
        await Assert.That(names).DoesNotContain("kcap-workitems");
    }

    [Test]
    public async Task Codex_subset_includes_analytics() {
        var names = KcapMcpServers.ForCodex.Select(s => s.Name).ToArray();
        await Assert.That(names).Contains("kcap-analytics");
    }

    [Test]
    public async Task Cursor_subset_excludes_workitems_but_keeps_flows_and_memory() {
        var names = KcapMcpServers.ForCursor.Select(s => s.Name).ToArray();
        await Assert.That(names).DoesNotContain("kcap-workitems");
        await Assert.That(names).Contains("kcap-flows");
        await Assert.That(names).Contains("kcap-memory");
    }

    [Test]
    public async Task Cursor_subset_includes_analytics() {
        // kcap-analytics resolves repo context from the process CWD, so it rides the same
        // writer path as kcap-sessions and is registered for every non-Claude JSON harness.
        var names = KcapMcpServers.ForCursor.Select(s => s.Name).ToArray();
        await Assert.That(names).Contains("kcap-analytics");
    }
}
