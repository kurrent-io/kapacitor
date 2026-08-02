using System.Diagnostics;
using Capacitor.Cli.Daemon;
using Capacitor.Cli.Daemon.Services;
using Microsoft.Extensions.Logging.Abstractions;

namespace Capacitor.Cli.Tests.Unit;

/// <summary>
/// <c>.gitattributes</c> is branch content and SELECTS which filter driver applies. When the operator's
/// driver command is relative it resolves against the worktree, so the branch supplies the executable and
/// git runs it during <c>worktree add</c> — before anything has neutralised the tree.
/// <c>core.hooksPath</c> does not affect filters, so the hook guard does nothing here.
///
/// <para>The hard requirement pulling the other way is <c>git-lfs</c>: disabling filters wholesale makes it
/// yield pointer files instead of content, silently. So the classifier has to separate branch-resolvable
/// commands from PATH- and absolutely-resolved ones, and both directions are asserted.</para>
/// </summary>
public class BranchResolvableFilterTests {
    // ── the classifier ──

    /// <summary>Commands the BRANCH can supply. `sh -c 'cat ./tools/x'` is included deliberately: the
    /// executable is PATH-resolved but the payload is not, and only inspecting the first token would miss
    /// it.</summary>
    [Test]
    [Arguments("./tools/filter")]
    [Arguments("../outside/filter")]
    [Arguments("tools/filter")]
    [Arguments("sh -c 'cat ./tools/x'")]
    [Arguments("python scripts/clean.py")]
    [Arguments(".\\tools\\filter.exe")]
    public async Task A_branch_resolvable_command_is_neutralized(string command) =>
        await Assert.That(WorktreeManager.ResolvesToBranchContent(command)).IsTrue();

    /// <summary>The git-lfs case and its relatives. A bare name is PATH-resolved and an absolute path is
    /// the operator's own — neither can be supplied by the branch, and disabling them would break real
    /// setups.</summary>
    [Test]
    [Arguments("git-lfs filter-process")]
    [Arguments("git-lfs clean -- %f")]
    [Arguments("/usr/local/bin/filter")]
    [Arguments("cat")]
    public async Task A_path_or_absolutely_resolved_command_is_left_alone(string command) =>
        await Assert.That(WorktreeManager.ResolvesToBranchContent(command)).IsFalse();

    // ── the emitted overrides ──

    /// <summary>
    /// A relative driver produces overrides, and they must include <c>required=false</c>. Measured: with
    /// <c>filter.x.required=true</c> an empty smudge command is FATAL and `worktree add` fails outright —
    /// so clearing the command alone would convert this guard into a launch failure for any repo using a
    /// required filter.
    /// </summary>
    [Test]
    public async Task A_relative_driver_yields_overrides_including_required_false() {
        var repo = NewRepo();
        Git(repo, "config", "filter.evil.smudge", "./tools/f");
        Git(repo, "config", "filter.evil.required", "true");

        var overrides = await WorktreeManager.BranchResolvableFilterOverridesAsync(repo);
        var joined = string.Join(' ', overrides);

        await Assert.That(joined).Contains("filter.evil.smudge=");
        await Assert.That(joined).Contains("filter.evil.clean=");
        await Assert.That(joined).Contains("filter.evil.process=");
        await Assert.That(joined).Contains("filter.evil.required=false");
    }

    /// <summary>The regression that matters for real repositories: git-lfs must not be touched.</summary>
    [Test]
    public async Task A_git_lfs_driver_yields_no_overrides() {
        var repo = NewRepo();
        Git(repo, "config", "filter.lfs.smudge", "git-lfs smudge -- %f");
        Git(repo, "config", "filter.lfs.clean", "git-lfs clean -- %f");
        Git(repo, "config", "filter.lfs.process", "git-lfs filter-process");
        Git(repo, "config", "filter.lfs.required", "true");

        await Assert.That(await WorktreeManager.BranchResolvableFilterOverridesAsync(repo)).IsEmpty();
    }

    /// <summary>A repo with no filters at all — `config --get-regexp` exits non-zero, which must read as
    /// "nothing to override" rather than as a failure.</summary>
    [Test]
    public async Task A_repo_with_no_filters_yields_no_overrides() =>
        await Assert.That(await WorktreeManager.BranchResolvableFilterOverridesAsync(NewRepo())).IsEmpty();

    /// <summary>Only the offending driver is disabled; a legitimate one alongside it keeps working.</summary>
    [Test]
    public async Task A_mixed_config_neutralizes_only_the_branch_resolvable_driver() {
        var repo = NewRepo();
        Git(repo, "config", "filter.lfs.process", "git-lfs filter-process");
        Git(repo, "config", "filter.evil.smudge", "./tools/f");

        var joined = string.Join(' ', await WorktreeManager.BranchResolvableFilterOverridesAsync(repo));

        await Assert.That(joined).Contains("filter.evil.");
        await Assert.That(joined).DoesNotContain("filter.lfs.");
    }

    /// <summary>A driver name containing dots must not be truncated — `filter.my.tool.smudge` is driver
    /// `my.tool`, and splitting naively would emit overrides for a driver that does not exist.</summary>
    [Test]
    public async Task A_dotted_driver_name_survives_parsing() {
        var repo = NewRepo();
        Git(repo, "config", "filter.my.tool.smudge", "./tools/f");

        var joined = string.Join(' ', await WorktreeManager.BranchResolvableFilterOverridesAsync(repo));

        await Assert.That(joined).Contains("filter.my.tool.smudge=");
    }

    // ── end to end ──

    /// <summary>
    /// The real thing: a branch that ships its own filter executable, with the operator's config pointing at
    /// it relatively. The control runs plain git and MUST execute the filter, or the assertion below proves
    /// nothing.
    /// </summary>
    [Test]
    public async Task CreateAsync_does_not_run_a_branch_supplied_smudge_filter() {
        Skip.Unless(!OperatingSystem.IsWindows(), "POSIX filter script with a shebang");

        var marker = Path.Combine(NewDir("filtermarker"), "fired");
        var repo = NewRepo();
        Directory.CreateDirectory(Path.Combine(repo, "tools"));
        var script = Path.Combine(repo, "tools", "f");
        File.WriteAllText(script, $"#!/bin/sh\nprintf fired > '{marker}'\ncat\n");
        File.SetUnixFileMode(script, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        // The filtered path must sort AFTER the script: git checks out in index order, so a filtered
        // `data.txt` would smudge before `tools/f` exists and the exec would merely fail. The attack is
        // therefore ordering-dependent, and naming it `zz.txt` is what makes the control actually fire —
        // the first version of this test used `data.txt` and its control failed, correctly.
        File.WriteAllText(Path.Combine(repo, "zz.txt"), "payload\n");
        File.WriteAllText(Path.Combine(repo, ".gitattributes"), "zz.txt filter=evil\n");
        Git(repo, "add", "-A");
        Git(repo, "commit", "-q", "-m", "branch ships its own filter");
        Git(repo, "config", "filter.evil.smudge", "./tools/f");

        // CONTROL — plain git honours the relative command and runs branch code.
        Git(repo, "worktree", "add", "-q", Path.Combine(NewDir("ctl"), "wt"),
            "-b", "ctl-" + Guid.NewGuid().ToString("N")[..8]);
        await Assert.That(File.Exists(marker))
            .IsTrue()
            .Because("the control must reproduce filter execution, or the assertion below is vacuous");

        File.Delete(marker);

        var info = await new WorktreeManager(new DaemonConfig(), NullLogger<WorktreeManager>.Instance)
            .CreateAsync(repo);

        await Assert.That(File.Exists(marker)).IsFalse();
        // The checkout still happened — a guard that broke worktree creation would also pass the line above.
        await Assert.That(File.Exists(Path.Combine(info.Path, "zz.txt"))).IsTrue();
    }

    // ── fixture ──

    static string NewDir(string tag) {
        var p = Path.Combine(Path.GetTempPath(), $"kcap-filt-{tag}-{Guid.NewGuid():N}"[..40]);
        Directory.CreateDirectory(p);
        return p;
    }

    static string NewRepo() {
        var repo = NewDir("repo");
        Git(repo, "init", "-q");
        Git(repo, "config", "user.email", "t@e.com");
        Git(repo, "config", "user.name", "T");
        File.WriteAllText(Path.Combine(repo, "README.md"), "hi");
        Git(repo, "add", "-A");
        Git(repo, "commit", "-q", "-m", "init");
        return repo;
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
