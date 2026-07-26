using Capacitor.Cli.Daemon.Services;

namespace Capacitor.Cli.Tests.Unit.Codex;

/// <summary>
/// Parser-level coverage for the review-flow reviewer's MCP enumeration authority. The full
/// process-spawn (<c>codex mcp list --json</c>) is exercised manually against the real binary; here
/// we pin the pure parse of its payload — including that ALL sources (config.toml servers, a native
/// plugin-provided server, a dotted-name server) are surfaced, which is the High 1 / High 2 gap the
/// config-only enumeration missed — and that malformed output fails CLOSED (throws) rather than
/// silently dropping servers.
/// </summary>
public class CodexMcpInventoryTests {
    // A representative `codex mcp list --json` payload: a config.toml server, a plugin-provided
    // server (no config transport), and a DOTTED-name server — the three shapes the hardened
    // enumeration must surface so every one gets disabled for the reviewer.
    const string SampleJson = """
        [
          { "name": "kcap-flows", "enabled": true, "disabled_reason": null,
            "transport": { "type": "stdio", "command": "kcap", "args": ["mcp", "flows"] } },
          { "name": "corp.flows", "enabled": true, "disabled_reason": null,
            "transport": { "type": "stdio", "command": "kcap", "args": ["mcp", "flows"] } },
          { "name": "sites-design-picker", "enabled": true, "disabled_reason": null,
            "transport": { "type": "stdio", "command": "node", "args": ["./mcp/server.mjs"] } },
          { "name": "node_repl", "enabled": false, "disabled_reason": null,
            "transport": { "type": "stdio", "command": "node_repl" } }
        ]
        """;

    [Test]
    public async Task ParseServerNames_surfaces_config_plugin_and_dotted_servers() {
        var names = CodexMcpInventory.ParseServerNames(SampleJson);

        await Assert.That(names).Contains("kcap-flows");           // config.toml
        await Assert.That(names).Contains("corp.flows");           // dotted (High 2)
        await Assert.That(names).Contains("sites-design-picker");  // plugin-provided (High 1)
        await Assert.That(names).Contains("node_repl");            // even a disabled one is reported
        await Assert.That(names.Count).IsEqualTo(4);
    }

    [Test]
    public async Task ParseServerNames_empty_array_returns_empty() {
        await Assert.That(CodexMcpInventory.ParseServerNames("[]")).IsEmpty();
    }

    [Test]
    [Arguments("not json at all")]
    [Arguments("{ \"name\": \"x\" }")]      // a JSON object, not the expected array
    [Arguments("[ { \"enabled\": true } ]")] // array element missing a name
    [Arguments("[ { \"name\": 42 } ]")]      // non-string name
    [Arguments("[ { \"name\": \"\" } ]")]    // empty name
    public async Task ParseServerNames_fails_closed_on_malformed_output(string payload) {
        await Assert.That(() => CodexMcpInventory.ParseServerNames(payload))
            .Throws<CodexReviewerMcpIsolationException>();
    }
}
