using Capacitor.Cli.Commands;

namespace Capacitor.Cli.Tests.Unit.Commands;

public class GuardedDiscoveryTests : IDisposable {
    readonly TempDir _tmp = new();
    readonly string  _root;
    public GuardedDiscoveryTests() => _root = _tmp.Path;

    public void Dispose() => _tmp.Dispose();

    [Test]
    public async Task EnumerateFiles_survives_symlink_cycle() {
        var sub = Path.Combine(_root, "sub");
        Directory.CreateDirectory(sub);
        File.WriteAllText(Path.Combine(sub, "a.jsonl"), "{}");

        // A directory symlink pointing back at the root creates a cycle.
        var loop = Path.Combine(sub, "loop");
        try { Directory.CreateSymbolicLink(loop, _root); }
        catch { return; /* platform without symlink perms — skip */ }

        var files = GuardedDiscovery.EnumerateFiles(_root, "*.jsonl").ToList();

        await Assert.That(files.Count).IsEqualTo(1);
        await Assert.That(files[0]).EndsWith("a.jsonl");
    }

    [Test]
    public async Task EnumerateFiles_returns_empty_for_missing_root() {
        var files = GuardedDiscovery.EnumerateFiles(Path.Combine(_root, "does-not-exist"), "*.jsonl").ToList();
        await Assert.That(files.Count).IsEqualTo(0);
    }

    [Test]
    public async Task EnumerateFiles_flat_mode_excludes_nested_files() {
        // Top-level file — must be returned in flat mode.
        File.WriteAllText(Path.Combine(_root, "top.jsonl"), "{}");

        // Nested file one level down — must NOT be returned in flat mode.
        var sub = Path.Combine(_root, "nested");
        Directory.CreateDirectory(sub);
        File.WriteAllText(Path.Combine(sub, "deep.jsonl"), "{}");

        var files = GuardedDiscovery.EnumerateFiles(_root, "*.jsonl", recursive: false).ToList();

        await Assert.That(files.Count).IsEqualTo(1);
        await Assert.That(files[0]).EndsWith("top.jsonl");
    }
}
