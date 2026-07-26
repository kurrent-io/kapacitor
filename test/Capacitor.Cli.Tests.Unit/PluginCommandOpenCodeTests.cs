using System.Text.Json.Nodes;
using Capacitor.Cli.Commands;
using Capacitor.Cli.Core;
using Capacitor.Cli.Core.Instructions;
using Capacitor.Cli.Core.Mcp;
using Capacitor.Cli.Core.OpenCode;

namespace Capacitor.Cli.Tests.Unit;

/// <summary>
/// Plugin-dispatch tests for the OpenCode live-ingest plugin: install
/// (+ <c>--if-installed</c> refresh) and remove via <c>kcap plugin --opencode</c>.
/// The npm refresh path (refresh.js) runs the <c>--if-installed</c> form on every
/// upgrade, so it MUST no-op for users who never opted in — these tests guard that
/// contract at the dispatch layer (the installer primitives themselves are covered
/// by OpenCodeExtensionInstallerTests). Uses <c>--opencode-plugin-path</c> to stay
/// isolated from the real ~/.config/opencode and any ambient XDG_CONFIG_HOME.
///
/// The MCP + instructions cases also register the kcap servers in opencode.json and
/// install the AGENTS.md block; they use a FakeUserHome + clear OPENCODE_CONFIG_DIR /
/// XDG_CONFIG_HOME so OpenCodePaths resolves under it (env mutation → NotInParallel).
/// </summary>
[NotInParallel("HomeEnvVarMutation")]
public class PluginCommandOpenCodeTests {
    [Test]
    public async Task Install_opencode_with_if_installed_is_noop_when_not_installed() {
        using var tmp = new TempDir();
        var pluginPath = Path.Combine(tmp.Path, "plugins", "kcap.ts");

        var exit = await PluginCommand.HandleAsync(
            ["plugin", "install", "--opencode", "--opencode-plugin-path", pluginPath, "--if-installed"],
            TestEnv(tmp.Path));
        await Assert.That(exit).IsEqualTo(0);

        // kcap.ts must NOT exist — the npm refresh must never force-install the
        // OpenCode plugin onto a user who never opted in.
        await Assert.That(File.Exists(pluginPath)).IsFalse();
    }

    [Test]
    public async Task Install_opencode_with_if_installed_refreshes_existing_plugin() {
        using var tmp = new TempDir();
        var dir = Path.Combine(tmp.Path, "plugins");
        Directory.CreateDirectory(dir);
        var pluginPath = Path.Combine(dir, "kcap.ts");
        // Seed a stale kcap.ts with NO version marker (pre-marker install). The
        // refresh path should rewrite it in place and stamp the version marker.
        await File.WriteAllTextAsync(pluginPath, "// stale plugin body");

        // Plugin-only: skip MCP/instructions so this stays isolated to the plugin file
        // (their config path derives from ambient OPENCODE_CONFIG_DIR/XDG, not this TempDir).
        var exit = await PluginCommand.HandleAsync(
            ["plugin", "install", "--opencode", "--opencode-plugin-path", pluginPath, "--if-installed",
             "--skip-opencode-mcp", "--skip-opencode-instructions"],
            TestEnv(tmp.Path));
        await Assert.That(exit).IsEqualTo(0);

        var body = await File.ReadAllTextAsync(pluginPath);
        await Assert.That(body).DoesNotContain("stale plugin body");
        await Assert.That(body).Contains("export const KcapPlugin");
        await Assert.That(File.Exists(Path.Combine(dir, ".kcap-extension-version"))).IsTrue();
    }

    [Test]
    public async Task Install_opencode_if_installed_recreates_plugin_when_file_missing_but_marker_current() {
        using var tmp = new TempDir();
        var dir = Path.Combine(tmp.Path, "plugins");
        Directory.CreateDirectory(dir);
        var pluginPath = Path.Combine(dir, "kcap.ts");
        // Marker at the CURRENT version but NO kcap.ts on disk (user deleted it). IsInstalled is true
        // via the marker, so --if-installed must still RECREATE the missing plugin, not skip it.
        await File.WriteAllTextAsync(Path.Combine(dir, ".kcap-extension-version"), CapacitorVersion.Current());

        var exit = await PluginCommand.HandleAsync(
            ["plugin", "install", "--opencode", "--opencode-plugin-path", pluginPath, "--if-installed",
             "--skip-opencode-mcp", "--skip-opencode-instructions"],
            TestEnv(tmp.Path));
        await Assert.That(exit).IsEqualTo(0);

        await Assert.That(File.Exists(pluginPath)).IsTrue();  // recreated despite the current marker
        await Assert.That(await File.ReadAllTextAsync(pluginPath)).Contains("export const KcapPlugin");
    }

    [Test]
    public async Task Remove_opencode_deletes_plugin_and_marker() {
        // remove also unregisters MCP + strips AGENTS.md (both under ConfigDir); clear
        // OPENCODE_CONFIG_DIR/XDG so those resolve under the TempDir, not the real config.
        using var _   = new EnvScope("OPENCODE_CONFIG_DIR", null);
        using var __  = new EnvScope("XDG_CONFIG_HOME", null);
        using var tmp = new TempDir();
        var dir = Path.Combine(tmp.Path, "plugins");
        Directory.CreateDirectory(dir);
        var pluginPath = Path.Combine(dir, "kcap.ts");
        var marker  = Path.Combine(dir, ".kcap-extension-version");
        await File.WriteAllTextAsync(pluginPath, "export const KcapPlugin = async () => ({})");
        await File.WriteAllTextAsync(marker, "1.0.0");

        var exit = await PluginCommand.HandleAsync(
            ["plugin", "remove", "--opencode", "--opencode-plugin-path", pluginPath],
            TestEnv(tmp.Path));
        await Assert.That(exit).IsEqualTo(0);

        await Assert.That(File.Exists(pluginPath)).IsFalse();
        await Assert.That(File.Exists(marker)).IsFalse();
    }

    // ── MCP + instructions ───────────────────────────────────────────────────

    // Seed an installed OpenCode plugin so `--if-installed` proceeds to the MCP/instructions
    // steps (and the fresh "kcap on PATH" precheck is skipped).
    static void SeedPlugin(PluginEnvironment env) => OpenCodeExtensionInstaller.Install(env.OpenCodeKcapPlugin);

    [Test]
    public async Task install_opencode_registers_mcp_into_opencode_json_preserving_user_config() {
        using var _   = new EnvScope("OPENCODE_CONFIG_DIR", null);
        using var __  = new EnvScope("XDG_CONFIG_HOME", null);
        using var home = new FakeUserHome();
        var env = TestEnv(home.Path);
        SeedPlugin(env);

        // A user opencode.json with $schema + a user mcp server — both must survive.
        Directory.CreateDirectory(Path.GetDirectoryName(env.OpenCodeMcpConfigJson)!);
        await File.WriteAllTextAsync(env.OpenCodeMcpConfigJson, """
            {"$schema":"https://opencode.ai/config.json","mcp":{"my-tool":{"type":"local","command":["my-tool","serve"],"enabled":true}}}
            """);

        var exit = await PluginCommand.HandleAsync(["plugin", "install", "--opencode", "--if-installed"], env);
        await Assert.That(exit).IsEqualTo(0);

        var root = JsonNode.Parse(await File.ReadAllTextAsync(env.OpenCodeMcpConfigJson))!.AsObject();
        await Assert.That(root["$schema"]!.GetValue<string>()).IsEqualTo("https://opencode.ai/config.json"); // preserved
        var mcp = root["mcp"]!.AsObject();
        await Assert.That(mcp["my-tool"]).IsNotNull();                                     // user server preserved

        // kcap-review has the exact OpenCode shape: type=local, command-as-array, enabled=true.
        var review = mcp["kcap-review"]!.AsObject();
        await Assert.That(review["type"]!.GetValue<string>()).IsEqualTo("local");
        await Assert.That(review["enabled"]!.GetValue<bool>()).IsTrue();
        var cmd = string.Join(",", review["command"]!.AsArray().Select(n => n!.GetValue<string>()));
        await Assert.That(cmd).IsEqualTo("kcap,mcp,review");
        await Assert.That(mcp.Select(kv => kv.Key)).Contains("kcap-sessions");
        await Assert.That(mcp.Select(kv => kv.Key)).Contains("kcap-flows");
        await Assert.That(mcp.Select(kv => kv.Key)).Contains("kcap-memory");
        await Assert.That(mcp.Select(kv => kv.Key)).Contains("kcap-analytics");
    }

    [Test]
    public async Task install_opencode_skip_mcp_flag_leaves_config_untouched() {
        using var _   = new EnvScope("OPENCODE_CONFIG_DIR", null);
        using var __  = new EnvScope("XDG_CONFIG_HOME", null);
        using var home = new FakeUserHome();
        var env = TestEnv(home.Path);
        SeedPlugin(env);

        var exit = await PluginCommand.HandleAsync(
            ["plugin", "install", "--opencode", "--if-installed", "--skip-opencode-mcp"], env);
        await Assert.That(exit).IsEqualTo(0);

        await Assert.That(File.Exists(env.OpenCodeMcpConfigJson)).IsFalse();
    }

    [Test]
    public async Task install_opencode_installs_instructions_into_agents_md() {
        using var _   = new EnvScope("OPENCODE_CONFIG_DIR", null);
        using var __  = new EnvScope("XDG_CONFIG_HOME", null);
        using var home = new FakeUserHome();
        var env = TestEnv(home.Path);
        SeedPlugin(env);

        Directory.CreateDirectory(Path.GetDirectoryName(env.OpenCodeAgentsMd)!);
        await File.WriteAllTextAsync(env.OpenCodeAgentsMd, "# My rules\n\nAlways use tabs.\n");

        var exit = await PluginCommand.HandleAsync(["plugin", "install", "--opencode", "--if-installed"], env);
        await Assert.That(exit).IsEqualTo(0);

        var content = await File.ReadAllTextAsync(env.OpenCodeAgentsMd);
        await Assert.That(content).Contains("Always use tabs.");                    // user content preserved
        await Assert.That(content).Contains(AgentInstructionsWriter.BeginMarker);
        await Assert.That(content).Contains("Prefer kcap tools");
    }

    [Test]
    public async Task install_opencode_skip_instructions_flag_leaves_file_untouched() {
        using var _   = new EnvScope("OPENCODE_CONFIG_DIR", null);
        using var __  = new EnvScope("XDG_CONFIG_HOME", null);
        using var home = new FakeUserHome();
        var env = TestEnv(home.Path);
        SeedPlugin(env);

        var exit = await PluginCommand.HandleAsync(
            ["plugin", "install", "--opencode", "--if-installed", "--skip-opencode-instructions"], env);
        await Assert.That(exit).IsEqualTo(0);

        await Assert.That(File.Exists(env.OpenCodeAgentsMd)).IsFalse();
    }

    [Test]
    public async Task remove_opencode_unregisters_mcp_and_strips_instructions() {
        using var _   = new EnvScope("OPENCODE_CONFIG_DIR", null);
        using var __  = new EnvScope("XDG_CONFIG_HOME", null);
        using var home = new FakeUserHome();
        var env = TestEnv(home.Path);

        // Seed MCP (kcap-owned marker + a user server) and an AGENTS.md with kcap's block + user content.
        JsonMcpConfigWriter.Register(env.OpenCodeMcpConfigJson, KcapMcpServers.All, McpConfigShape.OpenCode, cwd: null, new McpMarker("opencode"));
        var seeded = JsonNode.Parse(await File.ReadAllTextAsync(env.OpenCodeMcpConfigJson))!.AsObject();
        seeded["mcp"]!["my-tool"] = JsonNode.Parse("""{"type":"local","command":["my-tool"],"enabled":true}""");
        await File.WriteAllTextAsync(env.OpenCodeMcpConfigJson, seeded.ToJsonString());

        Directory.CreateDirectory(Path.GetDirectoryName(env.OpenCodeAgentsMd)!);
        await File.WriteAllTextAsync(env.OpenCodeAgentsMd, "# My rules\n\nAlways use tabs.\n");
        AgentInstructionsWriter.Write(env.OpenCodeAgentsMd, KcapAgentInstructions.Body);

        var exit = await PluginCommand.HandleAsync(["plugin", "remove", "--opencode"], env);
        await Assert.That(exit).IsEqualTo(0);

        var mcp  = JsonNode.Parse(await File.ReadAllTextAsync(env.OpenCodeMcpConfigJson))!.AsObject()["mcp"]!.AsObject();
        var keys = mcp.Select(kv => kv.Key).ToArray();
        await Assert.That(keys).DoesNotContain("kcap-review");
        await Assert.That(mcp["my-tool"]).IsNotNull();  // user server preserved

        var content = await File.ReadAllTextAsync(env.OpenCodeAgentsMd);
        await Assert.That(content).Contains("Always use tabs.");
        await Assert.That(content).DoesNotContain(AgentInstructionsWriter.BeginMarker);
    }

    // Sets an env var for the test's lifetime and restores it on Dispose. Used to clear
    // OPENCODE_CONFIG_DIR + XDG_CONFIG_HOME so OpenCodePaths resolves under the fake home.
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

    static PluginEnvironment TestEnv(string fakeHome) => new(
        HomeDirectory:     fakeHome,
        ResolvePluginPath: () => null,
        Stdout:            TextWriter.Null,
        Stderr:            TextWriter.Null
    );

    sealed class TempDir : IDisposable {
        public string Path { get; } = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(),
            $"kcap-oc-plugincmd-test-{Guid.NewGuid().ToString("N")[..8]}"
        );
        public TempDir() => Directory.CreateDirectory(Path);
        public void Dispose() {
            try { Directory.Delete(Path, true); } catch { /* best effort */ }
        }
    }
}
