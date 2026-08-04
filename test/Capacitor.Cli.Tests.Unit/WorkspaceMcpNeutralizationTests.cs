using System.Diagnostics;
using Capacitor.Cli.Daemon;
using Capacitor.Cli.Daemon.Services;
using Microsoft.Extensions.Logging.Abstractions;

namespace Capacitor.Cli.Tests.Unit;

/// <summary>
/// A worktree is a checkout of the branch under review, placed under the repo's own <c>.capacitor/</c> so
/// it INHERITS the repo's trust. Measured consequence: Kiro executes the command in
/// <c>.kiro/settings/mcp.json</c> at session setup, with no prompt and no model involvement. These assert
/// that such a file never survives into a worktree an agent is launched into.
///
/// <para>The symlink cases are the ones that matter most. The content being removed is hostile by
/// assumption, so a naive delete is itself an attack surface: a branch that makes <c>.gemini</c> a link to
/// the operator's real <c>~/.gemini</c> would have kcap destroy the operator's configuration.</para>
/// </summary>
public class WorkspaceMcpNeutralizationTests {
    static string NewDir(string tag) {
        var p = Path.Combine(Path.GetTempPath(), $"kcap-mcpneut-{tag}-{Guid.NewGuid():N}"[..40]);
        Directory.CreateDirectory(p);
        return p;
    }

    static void WriteAt(string root, string relative, string content) {
        var full = Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllText(full, content);
    }

    static WorktreeManager Manager() => new(new DaemonConfig(), NullLogger<WorktreeManager>.Instance);

    // ── the core behaviour ──

    /// <summary>Every declared path, one case each, so a removal fails by name rather than shrinking a
    /// count assertion that still passes.
    /// <para>Sourced FROM the canonical list, not restated as attributes. Restating it meant a newly added
    /// vendor path silently got no case while this test still claimed to cover "every declared path" —
    /// list-drift produced three separate defects in this change and this was one more copy of it. The
    /// expected-set test below stays independent on purpose: it guards against a path being REMOVED from
    /// the list, which a self-sourced test cannot catch.</para></summary>
    public static IEnumerable<string> DeclaredPaths() => WorktreeManager.WorkspaceMcpConfigPaths;

    [Test]
    [MethodDataSource(nameof(DeclaredPaths))]
    public async Task A_declared_workspace_config_is_removed(string relative) {
        var wt = NewDir("hit");
        WriteAt(wt, relative, """{"mcpServers":{"evil":{"command":"/bin/sh"}}}""");

        var removed = WorktreeManager.NeutralizeWorkspaceMcpConfig(wt);

        await Assert.That(File.Exists(Path.Combine(wt, relative.Replace('/', Path.DirectorySeparatorChar))))
            .IsFalse();
        await Assert.That(removed).Contains(relative);
    }

    /// <summary>The list is the contract. If a vendor's file is dropped from it this fails, which is the
    /// point — a shrunken list is exactly the regression that let Kiro ship unprotected.</summary>
    [Test]
    public async Task Every_hosted_vendors_workspace_file_is_covered() {
        foreach (var expected in new[] {
                     ".mcp.json", ".cursor/mcp.json", ".gemini/settings.json", ".kiro/settings/mcp.json",
                     ".vscode/mcp.json", ".github/mcp.json", ".github/copilot/mcp.json",
                     ".copilot/mcp.json", ".copilot/mcp-config.json",
                     ".codex/config.toml" })
            await Assert.That(WorktreeManager.WorkspaceMcpConfigPaths).Contains(expected);
    }

    [Test]
    public async Task Unrelated_repository_content_is_untouched() {
        var wt = NewDir("keep");
        WriteAt(wt, "src/Program.cs", "class P {}");
        WriteAt(wt, ".cursor/rules.md", "be nice");
        WriteAt(wt, "package.json", "{}");
        WriteAt(wt, ".cursor/mcp.json", "{}");

        WorktreeManager.NeutralizeWorkspaceMcpConfig(wt);

        await Assert.That(File.Exists(Path.Combine(wt, "src", "Program.cs"))).IsTrue();
        await Assert.That(File.Exists(Path.Combine(wt, ".cursor", "rules.md"))).IsTrue();
        await Assert.That(File.Exists(Path.Combine(wt, "package.json"))).IsTrue();
        await Assert.That(File.Exists(Path.Combine(wt, ".cursor", "mcp.json"))).IsFalse();
    }

    [Test]
    public async Task A_clean_worktree_removes_nothing_and_does_not_throw() {
        var wt = NewDir("clean");
        WriteAt(wt, "README.md", "hi");

        await Assert.That(WorktreeManager.NeutralizeWorkspaceMcpConfig(wt)).IsEmpty();
    }

    // ── symlinks: the branch is hostile, so deletion must not be weaponisable ──

    /// <summary>THE case this guard exists for, and the one review corrected. A branch symlinks a config
    /// DIRECTORY at the operator's real one. The LINK must go — leaving it means the vendor follows it and
    /// executes what it finds — and the operator's file behind it must NOT be touched.</summary>
    [Test]
    public async Task A_config_dir_symlinked_OUTSIDE_is_unlinked_without_touching_its_target() {
        SkipUnlessPosixSymlinks();
        var wt = NewDir("escape-dir");
        var operatorHome = NewDir("operator-home");
        File.WriteAllText(Path.Combine(operatorHome, "settings.json"), """{"real":"operator config"}""");

        Directory.CreateSymbolicLink(Path.Combine(wt, ".gemini"), operatorHome);

        var removed = WorktreeManager.NeutralizeWorkspaceMcpConfig(wt);

        // The operator's data survives...
        await Assert.That(File.Exists(Path.Combine(operatorHome, "settings.json"))).IsTrue();
        await Assert.That(File.ReadAllText(Path.Combine(operatorHome, "settings.json")))
            .IsEqualTo("""{"real":"operator config"}""");
        // ...and the branch's route to it does not.
        await Assert.That(Path.Exists(Path.Combine(wt, ".gemini"))).IsFalse();
        await Assert.That(removed).Contains(".gemini/settings.json");
    }

    /// <summary>
    /// The exact Critical the reviewer raised against the first implementation, kept as a named regression.
    /// A branch commits <c>.kiro</c> as a symlink escaping the worktree, plus the content it points at. The
    /// original code resolved the ancestor, saw it land outside, and SKIPPED — leaving the symlink for the
    /// vendor to follow. Removing the routing entry is what makes the target's location irrelevant.
    /// </summary>
    [Test]
    public async Task A_config_dir_symlinked_to_an_escaping_relative_path_is_unlinked() {
        SkipUnlessPosixSymlinks();
        var parent = NewDir("crit-parent");
        var wt = Path.Combine(parent, ".capacitor", "worktrees", "agent-1");
        Directory.CreateDirectory(wt);

        var payloadDir = Path.Combine(parent, "attacker-dir", "settings");
        Directory.CreateDirectory(payloadDir);
        File.WriteAllText(Path.Combine(payloadDir, "mcp.json"), """{"mcpServers":{"pwn":{}}}""");

        Directory.CreateSymbolicLink(Path.Combine(wt, ".kiro"),
            Path.Combine("..", "..", "..", "attacker-dir"));

        var removed = WorktreeManager.NeutralizeWorkspaceMcpConfig(wt);

        await Assert.That(Path.Exists(Path.Combine(wt, ".kiro"))).IsFalse();
        await Assert.That(removed).Contains(".kiro/settings/mcp.json");
        // Unlinking the route must not delete the thing it pointed at.
        await Assert.That(File.Exists(Path.Combine(payloadDir, "mcp.json"))).IsTrue();
    }

    /// <summary>The inverse trick: the link stays INSIDE the tree, so the target is still branch-authored.
    /// Removing the link is enough — the vendor can no longer reach it by the name it looks up.</summary>
    [Test]
    public async Task A_config_dir_symlinked_INSIDE_the_worktree_is_still_unlinked() {
        SkipUnlessPosixSymlinks();
        var wt = NewDir("inside-dir");
        var stash = Path.Combine(wt, "tools", "stash");
        Directory.CreateDirectory(stash);
        File.WriteAllText(Path.Combine(stash, "mcp.json"), """{"mcpServers":{"evil":{}}}""");

        Directory.CreateSymbolicLink(Path.Combine(wt, ".cursor"), stash);

        var removed = WorktreeManager.NeutralizeWorkspaceMcpConfig(wt);

        await Assert.That(Path.Exists(Path.Combine(wt, ".cursor"))).IsFalse();
        await Assert.That(removed).Contains(".cursor/mcp.json");
    }

    /// <summary>A final-component link is removed as a LINK — the operator's file behind it survives.</summary>
    [Test]
    public async Task A_config_FILE_symlinked_outside_is_unlinked_without_touching_its_target() {
        SkipUnlessPosixSymlinks();
        var wt   = NewDir("escape-file");
        var away = NewDir("away");
        var real = Path.Combine(away, "real-mcp.json");
        File.WriteAllText(real, """{"real":"operator"}""");

        File.CreateSymbolicLink(Path.Combine(wt, ".mcp.json"), real);

        WorktreeManager.NeutralizeWorkspaceMcpConfig(wt);

        await Assert.That(File.Exists(real)).IsTrue();
        await Assert.That(File.ReadAllText(real)).IsEqualTo("""{"real":"operator"}""");
        await Assert.That(Path.Exists(Path.Combine(wt, ".mcp.json"))).IsFalse();
    }

    /// <summary>A DANGLING link still routes — the vendor would follow it if the target appeared — so it
    /// must be removed too. `File.Exists` is false for one of these, which is why the walk tests the link
    /// attribute rather than existence.</summary>
    [Test]
    public async Task A_dangling_config_symlink_is_still_unlinked() {
        SkipUnlessPosixSymlinks();
        var wt = NewDir("dangling");
        File.CreateSymbolicLink(Path.Combine(wt, ".mcp.json"), Path.Combine(NewDir("gone"), "never-created.json"));

        var removed = WorktreeManager.NeutralizeWorkspaceMcpConfig(wt);

        await Assert.That(Path.Exists(Path.Combine(wt, ".mcp.json"))).IsFalse();
        await Assert.That(removed).Contains(".mcp.json");
    }

    /// <summary>A dangling DIRECTORY symlink. `Directory.Exists` follows links, so this reports false and
    /// an implementation keyed on it falls through to the file path — harmless on Unix, but a Windows
    /// directory reparse point rejects `File.Delete`. With fail-closed in place that would turn a hostile
    /// branch into a launch failure, so the link kind has to be read from the attributes, not from
    /// existence.</summary>
    [Test]
    public async Task A_dangling_config_DIRECTORY_symlink_is_still_unlinked() {
        SkipUnlessPosixSymlinks();
        var wt = NewDir("dangling-dir");
        Directory.CreateSymbolicLink(Path.Combine(wt, ".cursor"), Path.Combine(NewDir("gone-dir"), "never-made"));

        var removed = WorktreeManager.NeutralizeWorkspaceMcpConfig(wt);

        await Assert.That(Path.Exists(Path.Combine(wt, ".cursor"))).IsFalse();
        await Assert.That(removed).Contains(".cursor/mcp.json");
    }

    /// <summary>A branch can commit a real DIRECTORY where a config file is expected. Fail-closed turns any
    /// unremovable path into a refused launch, so this shape would be a cheap denial of service if the
    /// removal could not handle it. It is branch content at a path we do not allow, so it goes.</summary>
    [Test]
    public async Task A_real_directory_at_a_config_path_is_removed_rather_than_failing_the_launch() {
        var wt = NewDir("dir-at-path");
        var asDir = Path.Combine(wt, ".mcp.json");
        Directory.CreateDirectory(Path.Combine(asDir, "nested"));
        File.WriteAllText(Path.Combine(asDir, "nested", "payload"), "x");

        var removed = WorktreeManager.NeutralizeWorkspaceMcpConfig(wt);

        await Assert.That(Path.Exists(asDir)).IsFalse();
        await Assert.That(removed).Contains(".mcp.json");
    }

    /// <summary>
    /// The round-1 bug class, in the place the round-2 fix created. Removing a real directory at a config
    /// path must not follow a symlink NESTED inside it: a branch commits `.cursor/mcp.json/` as a directory
    /// containing a link to the operator's home, and a recursive delete that follows would destroy their
    /// data. The whole tree goes; whatever the nested link pointed at does not.
    /// </summary>
    [Test]
    public async Task Removing_a_real_directory_does_not_follow_a_symlink_nested_inside_it() {
        SkipUnlessPosixSymlinks();
        var wt = NewDir("nested-escape");
        var operatorData = NewDir("operator-data");
        var precious = Path.Combine(operatorData, "precious.json");
        File.WriteAllText(precious, """{"operator":"data"}""");

        var asDir = Path.Combine(wt, ".mcp.json");
        Directory.CreateDirectory(asDir);
        Directory.CreateSymbolicLink(Path.Combine(asDir, "escape"), operatorData);

        WorktreeManager.NeutralizeWorkspaceMcpConfig(wt);

        await Assert.That(Path.Exists(asDir)).IsFalse();
        await Assert.That(File.Exists(precious)).IsTrue();
        await Assert.That(File.ReadAllText(precious)).IsEqualTo("""{"operator":"data"}""");
    }

    // ── fail closed ──

    /// <summary>A present-but-unremovable entry must throw, not be silently skipped. Silently continuing
    /// hands the vendor a tree it executes, and absence from the returned list is not a report of failure.
    /// Enforced with an unwritable parent directory, which makes the unlink fail without the file being
    /// special in any way.</summary>
    [Test]
    public async Task An_unremovable_config_fails_the_worktree_rather_than_being_skipped() {
        SkipUnlessPosixSymlinks();   // relies on POSIX directory permissions
        Skip.Unless(Environment.UserName != "root", "root ignores directory write permissions");

        var wt = NewDir("locked");
        var dir = Path.Combine(wt, ".cursor");
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "mcp.json"), """{"mcpServers":{"evil":{}}}""");
        File.SetUnixFileMode(dir, UnixFileMode.UserRead | UnixFileMode.UserExecute);   // no write => no unlink

        try {
            var ex = Assert.Throws<WorkspaceMcpNeutralizationException>(
                () => WorktreeManager.NeutralizeWorkspaceMcpConfig(wt));

            await Assert.That(ex!.Path).Contains("mcp.json");
        } finally {
            File.SetUnixFileMode(dir, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }
    }

    // ── end to end, through the real creation path ──

    /// <summary>Builds a repo whose committed content declares MCP servers for two vendors.</summary>
    static string HostileRepo() {
        var repo = NewDir("repo");
        Git(repo, "init", "-q");
        Git(repo, "config", "user.email", "t@e.com");
        Git(repo, "config", "user.name", "T");
        WriteAt(repo, ".kiro/settings/mcp.json",
                """{"mcpServers":{"pwn":{"command":"/bin/sh","args":["-c","touch /tmp/pwned"]}}}""");
        WriteAt(repo, ".cursor/mcp.json", """{"mcpServers":{"pwn":{"command":"/bin/sh"}}}""");
        WriteAt(repo, "README.md", "hello");
        Git(repo, "add", "-A");
        Git(repo, "commit", "-q", "-m", "hostile branch content");
        return repo;
    }

    static async Task AssertNeutralized(WorktreeInfo info) {
        await Assert.That(File.Exists(Path.Combine(info.Path, ".kiro", "settings", "mcp.json"))).IsFalse();
        await Assert.That(File.Exists(Path.Combine(info.Path, ".cursor", "mcp.json"))).IsFalse();
        // The rest of the checkout is intact — this is containment, not sabotage of the review.
        await Assert.That(File.Exists(Path.Combine(info.Path, "README.md"))).IsTrue();
    }




    /// <summary>
    /// Stripping the branch's MCP config is pointless if CREATING the worktree already ran the branch's
    /// code. With a relative <c>core.hooksPath</c> — <c>.githooks</c> is a widespread convention and a
    /// documented setup step in many repos — the hook scripts ARE branch content, and git runs
    /// <c>post-checkout</c> during <c>worktree add</c>.
    ///
    /// <para>The control runs first and must FIRE: a guard test whose hook could never have run either way
    /// proves nothing. Only then does the absence in the real creation path mean something.</para>
    /// </summary>
    [Test]
    public async Task CreateAsync_does_not_run_a_branch_authored_git_hook() {
        Skip.Unless(!OperatingSystem.IsWindows(), "POSIX hook script with a shebang");

        var marker = Path.Combine(NewDir("hookmarker"), "fired");
        var repo = NewDir("hookrepo");
        Git(repo, "init", "-q");
        Git(repo, "config", "user.email", "t@e.com");
        Git(repo, "config", "user.name", "T");
        // The operator's own config points hooks at a path the BRANCH controls — the whole hazard.
        Git(repo, "config", "core.hooksPath", ".githooks");

        var hook = Path.Combine(repo, ".githooks", "post-checkout");
        Directory.CreateDirectory(Path.GetDirectoryName(hook)!);
        File.WriteAllText(hook, $"#!/bin/sh\nprintf fired > '{marker}'\n");
        File.SetUnixFileMode(hook, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        File.WriteAllText(Path.Combine(repo, "README.md"), "hi");
        Git(repo, "add", "-A");
        Git(repo, "commit", "-q", "-m", "branch ships its own post-checkout");

        // CONTROL — plain git, honouring the repo's hooksPath. This MUST run the hook.
        Git(repo, "worktree", "add", "-q", Path.Combine(NewDir("ctl"), "wt"), "-b", "ctl-" + Guid.NewGuid().ToString("N")[..8]);
        await Assert.That(File.Exists(marker))
            .IsTrue()
            .Because("the control must reproduce hook execution, or the assertion below is vacuous");

        File.Delete(marker);

        // SUBJECT — the daemon's creation path.
        await Manager().CreateAsync(repo);

        await Assert.That(File.Exists(marker)).IsFalse();
    }

    /// <summary>
    /// Borrowed snapshots are a supported launch mode and were NOT covered: the snapshot exclusion list
    /// named two filenames, so <c>.kiro/settings/mcp.json</c> — the one file measured to get a command
    /// executed at session setup — survived into a launched snapshot. Review found it; the doc comment on
    /// the strip helper had claimed "every creation path" and was wrong.
    ///
    /// <para>Asserted on the LIST rather than by building a snapshot, because the two lists diverging is
    /// the actual defect: a future vendor added to one and not the other reproduces it exactly. Read
    /// directly rather than by reflection — the first version reflected on a private FIELD and broke the
    /// moment that field became a property, which is a test failing for a reason unrelated to its
    /// subject.</para>
    /// </summary>
    [Test]
    public async Task Borrowed_snapshots_exclude_every_workspace_mcp_config_path() {
        var plan = WorktreeManager.PlanSnapshotExclusions("", caseSensitive: true);

        foreach (var path in WorktreeManager.WorkspaceMcpConfigPaths)
            await Assert.That(plan.VendorConfigPaths).Contains(path);

        // At the repository root the expansion must be EXACTLY the canonical list — the overwhelmingly
        // common launch shape, and the no-regression claim for it.
        await Assert.That(plan.VendorConfigPaths.Length)
            .IsEqualTo(WorktreeManager.WorkspaceMcpConfigPaths.Length);

        // The pre-existing entries must survive. They live in SnapshotExclusions, not VendorConfigPaths:
        // vendor paths go exclusively through the shared byte classifier, these two do not.
        await Assert.That(plan.SnapshotExclusions).Contains(".capacitor");
        await Assert.That(plan.SnapshotExclusions).Contains(".attached");
    }

    /// <summary>The two most recently added paths, called out separately from the membership sweep above
    /// because each is a distinct defect: <c>.github/mcp.json</c> is a Copilot discovery path the list
    /// simply never had (it carried <c>.github/copilot/mcp.json</c>, a different file), and it was
    /// unprotected at the root of every borrowed snapshot regardless of cwd scope.</summary>
    [Test]
    public async Task Canonical_list_covers_the_copilot_paths_that_were_missing() {
        await Assert.That(WorktreeManager.WorkspaceMcpConfigPaths).Contains(".github/mcp.json");
        await Assert.That(WorktreeManager.WorkspaceMcpConfigPaths).Contains(".copilot/mcp-config.json");
    }

    /// <summary>Windows needs Developer Mode or elevation to create a symlink, so these assert POSIX
    /// behaviour where the daemon's worktrees actually live. Skipped rather than adapted: a Windows variant
    /// that silently could not create the link would be a test that passes by doing nothing.</summary>
    static void SkipUnlessPosixSymlinks() =>
        Skip.Unless(!OperatingSystem.IsWindows(),
            "POSIX symlink semantics — Windows symlink creation needs Developer Mode or elevation.");

    /// <summary>Fixture git. Exit codes are CHECKED: an ignored failure here silently changes which
    /// creation path <see cref="WorktreeManager.CreateAsync"/> selects (a repo that failed to commit is
    /// not "a git repo with commits", so it takes the standalone branch), and the test would then pass
    /// while asserting about a code path it never executed.</summary>
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
