using System.Text.Json.Nodes;
using Capacitor.Cli.Commands;
using Capacitor.Cli.Core;
using Capacitor.Cli.Core.Antigravity;
using Capacitor.Cli.Core.Instructions;
using Capacitor.Cli.Core.Mcp;

namespace Capacitor.Cli.Tests.Unit;

// `plugin install/remove --antigravity` also registers the kcap MCP servers in Antigravity's OWN
// ~/.gemini/config/mcp_config.json (Standard shape) and installs the steering block into the shared
// ~/.gemini/GEMINI.md. FakeUserHome + a cleared GEMINI_CLI_HOME isolate the paths under a temp home;
// the --if-installed refresh branch (hooks pre-seeded, marker staled) skips the fresh "kcap on PATH"
// precheck. (Skills install to ~/.gemini/skills is covered at the CodingAgentsStep layer.)
[NotInParallel("HomeEnvVarMutation")]
public class PluginCommandAntigravityTests {
    // Seed installed-but-stale hooks so `--if-installed` refreshes (and registers MCP + instructions).
    static void SeedStaleHooks(PluginEnvironment env) {
        AntigravityHooksInstaller.Install(env.AntigravityHooksJson);  // hooks + plugin.json + current marker
        File.WriteAllText(
            Path.Combine(Path.GetDirectoryName(env.AntigravityHooksJson)!, AntigravityHooksInstaller.MarkerFileName),
            "0.0.0-stale");
    }

    [Test]
    public async Task install_antigravity_registers_mcp_into_own_config_preserving_user_servers() {
        using var _    = new EnvScope("GEMINI_CLI_HOME", null);
        using var home = new FakeUserHome();
        var env = TestEnv(home.Path);
        SeedStaleHooks(env);

        // A user-authored MCP server in Antigravity's mcp_config.json that must survive.
        Directory.CreateDirectory(Path.GetDirectoryName(env.AntigravityMcpConfigJson)!);
        await File.WriteAllTextAsync(env.AntigravityMcpConfigJson, """
            {"mcpServers":{"my-tool":{"command":"my-tool","args":["serve"]}}}
            """);

        var exit = await PluginCommand.HandleAsync(["plugin", "install", "--antigravity", "--if-installed"], env);
        await Assert.That(exit).IsEqualTo(0);

        var servers = JsonNode.Parse(await File.ReadAllTextAsync(env.AntigravityMcpConfigJson))!.AsObject()["mcpServers"]!.AsObject();
        await Assert.That(servers["kcap-review"]!["command"]!.GetValue<string>()).IsEqualTo("kcap");
        await Assert.That(servers["kcap-review"]!["type"]).IsNull();   // Standard shape: no `type`
        await Assert.That(servers["kcap-review"]!["trust"]).IsNull();  // Antigravity has no config trust knob
        await Assert.That(servers.Select(kv => kv.Key)).Contains("kcap-sessions");
        await Assert.That(servers.Select(kv => kv.Key)).Contains("kcap-flows");
        await Assert.That(servers.Select(kv => kv.Key)).Contains("kcap-memory");
        await Assert.That(servers.Select(kv => kv.Key)).Contains("kcap-analytics");
        await Assert.That(servers["kcap-analytics"]!["trust"]).IsNull();  // Antigravity has no config trust knob
        await Assert.That(servers["my-tool"]).IsNotNull();  // user server preserved
    }

    [Test]
    public async Task install_antigravity_installs_instructions_into_shared_gemini_md() {
        using var _    = new EnvScope("GEMINI_CLI_HOME", null);
        using var home = new FakeUserHome();
        var env = TestEnv(home.Path);
        SeedStaleHooks(env);

        Directory.CreateDirectory(Path.GetDirectoryName(env.AntigravityInstructionsMd)!);
        await File.WriteAllTextAsync(env.AntigravityInstructionsMd, "# My rules\n\nAlways use tabs.\n");

        var exit = await PluginCommand.HandleAsync(["plugin", "install", "--antigravity", "--if-installed"], env);
        await Assert.That(exit).IsEqualTo(0);

        var content = await File.ReadAllTextAsync(env.AntigravityInstructionsMd);
        await Assert.That(content).Contains("Always use tabs.");                    // user content preserved
        await Assert.That(content).Contains(AgentInstructionsWriter.BeginMarker);
        await Assert.That(content).Contains("Prefer kcap tools");
    }

    [Test]
    public async Task install_antigravity_skip_mcp_flag_leaves_config_untouched() {
        using var _    = new EnvScope("GEMINI_CLI_HOME", null);
        using var home = new FakeUserHome();
        var env = TestEnv(home.Path);
        SeedStaleHooks(env);

        var exit = await PluginCommand.HandleAsync(
            ["plugin", "install", "--antigravity", "--if-installed", "--skip-antigravity-mcp"], env);
        await Assert.That(exit).IsEqualTo(0);

        await Assert.That(File.Exists(env.AntigravityMcpConfigJson)).IsFalse();
    }

    [Test]
    public async Task install_antigravity_skip_instructions_flag_leaves_gemini_md_untouched() {
        using var _    = new EnvScope("GEMINI_CLI_HOME", null);
        using var home = new FakeUserHome();
        var env = TestEnv(home.Path);
        SeedStaleHooks(env);

        var exit = await PluginCommand.HandleAsync(
            ["plugin", "install", "--antigravity", "--if-installed", "--skip-antigravity-instructions"], env);
        await Assert.That(exit).IsEqualTo(0);

        await Assert.That(File.Exists(env.AntigravityInstructionsMd)).IsFalse();
    }

    [Test]
    public async Task remove_antigravity_unregisters_mcp_and_strips_instructions() {
        using var _    = new EnvScope("GEMINI_CLI_HOME", null);
        using var home = new FakeUserHome();
        var env = TestEnv(home.Path);

        // Seed MCP (kcap-owned marker + a user server) and a GEMINI.md with kcap's block + user content.
        JsonMcpConfigWriter.Register(env.AntigravityMcpConfigJson, KcapMcpServers.All, McpConfigShape.Standard, cwd: null, new McpMarker("antigravity"));
        var seeded = JsonNode.Parse(await File.ReadAllTextAsync(env.AntigravityMcpConfigJson))!.AsObject();
        seeded["mcpServers"]!["my-tool"] = JsonNode.Parse("""{"command":"my-tool","args":["serve"]}""");
        await File.WriteAllTextAsync(env.AntigravityMcpConfigJson, seeded.ToJsonString());

        Directory.CreateDirectory(Path.GetDirectoryName(env.AntigravityInstructionsMd)!);
        await File.WriteAllTextAsync(env.AntigravityInstructionsMd, "# My rules\n\nAlways use tabs.\n");
        AgentInstructionsWriter.Write(env.AntigravityInstructionsMd, KcapAgentInstructions.Body);

        var exit = await PluginCommand.HandleAsync(["plugin", "remove", "--antigravity"], env);
        await Assert.That(exit).IsEqualTo(0);

        var servers = JsonNode.Parse(await File.ReadAllTextAsync(env.AntigravityMcpConfigJson))!.AsObject()["mcpServers"]!.AsObject();
        var keys    = servers.Select(kv => kv.Key).ToArray();
        await Assert.That(keys).DoesNotContain("kcap-review");
        await Assert.That(servers["my-tool"]).IsNotNull();  // user server preserved

        var content = await File.ReadAllTextAsync(env.AntigravityInstructionsMd);
        await Assert.That(content).Contains("Always use tabs.");
        await Assert.That(content).DoesNotContain(AgentInstructionsWriter.BeginMarker);
    }

    [Test]
    public async Task remove_antigravity_keeps_shared_instructions_when_gemini_installed() {
        using var _    = new EnvScope("GEMINI_CLI_HOME", null);
        using var home = new FakeUserHome();
        var env = TestEnv(home.Path);

        // Gemini CLI is installed (its hooks live in the shared ~/.gemini/settings.json) and the
        // shared GEMINI.md carries kcap's block. Removing Antigravity must NOT strip that block —
        // it belongs to the still-installed Gemini integration too.
        Directory.CreateDirectory(Path.GetDirectoryName(env.GeminiSettingsJson)!);
        PluginCommand.InstallGeminiHooks(env.GeminiSettingsJson);

        Directory.CreateDirectory(Path.GetDirectoryName(env.AntigravityInstructionsMd)!);
        await File.WriteAllTextAsync(env.AntigravityInstructionsMd, "# My rules\n\nAlways use tabs.\n");
        AgentInstructionsWriter.Write(env.AntigravityInstructionsMd, KcapAgentInstructions.Body);

        var exit = await PluginCommand.HandleAsync(["plugin", "remove", "--antigravity"], env);
        await Assert.That(exit).IsEqualTo(0);

        var content = await File.ReadAllTextAsync(env.AntigravityInstructionsMd);
        await Assert.That(content).Contains("Always use tabs.");                    // user content preserved
        await Assert.That(content).Contains(AgentInstructionsWriter.BeginMarker);   // kcap block LEFT for Gemini
    }

    static PluginEnvironment TestEnv(string fakeHome) => new(
        HomeDirectory:     fakeHome,
        ResolvePluginPath: () => null,   // skills source unavailable → skills install no-ops (covered elsewhere)
        Stdout:            TextWriter.Null,
        Stderr:            TextWriter.Null
    );

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
