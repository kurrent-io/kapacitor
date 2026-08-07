using Capacitor.Cli.Core.Acp;
using Capacitor.Cli.Daemon.Acp;

namespace Capacitor.Cli.Tests.Unit.Acp;

/// <summary>
/// Unlike <see cref="KiroReviewerHomeTests"/>'s target, this home is not empty at spawn — it carries
/// a written <c>mcp_config.json</c> for the injected servers. The absence of the kcap plugin
/// directory is what keeps capture single-lane (a probe confirmed <c>agy -p</c> loads and fires it
/// when present), so that absence is asserted directly rather than inferred from "we wrote nothing".
/// </summary>
public class AntigravityReviewerHomeTests {
    [Test]
    public async Task The_home_carries_only_the_injected_mcp_server() {
        if (OperatingSystem.IsWindows()) return;
        using var root = new TempDir();
        var home = AntigravityReviewerHome.Create(
            root.Path, "epoch1", "agent1",
            [new AcpMcpServerSpec("kcap-flow-result", "kcap", ["mcp", "flow-result"], [])]);

        var mcp = Path.Combine(home, ".gemini", "config", "mcp_config.json");
        await Assert.That(File.Exists(mcp)).IsTrue();
        await Assert.That(await File.ReadAllTextAsync(mcp)).Contains("kcap-flow-result");

        // The kcap plugin dir must NOT exist — its absence is what keeps capture single-lane.
        // If a future change seeds a fuller home, this is the assertion that catches it.
        await Assert.That(Directory.Exists(Path.Combine(home, ".gemini", "config", "plugins"))).IsFalse();
    }

    [Test]
    public async Task The_home_is_owner_only_from_creation() {
        if (OperatingSystem.IsWindows()) return;
        using var root = new TempDir();
        var home = AntigravityReviewerHome.Create(root.Path, "epoch1", "agent1", []);
        var mode = File.GetUnixFileMode(home);

        await Assert.That(mode.HasFlag(UnixFileMode.GroupRead)).IsFalse();
        await Assert.That(mode.HasFlag(UnixFileMode.OtherRead)).IsFalse();
    }

    [Test]
    public async Task Sweep_removes_foreign_epochs_and_keeps_the_current_one() {
        if (OperatingSystem.IsWindows()) return;
        using var root = new TempDir();
        var stale   = AntigravityReviewerHome.Create(root.Path, "old",     "a1", []);
        var current = AntigravityReviewerHome.Create(root.Path, "current", "a2", []);

        AntigravityReviewerHome.SweepStale(root.Path, "current");

        await Assert.That(Directory.Exists(stale)).IsFalse();
        await Assert.That(Directory.Exists(current)).IsTrue();
    }

    sealed class TempDir : IDisposable {
        public string Path { get; } =
            System.IO.Path.Combine(System.IO.Path.GetTempPath(), "kcap-agy-home-tests-" + Guid.NewGuid().ToString("N"));

        public TempDir() => Directory.CreateDirectory(Path);

        public void Dispose() {
            try { Directory.Delete(Path, recursive: true); } catch { /* best-effort cleanup */ }
        }
    }
}
