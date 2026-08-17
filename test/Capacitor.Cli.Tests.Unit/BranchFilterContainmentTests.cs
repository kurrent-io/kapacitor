using System.Diagnostics;
using System.Runtime.Versioning;
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
/// is nothing to subvert. The cost is documented and logged rather than silent: an OWNED worktree checks
/// out through git and so holds LFS pointer text (standalone and borrowed snapshots carry the source's own
/// bytes), and the overrides cover kcap's creation commands only — not git the agent later runs there.</para>
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

        var overrides = await WorktreeManager.BranchFilterOverridesAsync(repo);

        await Assert.That(overrides).Contains(new GitConfigOverride("filter.custom.smudge", ""));
        await Assert.That(overrides).Contains(new GitConfigOverride("filter.custom.required", "false"));
    }

    /// <summary>
    /// `lfs` has NO exemption. Four designs tried to keep it working and review defeated each one at the
    /// exemption itself, so there is no longer a name to impersonate, a binding to authenticate, or a path
    /// to resolve. The cost is documented and logged: an OWNED worktree checks out through git and so
    /// holds LFS pointer text, while standalone and borrowed snapshots carry the source's own bytes.
    /// </summary>
    [Test]
    public async Task The_lfs_driver_has_no_exemption() {
        var repo = NewRepo();
        Git(repo, "config", "filter.lfs.smudge", "git-lfs smudge -- %f");
        Git(repo, "config", "filter.lfs.process", "git-lfs filter-process");
        Git(repo, "config", "filter.lfs.required", "true");

        var overrides = await WorktreeManager.BranchFilterOverridesAsync(repo);

        await Assert.That(overrides).Contains(new GitConfigOverride("filter.lfs.smudge", ""));
        await Assert.That(overrides).Contains(new GitConfigOverride("filter.lfs.required", "false"));
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
        var overrides = await WorktreeManager.BranchFilterOverridesAsync(repo);

        await Assert.That(expected).Contains("custom");          // the fixture's own driver is in scope
        foreach (var driver in expected)
            await Assert.That(overrides).Contains(new GitConfigOverride($"filter.{driver}.smudge", ""));

        // ...and nothing outside that set is emitted.
        var emitted = DisabledDriverNames(overrides);

        await Assert.That(emitted.Except(expected).Any()).IsFalse();
    }

    /// <summary>
    /// The enumeration regex matches lowercase <c>filter</c>/<c>clean</c>/<c>smudge</c>/<c>process</c>, but
    /// git config SECTION and VARIABLE names are case-insensitive — so is an operator config spelled
    /// <c>[Filter "evil"] Smudge</c> invisible to it, leaving a driver a branch could still select?
    ///
    /// <para>No: git canonicalizes section and variable names to lowercase when it reports keys, so
    /// <c>--get-regexp</c> yields <c>filter.evil.smudge</c> whatever the file says, and the lowercase
    /// override neutralizes it. Only the SUBSECTION keeps its case, which the pattern's <c>.*</c> covers.</para>
    ///
    /// <para>Measured end-to-end before writing this: with <c>[Filter "evil"] Smudge</c> and an absolute
    /// command, <c>worktree add</c> really does execute the driver, and adding the lowercase overrides
    /// stops it. This test pins the canonicalization the containment leans on — if a future git reported
    /// keys verbatim, the guard would silently miss a live driver, and this fails instead.</para>
    /// </summary>
    [Test]
    [Arguments("Filter", "evil", "Smudge", "filter.evil.smudge")]
    [Arguments("FILTER", "shouty", "PROCESS", "filter.shouty.process")]
    [Arguments("filter", "Mixed", "clean", "filter.Mixed.clean")]   // subsection case IS preserved
    public async Task A_mixed_case_config_spelling_is_still_enumerated_and_overridden(
            string section, string subsection, string variable, string canonical) {
        var repo = NewRepo();
        File.AppendAllText(Path.Combine(repo, ".git", "config"),
            $"[{section} \"{subsection}\"]\n\t{variable} = ./tools/f\n");

        // Precondition: git really does resolve the value under the canonical spelling.
        await Assert.That(EffectiveDriverNames(repo)).Contains(subsection);

        var overrides = await WorktreeManager.BranchFilterOverridesAsync(repo);
        var prefix = canonical[..canonical.LastIndexOf('.')];

        await Assert.That(overrides).Contains(new GitConfigOverride($"{prefix}.clean", ""));
        await Assert.That(overrides).Contains(new GitConfigOverride($"{prefix}.smudge", ""));
        await Assert.That(overrides).Contains(new GitConfigOverride($"{prefix}.process", ""));
        await Assert.That(overrides).Contains(new GitConfigOverride($"{prefix}.required", "false"));
    }

    /// <summary>The drivers an override set disables, read back from the keys it emits.</summary>
    static HashSet<string> DisabledDriverNames(GitConfigOverride[] overrides) =>
        overrides
            .Where(static o => o.Key.StartsWith("filter.", StringComparison.Ordinal)
                            && o.Key.EndsWith(".clean", StringComparison.Ordinal))
            .Select(static o => o.Key["filter.".Length..^".clean".Length])
            .ToHashSet(StringComparer.Ordinal);

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

        await Assert.That(await WorktreeManager.BranchFilterOverridesAsync(repo))
            .Contains(new GitConfigOverride("filter.my.tool.smudge", ""));
    }

    /// <summary>A newline inside a config VALUE must not hide a driver. A line-splitting inventory reads
    /// `cat\n./tools/f` as a safe record plus an ignored line while git executes the whole value — which is
    /// why enumeration is `--name-only -z` and never parses values.</summary>
    [Test]
    public async Task A_newline_inside_a_config_value_cannot_hide_a_driver() {
        var repo = NewRepo();
        Git(repo, "config", "filter.sneaky.smudge", "cat\n./tools/f");

        await Assert.That(await WorktreeManager.BranchFilterOverridesAsync(repo))
            .Contains(new GitConfigOverride("filter.sneaky.smudge", ""));
    }

    // ── end to end ──

    /// <summary>
    /// A driver named `evil=x` is CONTAINED, not refused. `-c key=value` splits at the first '=', so that
    /// name arrived as key `filter.evil` with value `x.smudge=` — the real driver stayed live while the
    /// override looked applied — and the guard refused such a name rather than mis-encode it. The env
    /// transport keeps the key/value boundary explicit, so the name is expressible and the launch proceeds.
    ///
    /// <para>Measured before this was written: plain git runs a driver whose name contains '=' (the control
    /// below), and `-c` really does fail to disable it. Git parses a `.gitattributes` attribute at its first
    /// '=' too, so `filter=evil=x` selects this driver — a branch can reach it.</para>
    /// </summary>
    [Test]
    [UnsupportedOSPlatform("windows")]
    public async Task An_equals_in_a_driver_name_is_contained_rather_than_refused() {
        Skip.Unless(!OperatingSystem.IsWindows(), "POSIX filter script with a shebang");

        var marker = Path.Combine(NewDir("eqmarker"), "fired");
        var repo = NewRepo();
        Directory.CreateDirectory(Path.Combine(repo, "tools"));
        var script = Path.Combine(repo, "tools", "f");
        File.WriteAllText(script, $"#!/bin/sh\nprintf fired > '{marker}'\ncat\n");
        File.SetUnixFileMode(script, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        File.WriteAllText(Path.Combine(repo, "zz.txt"), "payload\n");   // sorts after tools/f — see above
        File.WriteAllText(Path.Combine(repo, ".gitattributes"), "zz.txt filter=evil=x\n");
        Git(repo, "add", "-A");
        Git(repo, "commit", "-q", "-m", "branch selects a driver whose name contains '='");
        Git(repo, "config", "filter.evil=x.smudge", "./tools/f");

        // CONTROL — plain git honours the '='-named driver and runs branch code.
        Git(repo, "worktree", "add", "-q", Path.Combine(NewDir("ctl"), "wt"),
            "-b", "ctl-" + Guid.NewGuid().ToString("N")[..8]);
        await Assert.That(File.Exists(marker))
            .IsTrue()
            .Because("the control must reproduce filter execution, or the assertion below is vacuous");

        File.Delete(marker);

        var info = await new WorktreeManager(new DaemonConfig(), NullLogger<WorktreeManager>.Instance)
            .CreateAsync(repo);

        await Assert.That(File.Exists(marker)).IsFalse();
        // Contained, not refused: the worktree exists and the filtered path is materialised.
        await Assert.That(File.Exists(Path.Combine(info.Path, "zz.txt"))).IsTrue();
    }

    /// <summary>
    /// The real thing: a branch that ships its own filter executable, with the operator's config pointing at
    /// it relatively. The control runs plain git and MUST execute the filter, or the assertion below proves
    /// nothing.
    /// </summary>
    [Test]
    [UnsupportedOSPlatform("windows")]
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

    /// <summary>
    /// A driver name is BYTES, not text. git config accepts a subsection containing a raw 0xff, and this
    /// was a live bypass of the guard's own inventory: `--get-regexp` runs the platform regex in the
    /// ambient locale, where `.` will not span a byte that is invalid in that encoding, so
    /// <c>^filter\..*\.(clean|smudge|process)$</c> returned NOTHING for <c>[filter "ev\xffil"]</c> while
    /// <c>^filter\.</c> found it. Empty inventory, no overrides emitted, driver executes — the exact
    /// failure this file exists to prevent, arriving through the enumeration rather than the command.
    ///
    /// <para>Enumeration no longer uses a pattern. And because such a name cannot survive a round trip
    /// through a UTF-8 string, an override built from it would name a DIFFERENT driver while looking
    /// applied, so this refuses instead of guessing. The control proves plain git really does run it.</para>
    /// </summary>
    [Test]
    [UnsupportedOSPlatform("windows")]
    public async Task A_driver_name_that_is_not_valid_utf8_is_refused_rather_than_missed() {
        Skip.Unless(!OperatingSystem.IsWindows(), "POSIX filter script with a shebang");

        var marker = Path.Combine(NewDir("utf8marker"), "fired");
        var repo = NewRepo();
        Directory.CreateDirectory(Path.Combine(repo, "tools"));
        var script = Path.Combine(repo, "tools", "f");
        File.WriteAllText(script, $"#!/bin/sh\nprintf fired > '{marker}'\ncat\n");
        File.SetUnixFileMode(script, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);

        // Raw bytes on both sides: the config subsection and the .gitattributes selector must carry the
        // same 0xff, and neither can be written through a UTF-8 string without becoming U+FFFD.
        static byte[] WithFF(string before, string after) =>
            [.. System.Text.Encoding.ASCII.GetBytes(before), 0xff,
             .. System.Text.Encoding.ASCII.GetBytes(after)];

        File.WriteAllBytes(Path.Combine(repo, ".gitattributes"), WithFF("zz.txt filter=ev", "il\n"));
        File.WriteAllText(Path.Combine(repo, "zz.txt"), "payload\n");   // sorts after tools/f — see above
        Git(repo, "add", "-A");
        Git(repo, "commit", "-q", "-m", "branch selects a driver named with a raw 0xff");

        var config = Path.Combine(repo, ".git", "config");
        var appended = new List<byte>(File.ReadAllBytes(config));
        appended.AddRange(WithFF("[filter \"ev", "il\"]\n\tsmudge = ./tools/f\n"));
        File.WriteAllBytes(config, [.. appended]);

        // CONTROL — plain git honours the 0xff-named driver and runs branch code.
        Git(repo, "worktree", "add", "-q", Path.Combine(NewDir("ctl"), "wt"),
            "-b", "ctl-" + Guid.NewGuid().ToString("N")[..8]);
        await Assert.That(File.Exists(marker))
            .IsTrue()
            .Because("the control must reproduce filter execution, or the assertion below is vacuous");

        File.Delete(marker);

        await Assert.ThrowsAsync<BranchFilterInventoryException>(async () =>
            await WorktreeManager.BranchFilterOverridesAsync(repo));

        await Assert.That(File.Exists(marker)).IsFalse();
    }

    /// <summary>
    /// The refusal alone, with no filter execution — so it runs on WINDOWS too, which the end-to-end test
    /// above cannot (it needs a POSIX shebang). That matters specifically here: the guard detects a
    /// non-round-trippable name by the U+FFFD a UTF-8 decoder emits, and Windows is exactly where
    /// redirected output would otherwise decode with a console codepage that turns 0xff into an ordinary
    /// character — no U+FFFD, no refusal, and an override naming the wrong driver. This is the coverage for
    /// `StandardOutputEncoding` being pinned rather than inherited.
    /// </summary>
    [Test]
    public async Task A_non_round_trippable_driver_name_is_refused_on_every_platform() {
        var repo = NewRepo();

        // Written as BYTES: a process argument cannot carry 0xff — .NET would encode it to valid UTF-8.
        var config = Path.Combine(repo, ".git", "config");
        var bytes = new List<byte>(File.ReadAllBytes(config));
        bytes.AddRange([.. "[filter \"ev"u8, 0xff, .. "il\"]\n\tsmudge = ./tools/f\n"u8]);
        File.WriteAllBytes(config, [.. bytes]);

        // Precondition via the REGEX-FREE listing. `EffectiveDriverNames` uses `--get-regexp`, which is
        // blind to exactly this key — that blindness is the bug the enumeration change fixed, so using it
        // here would fail the precondition rather than test the refusal. (It did, first time round.)
        var psi = new ProcessStartInfo("git") {
            WorkingDirectory = repo, RedirectStandardOutput = true, RedirectStandardError = true,
            StandardOutputEncoding = new System.Text.UTF8Encoding(false, false)
        };
        foreach (var a in new[] { "config", "--list", "--name-only" }) psi.ArgumentList.Add(a);
        using var listing = Process.Start(psi)!;
        var keys = listing.StandardOutput.ReadToEnd();
        listing.WaitForExit();
        await Assert.That(keys).Contains("filter.")
            .Because("git must report the key, or there is nothing for the guard to refuse");

        await Assert.ThrowsAsync<BranchFilterInventoryException>(async () =>
            await WorktreeManager.BranchFilterOverridesAsync(repo));
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
