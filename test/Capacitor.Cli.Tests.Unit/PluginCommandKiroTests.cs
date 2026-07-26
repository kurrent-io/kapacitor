using System.Text.Json.Nodes;
using Capacitor.Cli.Commands;
using Capacitor.Cli.Core;
using Capacitor.Cli.Core.Kiro;
using Capacitor.Cli.Core.Mcp;

namespace Capacitor.Cli.Tests.Unit;

/// <summary>
/// `plugin install/remove --kiro` MCP registration in <c>~/.kiro/settings/mcp.json</c>. Uses a
/// FakeUserHome + cleared KIRO_HOME; <c>--if-installed</c> (agent pre-seeded) skips the kiro-cli clone.
/// </summary>
[NotInParallel("HomeEnvVarMutation")]
public class PluginCommandKiroTests {
    // Seed an installed kcap agent at the current version so `--if-installed` treats it as current
    // and skips the (kiro-cli-dependent) clone, proceeding straight to MCP registration.
    static void SeedAgent(PluginEnvironment env) {
        Directory.CreateDirectory(Path.GetDirectoryName(env.KiroKcapAgentJson)!);
        File.WriteAllText(env.KiroKcapAgentJson, """{"name":"kcap","hooks":{}}""");
        KiroHooksInstaller.WriteMarker(env.KiroKcapAgentJson, "kiro_default");
    }

    [Test]
    public async Task install_kiro_registers_mcp_servers_preserving_user_entries() {
        using var _    = new EnvScope("KIRO_HOME", null);
        using var home = new FakeUserHome();
        var env = TestEnv(home.Path);
        SeedAgent(env);

        // A user server with autoApprove set — both the server and its autoApprove must survive.
        Directory.CreateDirectory(Path.GetDirectoryName(env.KiroMcpJson)!);
        await File.WriteAllTextAsync(env.KiroMcpJson, """
            {"mcpServers":{"my-tool":{"command":"my-tool","args":["serve"],"autoApprove":["do_thing"]}}}
            """);

        var exit = await PluginCommand.HandleAsync(["plugin", "install", "--kiro", "--if-installed"], env);
        await Assert.That(exit).IsEqualTo(0);

        var servers = JsonNode.Parse(await File.ReadAllTextAsync(env.KiroMcpJson))!.AsObject()["mcpServers"]!.AsObject();
        // Standard shape: command="kcap" + args, no `type`, no `trust` (autoApprove left unset).
        await Assert.That(servers["kcap-review"]!["command"]!.GetValue<string>()).IsEqualTo("kcap");
        await Assert.That(servers["kcap-review"]!["type"]).IsNull();
        await Assert.That(servers["kcap-review"]!["trust"]).IsNull();
        await Assert.That(servers["kcap-review"]!["autoApprove"]).IsNull();
        await Assert.That(servers.Select(kv => kv.Key)).Contains("kcap-sessions");
        await Assert.That(servers.Select(kv => kv.Key)).Contains("kcap-flows");
        await Assert.That(servers.Select(kv => kv.Key)).Contains("kcap-memory");
        await Assert.That(servers.Select(kv => kv.Key)).Contains("kcap-analytics");
        await Assert.That(servers["kcap-analytics"]!["trust"]).IsNull();                    // Kiro has no config trust knob
        await Assert.That(servers["my-tool"]).IsNotNull();                                  // user server preserved
        await Assert.That(servers["my-tool"]!["autoApprove"]!.AsArray().Count).IsEqualTo(1); // its autoApprove preserved
    }

    [Test]
    public async Task install_kiro_skip_mcp_flag_leaves_config_untouched() {
        using var _    = new EnvScope("KIRO_HOME", null);
        using var home = new FakeUserHome();
        var env = TestEnv(home.Path);
        SeedAgent(env);

        var exit = await PluginCommand.HandleAsync(
            ["plugin", "install", "--kiro", "--if-installed", "--skip-kiro-mcp"], env);
        await Assert.That(exit).IsEqualTo(0);

        await Assert.That(File.Exists(env.KiroMcpJson)).IsFalse();
    }

    [Test]
    public async Task install_kiro_creates_settings_dir_when_missing() {
        using var _    = new EnvScope("KIRO_HOME", null);
        using var home = new FakeUserHome();
        var env = TestEnv(home.Path);
        SeedAgent(env);
        // settings/ dir does not exist yet — Register must create it.
        await Assert.That(Directory.Exists(Path.GetDirectoryName(env.KiroMcpJson)!)).IsFalse();

        var exit = await PluginCommand.HandleAsync(["plugin", "install", "--kiro", "--if-installed"], env);
        await Assert.That(exit).IsEqualTo(0);

        await Assert.That(File.Exists(env.KiroMcpJson)).IsTrue();
    }

    [Test]
    public async Task remove_kiro_unregisters_mcp_servers_preserving_user_entries() {
        using var _    = new EnvScope("KIRO_HOME", null);
        using var home = new FakeUserHome();
        var env = TestEnv(home.Path);

        // Seed mcp.json as a prior install would (ownership marker present) + a user server.
        JsonMcpConfigWriter.Register(env.KiroMcpJson, KcapMcpServers.All, McpConfigShape.Standard, cwd: null, new McpMarker("kiro"));
        var seeded = JsonNode.Parse(await File.ReadAllTextAsync(env.KiroMcpJson))!.AsObject();
        seeded["mcpServers"]!["my-tool"] = JsonNode.Parse("""{"command":"my-tool","args":["serve"]}""");
        await File.WriteAllTextAsync(env.KiroMcpJson, seeded.ToJsonString());

        var exit = await PluginCommand.HandleAsync(["plugin", "remove", "--kiro"], env);
        await Assert.That(exit).IsEqualTo(0);

        var servers = JsonNode.Parse(await File.ReadAllTextAsync(env.KiroMcpJson))!.AsObject()["mcpServers"]!.AsObject();
        var keys    = servers.Select(kv => kv.Key).ToArray();
        await Assert.That(keys).DoesNotContain("kcap-review");
        await Assert.That(keys).DoesNotContain("kcap-memory");
        await Assert.That(servers["my-tool"]).IsNotNull();  // user server preserved
    }

    [Test]
    public async Task install_kiro_if_installed_heals_mcp_only_install_without_cloning_agent() {
        using var _    = new EnvScope("KIRO_HOME", null);
        using var home = new FakeUserHome();
        var env = TestEnv(home.Path);

        // MCP-only prior install, PARTIAL (only kcap-review registered, e.g. from an older kcap),
        // and NO agent clone — as `--skip-kiro-hooks`, or a kiro-cli-less clone failure, leaves it.
        var partial = KcapMcpServers.All.Where(s => s.Name == "kcap-review").ToList();
        JsonMcpConfigWriter.Register(env.KiroMcpJson, partial, McpConfigShape.Standard, cwd: null, new McpMarker("kiro"));
        await Assert.That(File.Exists(env.KiroKcapAgentJson)).IsFalse();  // no agent installed

        var exit = await PluginCommand.HandleAsync(["plugin", "install", "--kiro", "--if-installed"], env);
        await Assert.That(exit).IsEqualTo(0);

        // The refresh reached RegisterKiroMcpServersAsync (instead of bailing on the missing agent
        // marker) and added the three servers the partial install lacked...
        var servers = JsonNode.Parse(await File.ReadAllTextAsync(env.KiroMcpJson))!.AsObject()["mcpServers"]!.AsObject();
        var keys    = servers.Select(kv => kv.Key).ToArray();
        await Assert.That(keys).Contains("kcap-sessions");
        await Assert.That(keys).Contains("kcap-flows");
        await Assert.That(keys).Contains("kcap-memory");
        // ...and the agent was NOT cloned — a refresh must never install hooks the user opted out of.
        await Assert.That(File.Exists(env.KiroKcapAgentJson)).IsFalse();
    }

    [Test]
    public async Task install_kiro_if_installed_noop_when_nothing_installed() {
        using var _    = new EnvScope("KIRO_HOME", null);
        using var home = new FakeUserHome();
        var env = TestEnv(home.Path);

        // Neither agent nor MCP present → refresh must be a pure no-op (never force-installs).
        var exit = await PluginCommand.HandleAsync(["plugin", "install", "--kiro", "--if-installed"], env);
        await Assert.That(exit).IsEqualTo(0);

        await Assert.That(File.Exists(env.KiroMcpJson)).IsFalse();
        await Assert.That(File.Exists(env.KiroKcapAgentJson)).IsFalse();
    }

    static PluginEnvironment TestEnv(string fakeHome) => new(
        HomeDirectory:     fakeHome,
        ResolvePluginPath: () => null,
        Stdout:            TextWriter.Null,
        Stderr:            TextWriter.Null
    );

    // Sets an env var for the test's lifetime and restores it on Dispose. Clears KIRO_HOME so
    // KiroPaths resolves under the fake home.
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
