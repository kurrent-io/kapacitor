using System.Text.Json.Nodes;
using Capacitor.Cli.Commands;
using Capacitor.Cli.Core;
using Capacitor.Cli.Core.Mcp;

namespace Capacitor.Cli.Tests.Unit;

[NotInParallel("HomeEnvVarMutation")]
public class PluginCommandCursorTests {
    [Test]
    public async Task install_cursor_if_installed_noops_when_marker_absent() {
        using var tmp = new FakeUserHome();
        var hooksPath = Path.Combine(tmp.Path, "hooks.json");

        var exit = await PluginCommand.HandleAsync(
            ["plugin", "install", "--cursor", "--if-installed", "--cursor-hooks-path", hooksPath]);
        await Assert.That(exit).IsEqualTo(0);
        await Assert.That(File.Exists(hooksPath)).IsFalse();
    }

    [Test]
    public async Task install_cursor_if_installed_short_circuits_on_same_version_marker() {
        using var tmp = new FakeUserHome();
        var hooksPath = Path.Combine(tmp.Path, "hooks.json");
        CursorHooksInstaller.WriteMarker(hooksPath);
        File.WriteAllText(hooksPath, "{}");

        var marker = CursorHooksInstaller.ReadMarker(hooksPath);
        await Assert.That(marker).IsEqualTo(CapacitorVersion.Current());

        var exit = await PluginCommand.HandleAsync(
            ["plugin", "install", "--cursor", "--if-installed", "--cursor-hooks-path", hooksPath]);
        await Assert.That(exit).IsEqualTo(0);
        await Assert.That(File.ReadAllText(hooksPath)).IsEqualTo("{}");
    }

    // `plugin install/remove --cursor` also (un)registers the kcap MCP
    // servers in ~/.cursor/mcp.json. These use an explicit PluginEnvironment
    // (not PluginEnvironment.FromProcess()) so env.CursorMcpJson resolves under
    // a temp home instead of the real machine's ~/.cursor — mirrors
    // PluginCommandCodexInstallIntegrationTests.TestEnv. The `--if-installed`
    // refresh branch is used (pre-marker hooks.json seeded) rather than a bare
    // `install --cursor`, so the AgentDetector "kcap on PATH" precheck (which
    // only runs on the non-refresh path) never comes into play.
    [Test]
    public async Task install_cursor_registers_mcp_servers_preserving_user_entries() {
        using var fakeHome = new FakeUserHome();
        var cursorDir = System.IO.Path.Combine(fakeHome.Path, ".cursor");
        Directory.CreateDirectory(cursorDir);

        var hooksPath = System.IO.Path.Combine(cursorDir, "hooks.json");
        await File.WriteAllTextAsync(hooksPath, """
            {"version":1,"hooks":{"sessionStart":[{"command":"kcap hook --cursor"}]}}
            """);

        var mcpPath = System.IO.Path.Combine(cursorDir, "mcp.json");
        await File.WriteAllTextAsync(mcpPath, """
            {"mcpServers":{"my-tool":{"command":"my-tool","args":["serve"]}}}
            """);

        var exit = await PluginCommand.HandleAsync(
            ["plugin", "install", "--cursor", "--if-installed"],
            TestEnv(fakeHome.Path));
        await Assert.That(exit).IsEqualTo(0);

        var root    = JsonNode.Parse(await File.ReadAllTextAsync(mcpPath))!.AsObject();
        var servers = root["mcpServers"]!.AsObject();
        await Assert.That(servers["kcap-review"]!["command"]!.GetValue<string>()).IsEqualTo("kcap");
        await Assert.That(servers.Select(kv => kv.Key)).Contains("kcap-sessions");
        await Assert.That(servers.Select(kv => kv.Key)).Contains("kcap-flows");
        await Assert.That(servers.Select(kv => kv.Key)).Contains("kcap-memory");
        // kcap-workitems is Claude Code plugin only — not auto-registered for Cursor.
        await Assert.That(servers.Select(kv => kv.Key)).DoesNotContain("kcap-workitems");
        // kcap-analytics rides the same CWD-resolved writer path as kcap-sessions.
        await Assert.That(servers.Select(kv => kv.Key)).Contains("kcap-analytics");
        await Assert.That(servers["my-tool"]).IsNotNull(); // user server preserved
    }

    [Test]
    public async Task install_cursor_if_installed_does_not_write_mcp_json_when_never_opted_in() {
        using var fakeHome = new FakeUserHome();

        // Hooks were never installed, so the refresh-only postinstall path
        // no-ops before ever touching hooks.json OR mcp.json.
        var exit = await PluginCommand.HandleAsync(
            ["plugin", "install", "--cursor", "--if-installed"],
            TestEnv(fakeHome.Path));
        await Assert.That(exit).IsEqualTo(0);

        var mcpPath = System.IO.Path.Combine(fakeHome.Path, ".cursor", "mcp.json");
        await Assert.That(File.Exists(mcpPath)).IsFalse();
    }

    [Test]
    public async Task install_cursor_skip_cursor_mcp_flag_leaves_mcp_json_untouched() {
        using var fakeHome = new FakeUserHome();
        var cursorDir = System.IO.Path.Combine(fakeHome.Path, ".cursor");
        Directory.CreateDirectory(cursorDir);

        var hooksPath = System.IO.Path.Combine(cursorDir, "hooks.json");
        await File.WriteAllTextAsync(hooksPath, """
            {"version":1,"hooks":{"sessionStart":[{"command":"kcap hook --cursor"}]}}
            """);

        var exit = await PluginCommand.HandleAsync(
            ["plugin", "install", "--cursor", "--if-installed", "--skip-cursor-mcp"],
            TestEnv(fakeHome.Path));
        await Assert.That(exit).IsEqualTo(0);

        var mcpPath = System.IO.Path.Combine(cursorDir, "mcp.json");
        await Assert.That(File.Exists(mcpPath)).IsFalse();
    }

    [Test]
    public async Task remove_cursor_unregisters_mcp_servers_preserving_user_entries() {
        using var fakeHome = new FakeUserHome();
        var cursorDir = System.IO.Path.Combine(fakeHome.Path, ".cursor");
        Directory.CreateDirectory(cursorDir);

        var hooksPath = System.IO.Path.Combine(cursorDir, "hooks.json");
        await File.WriteAllTextAsync(hooksPath, """
            {"version":1,"hooks":{"sessionStart":[{"command":"kcap hook --cursor"}]}}
            """);

        // Seed mcp.json exactly as a prior real install would have (so the
        // sidecar ownership marker exists), then splice in a user-authored
        // server that must survive removal.
        var mcpPath = System.IO.Path.Combine(cursorDir, "mcp.json");
        JsonMcpConfigWriter.Register(mcpPath, KcapMcpServers.All, McpConfigShape.Standard, cwd: null, new McpMarker("cursor"));
        var seeded = JsonNode.Parse(await File.ReadAllTextAsync(mcpPath))!.AsObject();
        seeded["mcpServers"]!["my-tool"] = JsonNode.Parse("""{"command":"my-tool","args":["serve"]}""");
        await File.WriteAllTextAsync(mcpPath, seeded.ToJsonString());

        var exit = await PluginCommand.HandleAsync(
            ["plugin", "remove", "--cursor"], TestEnv(fakeHome.Path));
        await Assert.That(exit).IsEqualTo(0);

        var root    = JsonNode.Parse(await File.ReadAllTextAsync(mcpPath))!.AsObject();
        var servers = root["mcpServers"]!.AsObject();
        var keys    = servers.Select(kv => kv.Key).ToArray();
        await Assert.That(keys).DoesNotContain("kcap-review");
        await Assert.That(keys).DoesNotContain("kcap-sessions");
        await Assert.That(keys).DoesNotContain("kcap-flows");
        await Assert.That(keys).DoesNotContain("kcap-memory");
        await Assert.That(servers["my-tool"]).IsNotNull(); // user server preserved
    }

    [Test]
    public async Task remove_cursor_retains_marker_on_failed_unregister_then_retry_removes_entries() {
        using var fakeHome = new FakeUserHome();
        var cursorDir = System.IO.Path.Combine(fakeHome.Path, ".cursor");
        Directory.CreateDirectory(cursorDir);

        // A prior real install: kcap entries + sidecar ownership marker.
        var mcpPath = System.IO.Path.Combine(cursorDir, "mcp.json");
        JsonMcpConfigWriter.Register(mcpPath, KcapMcpServers.All, McpConfigShape.Standard, cwd: null, new McpMarker("cursor"));
        var installed = await File.ReadAllTextAsync(mcpPath); // valid content to restore after the "fix"
        await Assert.That(new McpMarker("cursor").Owned(mcpPath).ToArray()).IsNotEmpty();

        // The config is temporarily malformed/unreadable → Unregister fails-closed.
        await File.WriteAllTextAsync(mcpPath, "{ not valid json");

        var failExit = await PluginCommand.HandleAsync(["plugin", "remove", "--cursor"], TestEnv(fakeHome.Path));
        await Assert.That(failExit).IsEqualTo(1);                                          // failed MCP unregister propagates
        await Assert.That(new McpMarker("cursor").Owned(mcpPath).ToArray()).IsNotEmpty();  // marker RETAINED for retry

        // User fixes the file (kcap entries intact); the retry now succeeds and cleans up.
        await File.WriteAllTextAsync(mcpPath, installed);
        var retryExit = await PluginCommand.HandleAsync(["plugin", "remove", "--cursor"], TestEnv(fakeHome.Path));
        await Assert.That(retryExit).IsEqualTo(0);

        var root    = JsonNode.Parse(await File.ReadAllTextAsync(mcpPath))!.AsObject();
        var servers = root["mcpServers"] as JsonObject;
        var keys    = servers?.Select(kv => kv.Key).ToArray() ?? [];
        await Assert.That(keys).DoesNotContain("kcap-review");
        await Assert.That(new McpMarker("cursor").Owned(mcpPath).ToArray()).IsEmpty();     // marker cleared after clean removal
    }

    static PluginEnvironment TestEnv(string fakeHome) => new(
        HomeDirectory:     fakeHome,
        ResolvePluginPath: () => null,
        Stdout:            TextWriter.Null,
        Stderr:            TextWriter.Null
    );

}
