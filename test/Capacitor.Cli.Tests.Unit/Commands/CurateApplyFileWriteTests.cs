using Capacitor.Cli.Commands;
using Capacitor.Cli.Core.Curation;

namespace Capacitor.Cli.Tests.Unit.Commands;

public class CurateApplyFileWriteTests {
    [Test]
    public async Task WriteFileAtomic_writes_content_and_leaves_no_tmp() {
        using var dir = new TempDir();
        var path = dir.PathTo("AGENTS.md");

        CurateCommand.WriteFileAtomic(path, "hello\n");

        await Assert.That(await File.ReadAllTextAsync(path)).IsEqualTo("hello\n");
        await Assert.That(File.Exists(path + ".tmp")).IsFalse();
    }

    [Test]
    public async Task ResolveAndDeduplicateTargets_writes_symlink_target_and_preserves_link() {
        if (OperatingSystem.IsWindows()) return;

        using var dir = new TempDir();
        var agents = dir.PathTo("AGENTS.md");
        var claude = dir.PathTo("CLAUDE.md");
        await File.WriteAllTextAsync(agents, "old\n");
        File.CreateSymbolicLink(claude, agents);         // CLAUDE.md -> AGENTS.md

        // Two plans (as HandleApply would build) — one per candidate name, same new content.
        var plans = new List<FilePlan> {
            new(claude, CurateAction.Update, "new\n", Array.Empty<string>(), Array.Empty<string>()),
            new(agents, CurateAction.Update, "new\n", Array.Empty<string>(), Array.Empty<string>()),
        };

        var resolved = CurateCommand.ResolveAndDeduplicateTargets(plans);

        await Assert.That(resolved.Count).IsEqualTo(1);                                          // written once
        await Assert.That(Path.GetFullPath(resolved[0].Path)).IsEqualTo(Path.GetFullPath(agents)); // real target

        CurateCommand.WriteFileAtomic(resolved[0].Path, resolved[0].NewContent!);

        await Assert.That(await File.ReadAllTextAsync(agents)).IsEqualTo("new\n");        // original updated
        await Assert.That(new FileInfo(claude).LinkTarget).IsNotNull();                    // CLAUDE.md still a symlink
    }

    [Test]
    public async Task WriteFileAtomic_preserves_existing_unix_mode() {
        if (OperatingSystem.IsWindows()) return;

        using var dir = new TempDir();
        var path = dir.PathTo("CLAUDE.md");
        await File.WriteAllTextAsync(path, "old\n");
        File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead);
        var before = File.GetUnixFileMode(path);

        CurateCommand.WriteFileAtomic(path, "new\n");

        await Assert.That(File.GetUnixFileMode(path)).IsEqualTo(before);
        await Assert.That(await File.ReadAllTextAsync(path)).IsEqualTo("new\n");
    }
}
