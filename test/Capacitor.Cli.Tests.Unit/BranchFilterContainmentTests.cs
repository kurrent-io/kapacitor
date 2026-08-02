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
/// <para>Containment is an allowlist of driver NAMES, not an analysis of command strings — a command is a
/// shell program and a tokeniser cannot decide what one will execute. The hard requirement pulling the other
/// way is <c>git-lfs</c>: disabling it does not fail loudly, it silently yields pointer files instead of
/// content, so it is allowlisted.</para>
/// </summary>
public class BranchFilterContainmentTests {
    // ── the allowlist ──

    /// <summary>
    /// Any driver the operator has defined that is NOT allowlisted gets disabled — regardless of what its
    /// command looks like. That is the whole point of the redesign: the first version classified the command
    /// string, and review defeated it four ways (`sh tools` and `python filter.py` execute a branch file
    /// with no separator at all; `/bin/true;./tools/f` is one rooted token whose shell runs the relative
    /// half; `%f` is substituted after any inspection). A command is a shell program; a name is not.
    /// </summary>
    [Test]
    [Arguments("./tools/f")]
    [Arguments("sh tools")]                  // bare relative file — the old rule called this safe
    [Arguments("python filter.py")]          // ditto
    [Arguments("/bin/true;./tools/f")]       // rooted token, shell runs the relative half
    [Arguments("cat %f")]                    // %f is substituted by git, after any inspection
    [Arguments("/usr/local/bin/filter")]     // even an absolute one: not allowlisted, not trusted here
    public async Task Any_non_allowlisted_driver_is_disabled_whatever_its_command(string command) {
        var repo = NewRepo();
        Git(repo, "config", "filter.custom.smudge", command);

        var joined = string.Join(' ', await WorktreeManager.BranchFilterOverridesAsync(repo));

        await Assert.That(joined).Contains("filter.custom.smudge=");
        await Assert.That(joined).Contains("filter.custom.required=false");
    }

    /// <summary>The git-lfs regression. Disabling it does not fail loudly — it silently yields pointer
    /// files instead of content — so it is allowlisted and must stay untouched.</summary>
    [Test]
    public async Task The_allowlisted_lfs_driver_is_left_alone() {
        var repo = NewRepo();
        Git(repo, "config", "filter.lfs.smudge", "git-lfs smudge -- %f");
        Git(repo, "config", "filter.lfs.process", "git-lfs filter-process");
        Git(repo, "config", "filter.lfs.required", "true");

        await Assert.That(await WorktreeManager.BranchFilterOverridesAsync(repo)).IsEmpty();
    }

    [Test]
    public async Task A_repo_with_no_filters_yields_no_overrides() =>
        await Assert.That(await WorktreeManager.BranchFilterOverridesAsync(NewRepo())).IsEmpty();

    [Test]
    public async Task A_mixed_config_disables_only_the_non_allowlisted_driver() {
        var repo = NewRepo();
        Git(repo, "config", "filter.lfs.process", "git-lfs filter-process");
        Git(repo, "config", "filter.evil.smudge", "./tools/f");

        var joined = string.Join(' ', await WorktreeManager.BranchFilterOverridesAsync(repo));

        await Assert.That(joined).Contains("filter.evil.");
        await Assert.That(joined).DoesNotContain("filter.lfs.");
    }

    /// <summary>`filter.my.tool.smudge` is driver `my.tool`; naive splitting would emit overrides for a
    /// driver that does not exist and leave the real one live.</summary>
    [Test]
    public async Task A_dotted_driver_name_survives_parsing() {
        var repo = NewRepo();
        Git(repo, "config", "filter.my.tool.smudge", "./tools/f");

        await Assert.That(string.Join(' ', await WorktreeManager.BranchFilterOverridesAsync(repo)))
            .Contains("filter.my.tool.smudge=");
    }

    /// <summary>
    /// A newline inside a config VALUE must not be able to hide a driver. A line-splitting inventory reads
    /// `cat\n./tools/f` as a safe record plus an ignored line while git executes the whole value — which is
    /// why enumeration is `--name-only -z` and never parses values at all.
    /// </summary>
    [Test]
    public async Task A_newline_inside_a_config_value_cannot_hide_a_driver() {
        var repo = NewRepo();
        Git(repo, "config", "filter.sneaky.smudge", "cat\n./tools/f");

        await Assert.That(string.Join(' ', await WorktreeManager.BranchFilterOverridesAsync(repo)))
            .Contains("filter.sneaky.smudge=");
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
