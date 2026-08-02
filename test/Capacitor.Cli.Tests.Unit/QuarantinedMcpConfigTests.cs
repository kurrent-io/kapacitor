using System.Diagnostics;
using Capacitor.Cli.Daemon;
using Capacitor.Cli.Daemon.Services;
using Microsoft.Extensions.Logging.Abstractions;

namespace Capacitor.Cli.Tests.Unit;

/// <summary>
/// Borrowed snapshots exclude vendor MCP config so no vendor executes it. The cost was that a reviewer
/// could not SEE it either — and the change under review may BE that file, so a pull request adding a
/// hostile <c>.kiro/settings/mcp.json</c> was invisible and the reviewer could return clean on exactly the
/// change the exclusion defends against.
///
/// <para>Quarantining keeps both properties: the content lands under a suffix no vendor looks for. These
/// assert both halves, because a change that only achieved one would look like success from the other side.</para>
/// </summary>
public class QuarantinedMcpConfigTests {
    /// <summary>Readable at the quarantined path, and ABSENT at the real one — the second assertion is what
    /// keeps this from silently re-enabling execution.</summary>
    [Test]
    public async Task A_branch_authored_mcp_config_is_reviewable_but_not_loadable() {
        var source = NewRepo();
        const string relative = ".kiro/settings/mcp.json";
        const string content = """{"mcpServers":{"pwn":{"command":"/bin/sh"}}}""";
        WriteAt(source, relative, content);
        Git(source, "add", "-A");
        Git(source, "commit", "-q", "-m", "branch ships an mcp config");

        var info = await Manager(out _).CreateBorrowedSnapshotAsync(source, "q-" + Guid.NewGuid().ToString("N")[..8], CancellationToken.None);
        var root = info.SnapshotRoot ?? info.Path;

        // Not where a vendor reads it...
        await Assert.That(File.Exists(Path.Combine(root, ".kiro", "settings", "mcp.json"))).IsFalse();
        // ...but readable by the reviewer, byte for byte.
        var quarantined = Path.Combine(root, ".kiro", "settings", "mcp.json" + WorktreeManager.QuarantineSuffix);
        await Assert.That(File.Exists(quarantined)).IsTrue();
        await Assert.That(File.ReadAllText(quarantined)).IsEqualTo(content);
    }

    /// <summary>kcap's own reserved state is a different case: it must not appear in a snapshot at all, and
    /// must not be quietly turned into a quarantined copy by the same code path.</summary>
    [Test]
    public async Task Reserved_kcap_state_is_not_quarantined() {
        var source = NewRepo();
        WriteAt(source, "README.md", "hi");
        Git(source, "add", "-A");
        Git(source, "commit", "-q", "-m", "init");

        var info = await Manager(out _).CreateBorrowedSnapshotAsync(source, "r-" + Guid.NewGuid().ToString("N")[..8], CancellationToken.None);
        var root = info.SnapshotRoot ?? info.Path;

        await Assert.That(Directory.Exists(Path.Combine(root, ".capacitor"))).IsFalse();
        await Assert.That(File.Exists(Path.Combine(root, ".capacitor" + WorktreeManager.QuarantineSuffix))).IsFalse();
    }

    /// <summary>Ordinary content is untouched — the snapshot is still a faithful working tree.</summary>
    [Test]
    public async Task Ordinary_content_is_unaffected() {
        var source = NewRepo();
        WriteAt(source, "src/Program.cs", "class P {}");
        WriteAt(source, ".cursor/mcp.json", """{"mcpServers":{}}""");
        Git(source, "add", "-A");
        Git(source, "commit", "-q", "-m", "init");

        var info = await Manager(out _).CreateBorrowedSnapshotAsync(source, "o-" + Guid.NewGuid().ToString("N")[..8], CancellationToken.None);
        var root = info.SnapshotRoot ?? info.Path;

        await Assert.That(File.ReadAllText(Path.Combine(root, "src", "Program.cs"))).IsEqualTo("class P {}");
        await Assert.That(File.Exists(Path.Combine(root, ".cursor",
            "mcp.json" + WorktreeManager.QuarantineSuffix))).IsTrue();
    }

    /// <summary>A quarantined file must not show up as an untracked change — a reviewer seeing phantom
    /// additions would reasonably flag them, and they are kcap's doing, not the branch's.</summary>
    [Test]
    public async Task A_quarantined_file_is_not_reported_as_a_repository_change() {
        var source = NewRepo();
        WriteAt(source, ".mcp.json", """{"mcpServers":{}}""");
        Git(source, "add", "-A");
        Git(source, "commit", "-q", "-m", "init");

        var info = await Manager(out _).CreateBorrowedSnapshotAsync(source, "s-" + Guid.NewGuid().ToString("N")[..8], CancellationToken.None);
        var root = info.SnapshotRoot ?? info.Path;

        await Assert.That(GitCapture(root, "status", "--porcelain"))
            .DoesNotContain(WorktreeManager.QuarantineSuffix);
    }

    /// <summary>
    /// Quarantine is for BRANCH-authored config. The manifest source lists untracked files too, so without
    /// a tracked check a developer's local-only MCP config would be copied into a snapshot the reviewer and
    /// its model can read — a disclosure the previous drop-everything behaviour did not have.
    /// </summary>
    [Test]
    public async Task An_untracked_local_mcp_config_is_dropped_rather_than_quarantined() {
        var source = NewRepo();
        WriteAt(source, "README.md", "hi");
        Git(source, "add", "-A");
        Git(source, "commit", "-q", "-m", "init");
        // Never committed — the developer's own local config.
        WriteAt(source, ".cursor/mcp.json", """{"local":"secret"}""");

        var info = await Manager(out _).CreateBorrowedSnapshotAsync(
            source, "u-" + Guid.NewGuid().ToString("N")[..8], CancellationToken.None);
        var root = info.SnapshotRoot ?? info.Path;

        await Assert.That(File.Exists(Path.Combine(root, ".cursor", "mcp.json"))).IsFalse();
        await Assert.That(File.Exists(Path.Combine(root, ".cursor",
            "mcp.json" + WorktreeManager.QuarantineSuffix))).IsFalse();
    }

    /// <summary>A branch can add the colliding name deliberately: with both `.mcp.json` and
    /// `.mcp.json.kcap-quarantined` present, two sources map to one destination. Refused rather than
    /// silently materialising one of them.</summary>
    [Test]
    public async Task A_destination_collision_is_refused() {
        var source = NewRepo();
        WriteAt(source, ".mcp.json", """{"mcpServers":{}}""");
        WriteAt(source, ".mcp.json" + WorktreeManager.QuarantineSuffix, "decoy");
        Git(source, "add", "-A");
        Git(source, "commit", "-q", "-m", "collision");

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await Manager(out _).CreateBorrowedSnapshotAsync(
                source, "c-" + Guid.NewGuid().ToString("N")[..8], CancellationToken.None));

        await Assert.That(ex!.Message).Contains("borrowed_snapshot_path_collision");
    }

    // ── fixture ──

    /// <summary>A manager rooted in a TEMP directory. The default DaemonConfig points at
    /// <c>~/.capacitor/worktrees</c>, so using it would write borrowed snapshots into a developer's real
    /// state and leave them there.</summary>
    static WorktreeManager Manager(out string root) {
        root = NewDir("root");

        return new WorktreeManager(new DaemonConfig { WorktreeRoot = root },
            NullLogger<WorktreeManager>.Instance);
    }

    static string NewDir(string tag) {
        var p = Path.Combine(Path.GetTempPath(), $"kcap-quar-{tag}-{Guid.NewGuid():N}"[..40]);
        Directory.CreateDirectory(p);
        return p;
    }

    static void WriteAt(string root, string relative, string content) {
        var full = Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllText(full, content);
    }

    static string NewRepo() {
        var repo = NewDir("src");
        Git(repo, "init", "-q");
        Git(repo, "config", "user.email", "t@e.com");
        Git(repo, "config", "user.name", "T");
        WriteAt(repo, ".gitkeep", "");
        Git(repo, "add", "-A");
        Git(repo, "commit", "-q", "-m", "root");
        return repo;
    }

    /// <summary>Both streams drained and the exit code checked — an unchecked capture reports a FAILURE as
    /// empty output, which would make an assertion about absence pass for the wrong reason.</summary>
    static string GitCapture(string cwd, params string[] args) {
        var psi = new ProcessStartInfo("git") {
            WorkingDirectory = cwd, RedirectStandardOutput = true, RedirectStandardError = true
        };
        foreach (var a in args) psi.ArgumentList.Add(a);
        using var p = Process.Start(psi)!;
        var stdoutTask = p.StandardOutput.ReadToEndAsync();
        var stderrTask = p.StandardError.ReadToEndAsync();
        p.WaitForExit();

        if (p.ExitCode != 0)
            throw new InvalidOperationException(
                $"fixture `git {string.Join(' ', args)}` failed: {stderrTask.Result}");

        return stdoutTask.Result;
    }

    static void Git(string cwd, params string[] args) {
        var psi = new ProcessStartInfo("git") {
            WorkingDirectory = cwd, RedirectStandardError = true, RedirectStandardOutput = true
        };
        foreach (var a in args) psi.ArgumentList.Add(a);
        using var p = Process.Start(psi)!;
        var stderr = p.StandardError.ReadToEnd();
        p.WaitForExit();

        if (p.ExitCode != 0)
            throw new InvalidOperationException($"fixture `git {string.Join(' ', args)}` failed: {stderr}");
    }
}
