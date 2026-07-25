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
    // server (no config transport), a DOTTED-name server, and a url-based (streamable_http) server
    // — the four shapes the hardened enumeration must surface so every one gets disabled for the
    // reviewer (the url shape needs its transport carried through — AI-1519).
    const string SampleJson = """
        [
          { "name": "kcap-flows", "enabled": true, "disabled_reason": null,
            "transport": { "type": "stdio", "command": "kcap", "args": ["mcp", "flows"] } },
          { "name": "corp.flows", "enabled": true, "disabled_reason": null,
            "transport": { "type": "stdio", "command": "kcap", "args": ["mcp", "flows"] } },
          { "name": "sites-design-picker", "enabled": true, "disabled_reason": null,
            "transport": { "type": "stdio", "command": "node", "args": ["./mcp/server.mjs"] } },
          { "name": "node_repl", "enabled": false, "disabled_reason": null,
            "transport": { "type": "stdio", "command": "node_repl" } },
          { "name": "linear", "enabled": true, "disabled_reason": null,
            "transport": { "type": "streamable_http", "url": "https://mcp.linear.app/mcp",
                           "bearer_token_env_var": null } }
        ]
        """;

    [Test]
    public async Task ParseServers_surfaces_config_plugin_dotted_and_url_servers() {
        var servers = CodexMcpInventory.ParseServers(SampleJson);
        var byName  = servers.ToDictionary(s => s.Name);

        await Assert.That(byName.Keys).Contains("kcap-flows");           // config.toml
        await Assert.That(byName.Keys).Contains("corp.flows");           // dotted (High 2)
        await Assert.That(byName.Keys).Contains("sites-design-picker");  // plugin-provided (High 1)
        await Assert.That(byName.Keys).Contains("node_repl");            // even a disabled one is reported
        await Assert.That(servers.Count).IsEqualTo(5);

        // The url transport is carried through for the streamable_http server, and only for it.
        await Assert.That(byName["linear"].Url).IsEqualTo("https://mcp.linear.app/mcp");
        await Assert.That(byName["kcap-flows"].Url).IsNull();
        await Assert.That(byName["node_repl"].Url).IsNull();
    }

    [Test]
    [Arguments("""[ { "name": "x" } ]""")]                                       // no transport at all
    [Arguments("""[ { "name": "x", "transport": null } ]""")]                    // null transport
    [Arguments("""[ { "name": "x", "transport": { "url": 42 } } ]""")]           // non-string url
    [Arguments("""[ { "name": "x", "transport": { "url": "" } } ]""")]           // empty url
    public async Task ParseServers_treats_missing_or_unusable_url_as_null(string payload) {
        // Best-effort url: either shape still yields a DISABLING override downstream, so an odd
        // transport must not fail the enumeration — it just selects the sentinel-command shape.
        var servers = CodexMcpInventory.ParseServers(payload);

        await Assert.That(servers.Count).IsEqualTo(1);
        await Assert.That(servers[0].Url).IsNull();
    }

    [Test]
    public async Task ParseServers_empty_array_returns_empty() {
        await Assert.That(CodexMcpInventory.ParseServers("[]")).IsEmpty();
    }

    [Test]
    [Arguments("not json at all")]
    [Arguments("{ \"name\": \"x\" }")]      // a JSON object, not the expected array
    [Arguments("[ { \"enabled\": true } ]")] // array element missing a name
    [Arguments("[ { \"name\": 42 } ]")]      // non-string name
    [Arguments("[ { \"name\": \"\" } ]")]    // empty name
    public async Task ParseServers_fails_closed_on_malformed_output(string payload) {
        await Assert.That(() => CodexMcpInventory.ParseServers(payload))
            .Throws<CodexReviewerMcpIsolationException>();
    }
}
