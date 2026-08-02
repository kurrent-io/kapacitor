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
/// <para>EVERY defined driver is disabled, with no exemption. Four designs tried to keep git-lfs working —
/// classify the command, allowlist the name, authenticate the binding, rebind to a resolved path — and
/// review defeated each at the exemption itself. Nothing is parsed, resolved or authenticated now, so there
/// is nothing to subvert. The cost is that LFS-tracked files appear as pointer text in agent worktrees; it
/// is documented and logged rather than silent.</para>
/// </summary>
public class BranchFilterContainmentTests {
    // ── every defined driver is disabled ──

    /// <summary>
    /// No command is inspected, so no command can evade the guard. These are the four shapes that defeated
    /// the classifier design plus the two that defeated its successors — all now irrelevant, which is the
    /// point of removing the classification entirely.
    /// </summary>
    [Test]
    [Arguments("./tools/f")]
    [Arguments("sh tools")]                            // bare relative file, no separator
    [Arguments("python filter.py")]
    [Arguments("/bin/true;./tools/f")]                 // rooted token, shell runs the relative half
    [Arguments("cat %f")]                              // %f substituted after any inspection
    [Arguments("git-lfs smudge -- %f; ./tools/f")]     // shell chaining past a trusted-looking token
    [Arguments("/usr/local/bin/filter")]
    public async Task Every_defined_driver_is_disabled_whatever_its_command(string command) {
        var repo = NewRepo();
        Git(repo, "config", "filter.custom.smudge", command);

        var joined = string.Join(' ', await WorktreeManager.BranchFilterOverridesAsync(repo));

        await Assert.That(joined).Contains("filter.custom.smudge=");
        await Assert.That(joined).Contains("filter.custom.required=false");
    }

    /// <summary>
    /// `lfs` has NO exemption. Four designs tried to keep it working and review defeated each one at the
    /// exemption itself, so there is no longer a name to impersonate, a binding to authenticate, or a path
    /// to resolve. The cost — LFS files as pointer text in agent worktrees — is documented and logged.
    /// </summary>
    [Test]
    public async Task The_lfs_driver_has_no_exemption() {
        var repo = NewRepo();
        Git(repo, "config", "filter.lfs.smudge", "git-lfs smudge -- %f");
        Git(repo, "config", "filter.lfs.process", "git-lfs filter-process");
        Git(repo, "config", "filter.lfs.required", "true");

        var joined = string.Join(' ', await WorktreeManager.BranchFilterOverridesAsync(repo));

        await Assert.That(joined).Contains("filter.lfs.smudge=");
        await Assert.That(joined).Contains("filter.lfs.required=false");
    }

    /// <summary>
    /// The override set must cover EXACTLY the drivers git reports for the repository — no more, no fewer.
    ///
    /// <para>Computed from git rather than hard-coded, because enumeration sees EFFECTIVE config: a host
    /// with git-lfs installed has a global <c>filter.lfs.*</c>, so "a repo with no filters of its own" is
    /// not a repo with no filters. An earlier version asserted an empty result and passed only on machines
    /// without git-lfs — green locally, red on CI, which is the test encoding its author's laptop.</para>
    /// </summary>
    [Test]
    public async Task The_override_set_covers_exactly_the_drivers_git_reports() {
        var repo = NewRepo();
        Git(repo, "config", "filter.custom.smudge", "./tools/f");

        var expected = EffectiveDriverNames(repo);
        var joined = string.Join(' ', await WorktreeManager.BranchFilterOverridesAsync(repo));

        await Assert.That(expected).Contains("custom");          // the fixture's own driver is in scope
        foreach (var driver in expected)
            await Assert.That(joined).Contains($"filter.{driver}.smudge=");

        // ...and nothing outside that set is emitted.
        var emitted = joined.Split(' ')
            .Where(static t => t.StartsWith("filter.", StringComparison.Ordinal) && t.EndsWith(".clean=", StringComparison.Ordinal))
            .Select(static t => t["filter.".Length..^".clean=".Length])
            .ToHashSet(StringComparer.Ordinal);

        await Assert.That(emitted.Except(expected).Any()).IsFalse();
    }

    /// <summary>Driver names git reports for the repo's EFFECTIVE config, global scope included.</summary>
    static HashSet<string> EffectiveDriverNames(string repo) {
        var psi = new ProcessStartInfo("git") {
            WorkingDirectory = repo, RedirectStandardOutput = true, RedirectStandardError = true
        };
        foreach (var a in new[] { "config", "--name-only", "--get-regexp", "^filter\\..*\\.(clean|smudge|process)$" })
            psi.ArgumentList.Add(a);

        using var p = Process.Start(psi)!;
        var stdout = p.StandardOutput.ReadToEnd();
        p.WaitForExit();

        return stdout.Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(static k => k.Trim().Split('.'))
            .Where(static parts => parts.Length >= 3)
            .Select(static parts => string.Join('.', parts[1..^1]))
            .ToHashSet(StringComparer.Ordinal);
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

    /// <summary>A newline inside a config VALUE must not hide a driver. A line-splitting inventory reads
    /// `cat\n./tools/f` as a safe record plus an ignored line while git executes the whole value — which is
    /// why enumeration is `--name-only -z` and never parses values.</summary>
    [Test]
    public async Task A_newline_inside_a_config_value_cannot_hide_a_driver() {
        var repo = NewRepo();
        Git(repo, "config", "filter.sneaky.smudge", "cat\n./tools/f");

        await Assert.That(string.Join(' ', await WorktreeManager.BranchFilterOverridesAsync(repo)))
            .Contains("filter.sneaky.smudge=");
    }

    /// <summary>
    /// `-c key=value` splits at the FIRST '='. A driver legally named `evil=x` would be written as key
    /// `filter.evil`, leaving `filter.evil=x.smudge` live while the override looked applied. Refused rather
    /// than mis-encoded; the env transport that would carry it safely is tracked separately.
    /// </summary>
    [Test]
    public async Task A_driver_name_that_cannot_be_safely_overridden_is_refused() {
        var repo = NewRepo();
        Git(repo, "config", "filter.evil=x.smudge", "./tools/f");

        var ex = await Assert.ThrowsAsync<BranchFilterInventoryException>(
            async () => await WorktreeManager.BranchFilterOverridesAsync(repo));

        await Assert.That(ex!.Message).Contains("evil=x");
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
