using System.Text.Json.Nodes;
using Capacitor.Cli.Core.Config;
using Capacitor.Cli.Commands;
using Capacitor.Cli.Core.Harness.Kiro;
using Capacitor.Cli.Core.Mcp;

namespace Capacitor.Cli.Tests.Unit.Commands;

/// <summary>
/// `plugin install/remove --kiro` MCP registration in <c>~/.kiro/settings/mcp.json</c>. Uses a
/// TempHome + cleared KIRO_HOME; <c>--if-installed</c> (agent pre-seeded) skips the kiro-cli clone.
/// </summary>
public class PluginCommandKiroTests {
    // Seed an installed kcap agent at the current version so `--if-installed` treats it as current
    // and skips the (kiro-cli-dependent) clone, proceeding straight to MCP registration.
    static void SeedAgent(PluginEnvironment env) {
        Directory.CreateDirectory(Path.GetDirectoryName(env.Paths.Kiro.KcapAgentJson)!);
        File.WriteAllText(env.Paths.Kiro.KcapAgentJson, """{"name":"kcap","hooks":{}}""");
        KiroHooksInstaller.WriteMarker(env.Paths.Kiro.KcapAgentJson, "kiro_default");
    }

    [Test]
    public async Task install_kiro_registers_mcp_servers_preserving_user_entries() {
        using var home = new TempHome();
        var env = TestEnv(home.Path);
        SeedAgent(env);

        // A user server with autoApprove set — both the server and its autoApprove must survive.
        Directory.CreateDirectory(Path.GetDirectoryName(env.Paths.Kiro.SettingsMcpJson)!);
        await File.WriteAllTextAsync(env.Paths.Kiro.SettingsMcpJson, """
            {"mcpServers":{"my-tool":{"command":"my-tool","args":["serve"],"autoApprove":["do_thing"]}}}
            """);

        var exit = await new PluginCommand(env).HandleAsync(["plugin", "install", "--kiro", "--if-installed"]);
        await Assert.That(exit).IsEqualTo(0);

        var servers = JsonNode.Parse(await File.ReadAllTextAsync(env.Paths.Kiro.SettingsMcpJson))!.AsObject()["mcpServers"]!.AsObject();
        // Standard shape: command="kcap" + args, no `type`, no `trust` (autoApprove left unset).
        // Registered command is the resolved native binary (injected seam), not the wrapper-resolved "kcap".
        await Assert.That(servers["kcap-review"]!["command"]!.GetValue<string>()).IsEqualTo(TestBinaryPath);
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
        using var home = new TempHome();
        var env = TestEnv(home.Path);
        SeedAgent(env);

        var exit = await new PluginCommand(env).HandleAsync(
            ["plugin", "install", "--kiro", "--if-installed", "--skip-kiro-mcp"]);
        await Assert.That(exit).IsEqualTo(0);

        await Assert.That(File.Exists(env.Paths.Kiro.SettingsMcpJson)).IsFalse();
    }

    [Test]
    public async Task install_kiro_creates_settings_dir_when_missing() {
        using var home = new TempHome();
        var env = TestEnv(home.Path);
        SeedAgent(env);
        // settings/ dir does not exist yet — Register must create it.
        await Assert.That(Directory.Exists(Path.GetDirectoryName(env.Paths.Kiro.SettingsMcpJson)!)).IsFalse();

        var exit = await new PluginCommand(env).HandleAsync(["plugin", "install", "--kiro", "--if-installed"]);
        await Assert.That(exit).IsEqualTo(0);

        await Assert.That(File.Exists(env.Paths.Kiro.SettingsMcpJson)).IsTrue();
    }

    [Test]
    public async Task remove_kiro_unregisters_mcp_servers_preserving_user_entries() {
        using var home = new TempHome();
        var env = TestEnv(home.Path);

        // Seed mcp.json as a prior install would (ownership marker present) + a user server.
        JsonMcpConfigWriter.Register(env.Paths.Kiro.SettingsMcpJson, KcapMcpServers.All, McpConfigShape.Standard, cwd: null, new McpMarker("kiro", env.Home));
        var seeded = JsonNode.Parse(await File.ReadAllTextAsync(env.Paths.Kiro.SettingsMcpJson))!.AsObject();
        seeded["mcpServers"]!["my-tool"] = JsonNode.Parse("""{"command":"my-tool","args":["serve"]}""");
        await File.WriteAllTextAsync(env.Paths.Kiro.SettingsMcpJson, seeded.ToJsonString());

        var exit = await new PluginCommand(env).HandleAsync(["plugin", "remove", "--kiro"]);
        await Assert.That(exit).IsEqualTo(0);

        var servers = JsonNode.Parse(await File.ReadAllTextAsync(env.Paths.Kiro.SettingsMcpJson))!.AsObject()["mcpServers"]!.AsObject();
        var keys    = servers.Select(kv => kv.Key).ToArray();
        await Assert.That(keys).DoesNotContain("kcap-review");
        await Assert.That(keys).DoesNotContain("kcap-memory");
        await Assert.That(servers["my-tool"]).IsNotNull();  // user server preserved
    }

    [Test]
    public async Task install_kiro_if_installed_heals_mcp_only_install_without_cloning_agent() {
        using var home = new TempHome();
        var env = TestEnv(home.Path);

        // MCP-only prior install, PARTIAL (only kcap-review registered, e.g. from an older kcap),
        // and NO agent clone — as `--skip-kiro-hooks`, or a kiro-cli-less clone failure, leaves it.
        var partial = KcapMcpServers.All.Where(s => s.Name == "kcap-review").ToList();
        JsonMcpConfigWriter.Register(env.Paths.Kiro.SettingsMcpJson, partial, McpConfigShape.Standard, cwd: null, new McpMarker("kiro", env.Home));
        await Assert.That(File.Exists(env.Paths.Kiro.KcapAgentJson)).IsFalse();  // no agent installed

        var exit = await new PluginCommand(env).HandleAsync(["plugin", "install", "--kiro", "--if-installed"]);
        await Assert.That(exit).IsEqualTo(0);

        // The refresh reached RegisterKiroMcpServersAsync (instead of bailing on the missing agent
        // marker) and added the three servers the partial install lacked...
        var servers = JsonNode.Parse(await File.ReadAllTextAsync(env.Paths.Kiro.SettingsMcpJson))!.AsObject()["mcpServers"]!.AsObject();
        var keys    = servers.Select(kv => kv.Key).ToArray();
        await Assert.That(keys).Contains("kcap-sessions");
        await Assert.That(keys).Contains("kcap-flows");
        await Assert.That(keys).Contains("kcap-memory");
        // ...and the agent was NOT cloned — a refresh must never install hooks the user opted out of.
        await Assert.That(File.Exists(env.Paths.Kiro.KcapAgentJson)).IsFalse();
    }

    [Test]
    public async Task install_kiro_if_installed_noop_when_nothing_installed() {
        using var home = new TempHome();
        var env = TestEnv(home.Path);

        // Neither agent nor MCP present → refresh must be a pure no-op (never force-installs).
        var exit = await new PluginCommand(env).HandleAsync(["plugin", "install", "--kiro", "--if-installed"]);
        await Assert.That(exit).IsEqualTo(0);

        await Assert.That(File.Exists(env.Paths.Kiro.SettingsMcpJson)).IsFalse();
        await Assert.That(File.Exists(env.Paths.Kiro.KcapAgentJson)).IsFalse();
    }

    // Deterministic native-binary path: registration writes the resolved binary as the command
    // (default: the running process), so tests inject their own value and assert that,
    // never blessing whatever executable happens to run the suite.
    internal const string TestBinaryPath = "/opt/kcap-test/bin/kcap";

    static PluginEnvironment TestEnv(string fakeHome) => new(
        Home:     new(fakeHome),
        Profiles:          new ProfileConfig(),
        ResolvePluginPath: () => null,
        Stdout:            TextWriter.Null,
        Stderr:            TextWriter.Null
    ) {
        Paths = TestHarnessPaths.NoOverrides(new(fakeHome)),
        ResolveMcpBinaryPath = () => TestBinaryPath
    };

}
