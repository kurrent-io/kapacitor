using Capacitor.Cli.Daemon.Services;

namespace Capacitor.Cli.Daemon.Tests.Unit.Services;

public class FileSystemOverlayTests {
    [Test]
    public async Task OverlayDirectory_copies_regular_files() {
        using var tmp = new OverlayDirs();
        var sourceFile = Path.Combine(tmp.Source, "file.txt");
        File.WriteAllText(sourceFile, "hello");

        FileSystemOverlay.OverlayDirectory(tmp.Source, tmp.Dest);

        await Assert.That(File.Exists(Path.Combine(tmp.Dest, "file.txt"))).IsTrue();
    }

    [Test]
    public async Task OverlayDirectory_skips_symlinked_files() {
        using var tmp = new OverlayDirs();

        // Create an external file that the symlink points to
        var externalFile = Path.Combine(tmp.External, "secret.txt");
        File.WriteAllText(externalFile, "secret");

        // Create a symlink inside source pointing to external file
        var linkPath = Path.Combine(tmp.Source, "link.txt");
        File.CreateSymbolicLink(linkPath, externalFile);

        FileSystemOverlay.OverlayDirectory(tmp.Source, tmp.Dest);

        await Assert.That(File.Exists(Path.Combine(tmp.Dest, "link.txt"))).IsFalse();
    }

    [Test]
    public async Task OverlayDirectory_skips_symlinked_directories() {
        using var tmp = new OverlayDirs();

        // Create an external directory with content
        var externalDir = Path.Combine(tmp.External, "extdir");
        Directory.CreateDirectory(externalDir);
        File.WriteAllText(Path.Combine(externalDir, "inside.txt"), "contents");

        // Create a symlinked subdir in source pointing to external dir
        var linkDir = Path.Combine(tmp.Source, "linked-dir");
        Directory.CreateSymbolicLink(linkDir, externalDir);

        FileSystemOverlay.OverlayDirectory(tmp.Source, tmp.Dest);

        await Assert.That(Directory.Exists(Path.Combine(tmp.Dest, "linked-dir"))).IsFalse();
    }

    [Test]
    public async Task OverlayDirectory_handles_symlink_loop_without_recursion() {
        using var tmp = new OverlayDirs();

        // Create a symlink loop: source/loop → source (points back to its ancestor)
        var loopLink = Path.Combine(tmp.Source, "loop");
        Directory.CreateSymbolicLink(loopLink, tmp.Source);

        // Should complete without StackOverflowException or hang
        FileSystemOverlay.OverlayDirectory(tmp.Source, tmp.Dest);

        // No loop directory should have been created in dest
        await Assert.That(Directory.Exists(Path.Combine(tmp.Dest, "loop"))).IsFalse();
    }

    [Test]
    public async Task OverlayDirectory_does_not_recurse_into_nested_git_worktree() {
        using var tmp = new OverlayDirs();

        // A normal config subdir IS copied.
        var commands = Path.Combine(tmp.Source, "commands");
        Directory.CreateDirectory(commands);
        File.WriteAllText(Path.Combine(commands, "cmd.md"), "do a thing");

        // A nested worktree (marked by a .git pointer FILE) must NOT be recursed into — this is the
        // .claude/worktrees/<x> shape that ballooned the overlay to gigabytes and wedged the daemon.
        var nested = Path.Combine(tmp.Source, "worktrees", "feature-x");
        Directory.CreateDirectory(nested);
        File.WriteAllText(Path.Combine(nested, ".git"), "gitdir: /repo/.git/worktrees/feature-x");
        File.WriteAllText(Path.Combine(nested, "huge.bin"), "pretend this is gigabytes");

        FileSystemOverlay.OverlayDirectory(tmp.Source, tmp.Dest);

        await Assert.That(File.Exists(Path.Combine(tmp.Dest, "commands", "cmd.md"))).IsTrue();
        await Assert.That(File.Exists(Path.Combine(tmp.Dest, "worktrees", "feature-x", "huge.bin"))).IsFalse();
    }

    [Test]
    public async Task OverlayDirectory_does_not_recurse_into_nested_git_repo() {
        using var tmp = new OverlayDirs();

        // A regular nested repo, marked by a .git DIRECTORY, must also be skipped.
        var repo = Path.Combine(tmp.Source, "vendored");
        Directory.CreateDirectory(Path.Combine(repo, ".git"));
        File.WriteAllText(Path.Combine(repo, "code.cs"), "class C {}");

        FileSystemOverlay.OverlayDirectory(tmp.Source, tmp.Dest);

        await Assert.That(File.Exists(Path.Combine(tmp.Dest, "vendored", "code.cs"))).IsFalse();
    }

    sealed class OverlayDirs : IDisposable {
        readonly TempDir _root = new();

        public string Source { get; }
        public string Dest { get; }
        public string External { get; }

        public OverlayDirs() {
            Source   = _root.CreateDir("source");
            Dest     = _root.CreateDir("dest");
            External = _root.CreateDir("external");
        }

        public void Dispose() => _root.Dispose();
    }
}
