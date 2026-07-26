using System.Text.Json.Nodes;
using Capacitor.Cli.Commands;
using Capacitor.Cli.Core;
using Capacitor.Cli.Core.Copilot;
using Capacitor.Cli.Core.Instructions;
using Capacitor.Cli.Core.Mcp;

namespace Capacitor.Cli.Tests.Unit;

// `plugin install/remove --copilot` also (un)registers the kcap MCP servers in
// ~/.copilot/mcp-config.json (McpConfigShape.Copilot → per-entry type:"stdio").
// These use an explicit PluginEnvironment rooted at a FakeUserHome, and clear
// COPILOT_HOME for the test's duration so CopilotPaths resolves under the fake
// home (COPILOT_HOME otherwise replaces the entire ~/.copilot path). The
// `--if-installed` refresh branch is used (hooks pre-seeded, version marker
// dropped) so the "kcap on PATH" precheck on the fresh-install path never runs.
[NotInParallel("HomeEnvVarMutation")]
public class PluginCommandCopilotTests {
    [Test]
    public async Task install_copilot_registers_mcp_servers_preserving_user_entries() {
        using var _    = new EnvScope("COPILOT_HOME", null);
        using var home = new FakeUserHome();
        var env = TestEnv(home.Path);

        // Installed-but-stale hooks so `--if-installed` refreshes (and registers MCP).
        PluginCommand.InstallCopilotHooks(env.CopilotKcapHooksJson);
        CopilotHooksInstaller.DeleteMarker(env.CopilotKcapHooksJson);

        // A user-authored MCP server that must survive registration.
        await File.WriteAllTextAsync(env.CopilotMcpConfigJson, """
            {"mcpServers":{"my-tool":{"type":"stdio","command":"my-tool","args":["serve"]}}}
            """);

        var exit = await PluginCommand.HandleAsync(["plugin", "install", "--copilot", "--if-installed"], env);
        await Assert.That(exit).IsEqualTo(0);

        var root    = JsonNode.Parse(await File.ReadAllTextAsync(env.CopilotMcpConfigJson))!.AsObject();
        var servers = root["mcpServers"]!.AsObject();
        await Assert.That(servers["kcap-review"]!["command"]!.GetValue<string>()).IsEqualTo("kcap");
        await Assert.That(servers["kcap-review"]!["type"]!.GetValue<string>()).IsEqualTo("stdio");
        await Assert.That(servers.Select(kv => kv.Key)).Contains("kcap-sessions");
        await Assert.That(servers.Select(kv => kv.Key)).Contains("kcap-flows");
        await Assert.That(servers.Select(kv => kv.Key)).Contains("kcap-memory");
        await Assert.That(servers.Select(kv => kv.Key)).Contains("kcap-analytics");
        await Assert.That(servers["my-tool"]).IsNotNull(); // user server preserved
    }

    [Test]
    public async Task install_copilot_skip_flag_leaves_mcp_config_untouched() {
        using var _    = new EnvScope("COPILOT_HOME", null);
        using var home = new FakeUserHome();
        var env = TestEnv(home.Path);

        PluginCommand.InstallCopilotHooks(env.CopilotKcapHooksJson);
        CopilotHooksInstaller.DeleteMarker(env.CopilotKcapHooksJson);

        var exit = await PluginCommand.HandleAsync(
            ["plugin", "install", "--copilot", "--if-installed", "--skip-copilot-mcp"], env);
        await Assert.That(exit).IsEqualTo(0);

        await Assert.That(File.Exists(env.CopilotMcpConfigJson)).IsFalse();
    }

    [Test]
    public async Task install_copilot_if_installed_does_not_write_mcp_config_when_never_opted_in() {
        using var _    = new EnvScope("COPILOT_HOME", null);
        using var home = new FakeUserHome();
        var env = TestEnv(home.Path);

        // No hooks seeded → --if-installed no-ops before touching hooks OR mcp-config.
        var exit = await PluginCommand.HandleAsync(["plugin", "install", "--copilot", "--if-installed"], env);
        await Assert.That(exit).IsEqualTo(0);

        await Assert.That(File.Exists(env.CopilotMcpConfigJson)).IsFalse();
    }

    [Test]
    public async Task install_copilot_if_installed_heals_mcp_and_instructions_when_hooks_current() {
        using var _    = new EnvScope("COPILOT_HOME", null);
        using var home = new FakeUserHome();
        var env = TestEnv(home.Path);

        // Hooks installed AND marker already current → the refresh must NOT rewrite hooks, but must
        // still (re)create the separate MCP + instructions files if they're missing (self-heal).
        PluginCommand.InstallCopilotHooks(env.CopilotKcapHooksJson); // writes hooks + current marker

        var exit = await PluginCommand.HandleAsync(["plugin", "install", "--copilot", "--if-installed"], env);
        await Assert.That(exit).IsEqualTo(0);

        await Assert.That(File.Exists(env.CopilotMcpConfigJson)).IsTrue();
        await Assert.That(File.Exists(env.CopilotInstructionsMd)).IsTrue();
        var servers = JsonNode.Parse(await File.ReadAllTextAsync(env.CopilotMcpConfigJson))!.AsObject()["mcpServers"]!.AsObject();
        await Assert.That(servers.Select(kv => kv.Key)).Contains("kcap-review");
    }

    [Test]
    public async Task install_copilot_if_installed_heals_mcp_and_instructions_when_hook_rewrite_fails() {
        using var _    = new EnvScope("COPILOT_HOME", null);
        using var home = new FakeUserHome();
        var env = TestEnv(home.Path);

        // Installed (stale marker → IsInstalled true, hooksCurrent false) but the hooks rewrite will
        // FAIL: make the kcap.json path a directory so InstallCopilotHooks's write throws → returns
        // false. The refresh must warn on the hook failure but still heal the independent MCP +
        // instructions files rather than bailing.
        var hooksDir = System.IO.Path.GetDirectoryName(env.CopilotKcapHooksJson)!;
        Directory.CreateDirectory(env.CopilotKcapHooksJson);   // kcap.json is a directory → write fails
        await File.WriteAllTextAsync(System.IO.Path.Combine(hooksDir, CopilotHooksInstaller.MarkerFileName), "0.0.0-stale");

        var exit = await PluginCommand.HandleAsync(["plugin", "install", "--copilot", "--if-installed"], env);
        await Assert.That(exit).IsEqualTo(0);                  // refresh swallows the hook-write failure

        await Assert.That(File.Exists(env.CopilotMcpConfigJson)).IsTrue();   // MCP healed despite the hook failure
        await Assert.That(File.Exists(env.CopilotInstructionsMd)).IsTrue();  // instructions healed too
    }

    [Test]
    public async Task remove_copilot_unregisters_mcp_servers_preserving_user_entries() {
        using var _    = new EnvScope("COPILOT_HOME", null);
        using var home = new FakeUserHome();
        var env = TestEnv(home.Path);

        // Seed mcp-config.json as a prior install would (ownership marker present),
        // then splice in a user-authored server that must survive removal.
        JsonMcpConfigWriter.Register(env.CopilotMcpConfigJson, KcapMcpServers.All, McpConfigShape.Copilot, cwd: null, new McpMarker("copilot"));
        var seeded = JsonNode.Parse(await File.ReadAllTextAsync(env.CopilotMcpConfigJson))!.AsObject();
        seeded["mcpServers"]!["my-tool"] = JsonNode.Parse("""{"type":"stdio","command":"my-tool","args":["serve"]}""");
        await File.WriteAllTextAsync(env.CopilotMcpConfigJson, seeded.ToJsonString());

        var exit = await PluginCommand.HandleAsync(["plugin", "remove", "--copilot"], env);
        await Assert.That(exit).IsEqualTo(0);

        var root    = JsonNode.Parse(await File.ReadAllTextAsync(env.CopilotMcpConfigJson))!.AsObject();
        var servers = root["mcpServers"]!.AsObject();
        var keys    = servers.Select(kv => kv.Key).ToArray();
        await Assert.That(keys).DoesNotContain("kcap-review");
        await Assert.That(keys).DoesNotContain("kcap-sessions");
        await Assert.That(keys).DoesNotContain("kcap-flows");
        await Assert.That(keys).DoesNotContain("kcap-memory");
        await Assert.That(servers["my-tool"]).IsNotNull(); // user server preserved
    }

    [Test]
    public async Task remove_copilot_retains_marker_on_failed_unregister_then_retry_removes_entries() {
        using var _    = new EnvScope("COPILOT_HOME", null);
        using var home = new FakeUserHome();
        var env = TestEnv(home.Path);

        JsonMcpConfigWriter.Register(env.CopilotMcpConfigJson, KcapMcpServers.All, McpConfigShape.Copilot, cwd: null, new McpMarker("copilot"));
        var installed = await File.ReadAllTextAsync(env.CopilotMcpConfigJson); // valid content to restore after the "fix"
        await Assert.That(new McpMarker("copilot").Owned(env.CopilotMcpConfigJson).ToArray()).IsNotEmpty();

        // The config is temporarily malformed/unreadable → Unregister fails-closed.
        await File.WriteAllTextAsync(env.CopilotMcpConfigJson, "{ not valid json");

        var failExit = await PluginCommand.HandleAsync(["plugin", "remove", "--copilot"], env);
        await Assert.That(failExit).IsEqualTo(1);                                                  // failed MCP unregister propagates
        await Assert.That(new McpMarker("copilot").Owned(env.CopilotMcpConfigJson).ToArray()).IsNotEmpty();  // marker RETAINED for retry

        // User fixes the file (kcap entries intact); the retry now succeeds and cleans up.
        await File.WriteAllTextAsync(env.CopilotMcpConfigJson, installed);
        var retryExit = await PluginCommand.HandleAsync(["plugin", "remove", "--copilot"], env);
        await Assert.That(retryExit).IsEqualTo(0);

        var root    = JsonNode.Parse(await File.ReadAllTextAsync(env.CopilotMcpConfigJson))!.AsObject();
        var servers = root["mcpServers"] as JsonObject;
        var keys    = servers?.Select(kv => kv.Key).ToArray() ?? [];
        await Assert.That(keys).DoesNotContain("kcap-review");
        await Assert.That(new McpMarker("copilot").Owned(env.CopilotMcpConfigJson).ToArray()).IsEmpty();  // marker cleared after clean removal
    }

    [Test]
    public async Task install_copilot_installs_instructions_preserving_user_content() {
        using var _    = new EnvScope("COPILOT_HOME", null);
        using var home = new FakeUserHome();
        var env = TestEnv(home.Path);

        PluginCommand.InstallCopilotHooks(env.CopilotKcapHooksJson);
        CopilotHooksInstaller.DeleteMarker(env.CopilotKcapHooksJson);

        // A pre-existing user instructions file that must survive.
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(env.CopilotInstructionsMd)!);
        await File.WriteAllTextAsync(env.CopilotInstructionsMd, "# My rules\n\nAlways use tabs.\n");

        var exit = await PluginCommand.HandleAsync(["plugin", "install", "--copilot", "--if-installed"], env);
        await Assert.That(exit).IsEqualTo(0);

        var content = await File.ReadAllTextAsync(env.CopilotInstructionsMd);
        await Assert.That(content).Contains("Always use tabs.");                       // user content preserved
        await Assert.That(content).Contains(AgentInstructionsWriter.BeginMarker);
        await Assert.That(content).Contains("Prefer kcap tools");
    }

    [Test]
    public async Task install_copilot_skip_instructions_flag_leaves_file_untouched() {
        using var _    = new EnvScope("COPILOT_HOME", null);
        using var home = new FakeUserHome();
        var env = TestEnv(home.Path);

        PluginCommand.InstallCopilotHooks(env.CopilotKcapHooksJson);
        CopilotHooksInstaller.DeleteMarker(env.CopilotKcapHooksJson);

        var exit = await PluginCommand.HandleAsync(
            ["plugin", "install", "--copilot", "--if-installed", "--skip-copilot-instructions"], env);
        await Assert.That(exit).IsEqualTo(0);

        await Assert.That(File.Exists(env.CopilotInstructionsMd)).IsFalse();
    }

    [Test]
    public async Task remove_copilot_strips_instructions_block_keeping_user_content() {
        using var _    = new EnvScope("COPILOT_HOME", null);
        using var home = new FakeUserHome();
        var env = TestEnv(home.Path);

        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(env.CopilotInstructionsMd)!);
        await File.WriteAllTextAsync(env.CopilotInstructionsMd, "# My rules\n\nAlways use tabs.\n");
        AgentInstructionsWriter.Write(env.CopilotInstructionsMd, KcapAgentInstructions.Body);

        var exit = await PluginCommand.HandleAsync(["plugin", "remove", "--copilot"], env);
        await Assert.That(exit).IsEqualTo(0);

        var content = await File.ReadAllTextAsync(env.CopilotInstructionsMd);
        await Assert.That(content).Contains("Always use tabs.");
        await Assert.That(content).DoesNotContain(AgentInstructionsWriter.BeginMarker);
        await Assert.That(content).DoesNotContain("Prefer kcap tools");
    }

    static PluginEnvironment TestEnv(string fakeHome) => new(
        HomeDirectory:     fakeHome,
        ResolvePluginPath: () => null,
        Stdout:            TextWriter.Null,
        Stderr:            TextWriter.Null
    );

    // Sets an env var for the test's lifetime and restores the previous value on Dispose.
    // Used to clear COPILOT_HOME so CopilotPaths resolves under the fake home.
    sealed class EnvScope : IDisposable {
        readonly string  _key;
        readonly string? _prev;
        public EnvScope(string key, string? value) {
            _key  = key;
            _prev = Environment.GetEnvironmentVariable(key);
            Environment.SetEnvironmentVariable(key, value);
        }
        public void Dispose() => Environment.SetEnvironmentVariable(_key, _prev);
    }
}
