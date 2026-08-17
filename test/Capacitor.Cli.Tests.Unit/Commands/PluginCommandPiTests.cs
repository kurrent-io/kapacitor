using Capacitor.Cli.Commands;
using Capacitor.Cli.Core.Harness.Pi;
using Capacitor.Cli.Core.Instructions;

namespace Capacitor.Cli.Tests.Unit.Commands;

/// <summary>
/// Plugin-dispatch tests for the Pi live-ingest extension. Pi has no
/// hook file — its integration is the <c>~/.pi/agent/extensions/kcap.ts</c>
/// extension, installed by <c>kcap plugin install --pi</c>, refreshed via
/// <c>--if-installed</c>, removed by <c>kcap plugin remove --pi</c>. The npm
/// refresh path (refresh.js) runs the <c>--if-installed</c> form on every
/// upgrade, so it MUST no-op for users who never opted into Pi — these tests
/// guard that contract at the dispatch layer (the installer primitives
/// themselves are covered by PiExtensionInstallerTests).
/// </summary>
public class PluginCommandPiTests {
    [Test]
    public async Task Install_pi_with_if_installed_is_noop_when_not_installed() {
        using var fakeHome = new TempDir();

        var exit = await PluginCommand.HandleAsync(
            ["plugin", "install", "--pi", "--if-installed"],
            TestEnv(fakeHome.Path));
        await Assert.That(exit).IsEqualTo(0);

        // kcap.ts must NOT exist — the npm refresh must never force-install the
        // Pi extension onto a user who never opted in.
        var extPath = Path.Combine(fakeHome.Path, ".pi", "agent", "extensions", "kcap.ts");
        await Assert.That(File.Exists(extPath)).IsFalse();
    }

    [Test]
    public async Task Install_pi_with_if_installed_refreshes_existing_extension() {
        using var fakeHome = new TempDir();

        // Seed a stale kcap.ts with NO version marker (pre-marker install). The
        // refresh path should rewrite it in place and stamp the version marker.
        var extDir = Path.Combine(fakeHome.Path, ".pi", "agent", "extensions");
        Directory.CreateDirectory(extDir);
        var extPath = Path.Combine(extDir, "kcap.ts");
        await File.WriteAllTextAsync(extPath, "// stale extension body");

        var exit = await PluginCommand.HandleAsync(
            ["plugin", "install", "--pi", "--if-installed"],
            TestEnv(fakeHome.Path));
        await Assert.That(exit).IsEqualTo(0);

        var body = await File.ReadAllTextAsync(extPath);
        await Assert.That(body).DoesNotContain("stale extension body");
        await Assert.That(File.Exists(Path.Combine(extDir, ".kcap-extension-version"))).IsTrue();
    }

    [Test]
    public async Task Remove_pi_deletes_extension_and_marker() {
        using var fakeHome = new TempDir();

        var extDir = Path.Combine(fakeHome.Path, ".pi", "agent", "extensions");
        Directory.CreateDirectory(extDir);
        var extPath = Path.Combine(extDir, "kcap.ts");
        var marker  = Path.Combine(extDir, ".kcap-extension-version");
        await File.WriteAllTextAsync(extPath, "export default function(pi){}");
        await File.WriteAllTextAsync(marker, "1.0.0");

        var exit = await PluginCommand.HandleAsync(
            ["plugin", "remove", "--pi"],
            TestEnv(fakeHome.Path));
        await Assert.That(exit).IsEqualTo(0);

        await Assert.That(File.Exists(extPath)).IsFalse();
        await Assert.That(File.Exists(marker)).IsFalse();
    }

    // ── MCP-bridge extension + AGENTS.md steering ─────────────────
    // Exercised via the --if-installed refresh path (PATH-independent) with a
    // seeded ingest extension as the opt-in signal, mirroring the Gemini tests.

    [Test]
    public async Task Install_pi_if_installed_installs_mcp_bridge_and_agents_md() {
        using var fakeHome = new TempDir();
        var extDir = Path.Combine(fakeHome.Path, ".pi", "agent", "extensions");
        Directory.CreateDirectory(extDir);
        await File.WriteAllTextAsync(Path.Combine(extDir, "kcap.ts"), "// stale ingest");

        var exit = await PluginCommand.HandleAsync(
            ["plugin", "install", "--pi", "--if-installed"], TestEnv(fakeHome.Path));
        await Assert.That(exit).IsEqualTo(0);

        await Assert.That(File.Exists(Path.Combine(extDir, "kcap-mcp.ts"))).IsTrue();
        var agents = Path.Combine(fakeHome.Path, ".pi", "agent", "AGENTS.md");
        await Assert.That(File.Exists(agents)).IsTrue();
        await Assert.That(await File.ReadAllTextAsync(agents)).Contains(AgentInstructionsWriter.BeginMarker);
    }

    [Test]
    public async Task Install_pi_if_installed_heals_deleted_mcp_bridge_with_current_marker() {
        using var fakeHome = new TempDir();
        var extDir = Path.Combine(fakeHome.Path, ".pi", "agent", "extensions");
        Directory.CreateDirectory(extDir);
        // Opt-in signal: the ingest extension is present.
        await File.WriteAllTextAsync(Path.Combine(extDir, "kcap.ts"), "// stale ingest");
        // A CURRENT MCP marker but NO kcap-mcp.ts (user deleted the file). A marker-only "current"
        // state must NOT let the refresh skip recreating the bridge file.
        var mcpPath = Path.Combine(extDir, "kcap-mcp.ts");
        PiMcpExtensionInstaller.WriteMarker(mcpPath);
        await Assert.That(File.Exists(mcpPath)).IsFalse();

        var exit = await PluginCommand.HandleAsync(
            ["plugin", "install", "--pi", "--if-installed"], TestEnv(fakeHome.Path));
        await Assert.That(exit).IsEqualTo(0);

        await Assert.That(File.Exists(mcpPath)).IsTrue();  // healed despite the current marker
    }

    [Test]
    public async Task Install_pi_skip_mcp_omits_bridge_but_keeps_instructions() {
        using var fakeHome = new TempDir();
        var extDir = Path.Combine(fakeHome.Path, ".pi", "agent", "extensions");
        Directory.CreateDirectory(extDir);
        await File.WriteAllTextAsync(Path.Combine(extDir, "kcap.ts"), "// stale ingest");

        var exit = await PluginCommand.HandleAsync(
            ["plugin", "install", "--pi", "--if-installed", "--skip-pi-mcp"], TestEnv(fakeHome.Path));
        await Assert.That(exit).IsEqualTo(0);

        await Assert.That(File.Exists(Path.Combine(extDir, "kcap-mcp.ts"))).IsFalse();
        await Assert.That(File.Exists(Path.Combine(fakeHome.Path, ".pi", "agent", "AGENTS.md"))).IsTrue();
    }

    [Test]
    public async Task Install_pi_skip_instructions_omits_agents_md_but_keeps_bridge() {
        using var fakeHome = new TempDir();
        var extDir = Path.Combine(fakeHome.Path, ".pi", "agent", "extensions");
        Directory.CreateDirectory(extDir);
        await File.WriteAllTextAsync(Path.Combine(extDir, "kcap.ts"), "// stale ingest");

        var exit = await PluginCommand.HandleAsync(
            ["plugin", "install", "--pi", "--if-installed", "--skip-pi-instructions"], TestEnv(fakeHome.Path));
        await Assert.That(exit).IsEqualTo(0);

        await Assert.That(File.Exists(Path.Combine(extDir, "kcap-mcp.ts"))).IsTrue();
        await Assert.That(File.Exists(Path.Combine(fakeHome.Path, ".pi", "agent", "AGENTS.md"))).IsFalse();
    }

    [Test]
    public async Task Install_pi_preserves_user_agents_md_content() {
        using var fakeHome = new TempDir();
        var agentDir = Path.Combine(fakeHome.Path, ".pi", "agent");
        var extDir   = Path.Combine(agentDir, "extensions");
        Directory.CreateDirectory(extDir);
        await File.WriteAllTextAsync(Path.Combine(extDir, "kcap.ts"), "// stale ingest");
        var agents = Path.Combine(agentDir, "AGENTS.md");
        await File.WriteAllTextAsync(agents, "# My Pi instructions\nKeep this line.\n");

        var exit = await PluginCommand.HandleAsync(
            ["plugin", "install", "--pi", "--if-installed"], TestEnv(fakeHome.Path));
        await Assert.That(exit).IsEqualTo(0);

        var body = await File.ReadAllTextAsync(agents);
        await Assert.That(body).Contains("Keep this line.");                    // user content preserved
        await Assert.That(body).Contains(AgentInstructionsWriter.BeginMarker);  // kcap block added
    }

    [Test]
    public async Task Remove_pi_removes_mcp_bridge_and_instructions_block() {
        using var fakeHome = new TempDir();
        var agentDir = Path.Combine(fakeHome.Path, ".pi", "agent");
        var extDir   = Path.Combine(agentDir, "extensions");
        Directory.CreateDirectory(extDir);
        await File.WriteAllTextAsync(Path.Combine(extDir, "kcap.ts"), "export default function(pi){}");
        await File.WriteAllTextAsync(Path.Combine(extDir, "kcap-mcp.ts"), "export default async function(pi){}");
        var agents = Path.Combine(agentDir, "AGENTS.md");
        await File.WriteAllTextAsync(agents, "# Mine\n");
        AgentInstructionsWriter.Write(agents, "kcap steering body");

        var exit = await PluginCommand.HandleAsync(
            ["plugin", "remove", "--pi"], TestEnv(fakeHome.Path));
        await Assert.That(exit).IsEqualTo(0);

        await Assert.That(File.Exists(Path.Combine(extDir, "kcap-mcp.ts"))).IsFalse();
        var body = await File.ReadAllTextAsync(agents);
        await Assert.That(body).DoesNotContain(AgentInstructionsWriter.BeginMarker);  // kcap block stripped
        await Assert.That(body).Contains("# Mine");                                    // user content kept
    }

    static PluginEnvironment TestEnv(string fakeHome) => new(
        HomeDirectory:     fakeHome,
        ResolvePluginPath: () => null,
        Stdout:            TextWriter.Null,
        Stderr:            TextWriter.Null
    );
}
