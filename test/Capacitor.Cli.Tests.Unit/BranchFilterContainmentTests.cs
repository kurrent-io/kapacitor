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

    /// <summary>
    /// The git-lfs outcome, which now depends on whether the binary resolves. Resolvable: the driver is
    /// rebound to our canonical command so LFS keeps working. Not resolvable: there is nothing trustworthy
    /// to substitute, so it is DISABLED rather than left running the operator's command — pointer files
    /// instead of an unvetted execution. Both branches are asserted, because the machine running the suite
    /// decides which one is live and a test that silently covered neither would be worthless.
    /// </summary>
    [Test]
    public async Task An_allowlisted_driver_is_rebound_when_resolvable_and_disabled_otherwise() {
        var repo = NewRepo();
        Git(repo, "config", "filter.lfs.smudge", "git-lfs smudge -- %f");
        Git(repo, "config", "filter.lfs.process", "git-lfs filter-process");

        var joined = string.Join(' ', await WorktreeManager.BranchFilterOverridesAsync(repo));

        if (WorktreeManager.ResolvedAllowedFilterBinary is { } binary) {
            await Assert.That(joined).Contains(binary);
            await Assert.That(joined).Contains("filter.lfs.required=true");
        } else {
            await Assert.That(joined).Contains("filter.lfs.smudge=");
            await Assert.That(joined).Contains("filter.lfs.required=false");
        }
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

        // The non-allowlisted driver is always neutralised...
        await Assert.That(joined).Contains("filter.evil.smudge=");
        await Assert.That(joined).Contains("filter.evil.required=false");
        // ...while lfs is rebound rather than neutralised, when it can be.
        if (WorktreeManager.ResolvedAllowedFilterBinary is { } binary)
            await Assert.That(joined).Contains($"filter.lfs.smudge={binary}");
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

    /// <summary>
    /// Whatever the operator bound `lfs` to, the guarded command runs OUR command, not theirs. Vetting
    /// their string was the previous design and is unsound: git runs the whole value through a shell, so
    /// `git-lfs smudge -- %f; ./tools/f` passes any first-token check and then executes branch content, and
    /// a branch-owned `/repo/tools/git-lfs` passes by basename. Substitution removes the question.
    /// </summary>
    [Test]
    [Arguments("git-lfs smudge -- %f; ./tools/f")]   // shell chaining past a valid-looking first token
    [Arguments("/repo/tools/git-lfs smudge")]        // branch-owned binary with the right basename
    [Arguments("./tools/f")]
    public async Task An_allowlisted_driver_runs_our_command_not_the_operators(string command) {
        Skip.Unless(WorktreeManager.ResolvedAllowedFilterBinary is not null,
            "needs git-lfs on PATH to build the canonical form");

        var repo = NewRepo();
        Git(repo, "config", "filter.lfs.smudge", command);

        var joined = string.Join(' ', await WorktreeManager.BranchFilterOverridesAsync(repo));

        await Assert.That(joined).DoesNotContain("./tools/f");
        await Assert.That(joined).Contains("filter.lfs.smudge=");
        await Assert.That(joined).Contains(WorktreeManager.ResolvedAllowedFilterBinary!);
    }

    /// <summary>The substituted path must be ABSOLUTE — a bare `git-lfs` can be shadowed when the
    /// inherited PATH carries a relative component.</summary>
    [Test]
    public async Task The_substituted_command_uses_an_absolute_path() {
        Skip.Unless(WorktreeManager.ResolvedAllowedFilterBinary is not null, "needs git-lfs on PATH");

        await Assert.That(Path.IsPathRooted(WorktreeManager.ResolvedAllowedFilterBinary!)).IsTrue();
    }

    /// <summary>
    /// `-c key=value` splits at the FIRST '='. A driver legally named `evil=x` would be written as key
    /// `filter.evil`, leaving `filter.evil=x.smudge` live while the override looked applied. Refused rather
    /// than mis-encoded: a guard that silently does nothing is worse than a launch that fails.
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
