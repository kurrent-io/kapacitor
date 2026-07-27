using Capacitor.Cli.Core;
using Capacitor.Cli.Core.Pi;

namespace Capacitor.Cli.Tests.Unit;

/// <summary>
/// Covers the Pi MCP-bridge extension installer: kcap-mcp.ts write/remove
/// + the version-marker helpers, and content assertions on the embedded bridge. Pi
/// has no built-in MCP, so kcap ships a second TypeScript extension that spawns the
/// <c>kcap mcp &lt;name&gt;</c> servers and registers their tools. It's a sibling of
/// the live-ingest extension with a DISTINCT marker so the two install independently.
/// </summary>
public class PiMcpExtensionInstallerTests {
    [Test]
    public async Task install_writes_extension_and_marker() {
        using var tmp = new TempDir();
        var path = Path.Combine(tmp.Path, "extensions", "kcap-mcp.ts");

        var ok = PiMcpExtensionInstaller.Install(path);
        await Assert.That(ok).IsTrue();
        await Assert.That(File.Exists(path)).IsTrue();

        await Assert.That(PiMcpExtensionInstaller.IsInstalled(path)).IsTrue();
        await Assert.That(PiMcpExtensionInstaller.ReadMarker(path)).IsEqualTo(CapacitorVersion.Current());
    }

    [Test]
    public async Task embedded_bridge_has_the_required_shape() {
        using var tmp = new TempDir();
        var path = Path.Combine(tmp.Path, "extensions", "kcap-mcp.ts");
        PiMcpExtensionInstaller.Install(path);

        var content = await File.ReadAllTextAsync(path);

        // Async factory (pi awaits it before session_start → tools ready turn 1).
        await Assert.That(content).Contains("export default async function");
        // Bridges every kcap server Pi ships, including analytics (the guided tour's menu
        // query needs query_analytics — kcap-workitems stays Claude-plugin-only).
        await Assert.That(content).Contains("[\"review\", \"sessions\", \"flows\", \"memory\", \"analytics\"]");
        // MCP handshake incl. the mandatory notifications/initialized.
        await Assert.That(content).Contains("initialize");
        await Assert.That(content).Contains("notifications/initialized");
        await Assert.That(content).Contains("tools/list");
        await Assert.That(content).Contains("tools/call");
        // Registers each tool as a Pi tool with the all-underscore name.
        await Assert.That(content).Contains("pi.registerTool");
        await Assert.That(content).Contains("\"kcap_\" + server + \"_\"");
        // Session-scoped lifecycle: register-once guard + holder + respawn on session_start.
        await Assert.That(content).Contains("function startBridge");
        await Assert.That(content).Contains("function stopBridge");
        await Assert.That(content).Contains("session_start");
        await Assert.That(content).Contains("session_shutdown");
        // Parallel, bounded startup (four hung servers ⇒ ~one timeout, not 4×).
        await Assert.That(content).Contains("Promise.all");
        // Teardown ladder EOF → SIGTERM → SIGKILL, plus a globalThis-guarded sync exit backstop.
        await Assert.That(content).Contains("SIGTERM");
        await Assert.That(content).Contains("SIGKILL");
        await Assert.That(content).Contains("process.on(\"exit\"");
        await Assert.That(content).Contains("__kcapPiMcpBridge");
        // MCP isError → Pi tool failure (execute throws).
        await Assert.That(content).Contains("result.isError");
        await Assert.That(content).Contains("throw new Error");
        // Dependency-free spawn of the stdio servers.
        await Assert.That(content).Contains("node:child_process");
        await Assert.That(content).Contains("\"mcp\", this.server");
    }

    [Test]
    public async Task marker_is_distinct_from_the_ingest_extension_marker() {
        // The bridge and the ingest extension live in the same dir; a shared marker
        // would make one clobber the other's version tracking.
        await Assert.That(PiMcpExtensionInstaller.MarkerFileName)
            .IsNotEqualTo(PiExtensionInstaller.MarkerFileName);
    }

    [Test]
    public async Task is_installed_true_when_only_marker_present() {
        using var tmp = new TempDir();
        var path = Path.Combine(tmp.Path, "extensions", "kcap-mcp.ts");
        var dir  = Path.GetDirectoryName(path)!;
        Directory.CreateDirectory(dir);
        await File.WriteAllTextAsync(Path.Combine(dir, PiMcpExtensionInstaller.MarkerFileName), "9.9.9");

        await Assert.That(PiMcpExtensionInstaller.IsInstalled(path)).IsTrue();
        await Assert.That(File.Exists(path)).IsFalse();
    }

    [Test]
    public async Task remove_deletes_extension_and_marker() {
        using var tmp = new TempDir();
        var path = Path.Combine(tmp.Path, "extensions", "kcap-mcp.ts");
        PiMcpExtensionInstaller.Install(path);

        var removed = PiMcpExtensionInstaller.Remove(path);
        await Assert.That(removed).IsTrue();
        await Assert.That(File.Exists(path)).IsFalse();
        await Assert.That(PiMcpExtensionInstaller.IsInstalled(path)).IsFalse();
    }

    [Test]
    public async Task remove_returns_false_when_absent() {
        using var tmp = new TempDir();
        var path = Path.Combine(tmp.Path, "extensions", "kcap-mcp.ts");

        await Assert.That(PiMcpExtensionInstaller.Remove(path)).IsFalse();
    }

    sealed class TempDir : IDisposable {
        public string Path { get; } = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(),
            $"kcap-pi-mcp-ext-test-{Guid.NewGuid().ToString("N")[..8]}"
        );
        public TempDir() => Directory.CreateDirectory(Path);
        public void Dispose() {
            try { Directory.Delete(Path, true); } catch { /* best effort */ }
        }
    }
}
