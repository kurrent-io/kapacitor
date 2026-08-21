using Capacitor.Cli.Commands;

namespace Capacitor.Cli.Tests.Unit.Commands;

public class GuardedDiscoveryTests {
    [TempDir] public required TempDir Tmp { get; init; }

    [Test]
    public async Task EnumerateFiles_survives_symlink_cycle() {
        var sub = Tmp.CreateDir("sub");
        sub.CreateFile("a.jsonl", "{}");

        // A directory symlink pointing back at the root creates a cycle.
        try { Directory.CreateSymbolicLink(sub.PathTo("loop"), Tmp.Path); }
        catch { return; /* platform without symlink perms — skip */ }

        var files = GuardedDiscovery.EnumerateFiles(Tmp.Path, "*.jsonl").ToList();

        await Assert.That(files.Count).IsEqualTo(1);
        await Assert.That(files[0]).EndsWith("a.jsonl");
    }

    [Test]
    public async Task EnumerateFiles_returns_empty_for_missing_root() {
        var files = GuardedDiscovery.EnumerateFiles(Tmp.PathTo("does-not-exist"), "*.jsonl").ToList();
        await Assert.That(files.Count).IsEqualTo(0);
    }

    [Test]
    public async Task EnumerateFiles_flat_mode_excludes_nested_files() {
        // Top-level file — must be returned in flat mode.
        Tmp.CreateFile("top.jsonl", "{}");

        // Nested file one level down — must NOT be returned in flat mode.
        Tmp.CreateFile(["nested", "deep.jsonl"], "{}");

        var files = GuardedDiscovery.EnumerateFiles(Tmp.Path, "*.jsonl", recursive: false).ToList();

        await Assert.That(files.Count).IsEqualTo(1);
        await Assert.That(files[0]).EndsWith("top.jsonl");
    }
}
