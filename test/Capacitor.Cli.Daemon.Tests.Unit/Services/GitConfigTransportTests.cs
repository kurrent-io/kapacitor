using Capacitor.Cli.Daemon.Services;

namespace Capacitor.Cli.Daemon.Tests.Unit.Services;

/// <summary>
/// Config overrides reach git as <c>GIT_CONFIG_COUNT</c> / <c>GIT_CONFIG_KEY_n</c> / <c>GIT_CONFIG_VALUE_n</c>
/// rather than <c>-c key=value</c>, so a key containing <c>=</c> keeps its boundary.
///
/// <para>The count is the load-bearing part: entries are APPENDED, because the environment may already carry
/// some — this daemon's own read-only suppressions, or an operator's. Write from index 0 and theirs are
/// displaced; leave the count short and git ignores the tail. Either way overrides go missing while the call
/// site believes they applied, which is the failure mode the <c>=</c> mis-encoding had. So git is the oracle
/// here, not the environment dictionary: these run a real <c>git config --list</c> through the production
/// runner and read back what git resolved.</para>
/// </summary>
public class GitConfigTransportTests {
    static readonly TimeSpan Timeout = TimeSpan.FromSeconds(60);

    /// <summary>`--list -z` records are `key\nvalue\0`; an empty value is `key\n`.</summary>
    static async Task<string[]> EffectiveConfigAsync(bool sourceReadOnly, params GitConfigOverride[] config) {
        var result = await WorktreeManager.RunGitCaptureResult(
            Path.GetTempPath(), Timeout, sourceReadOnly, config, "config", "--list", "-z");

        await Assert.That(result.ExitCode).IsEqualTo(0).Because(result.Stderr);

        return result.Stdout.Split('\0', StringSplitOptions.RemoveEmptyEntries);
    }

    /// <summary>
    /// The `=` this whole transport exists for: `-c filter.evil=x.smudge=` reaches git as key `filter.evil`
    /// with value `x.smudge=`, so the driver stays live. Here the key arrives whole, with the empty value
    /// that disables a filter command.
    ///
    /// <para>Platform-agnostic on purpose. The end-to-end containment test needs a POSIX shebang and cannot
    /// run on Windows, which is exactly where an empty value is least certain to survive an environment
    /// block — an override that arrived without its value would be a fatal `missing config value`, but one
    /// whose value arrived as absent-vs-empty is the silent case, and this is its coverage.</para>
    /// </summary>
    [Test]
    public async Task An_equals_in_a_key_reaches_git_as_part_of_the_key() {
        var records = await EffectiveConfigAsync(
            sourceReadOnly: false, new GitConfigOverride("filter.evil=x.smudge", ""));

        await Assert.That(records).Contains("filter.evil=x.smudge\n");
        // The `-c` spelling's damage, stated as an assertion: the key must not have been cut at the '='.
        await Assert.That(records.Any(static r => r.StartsWith("filter.evil\n", StringComparison.Ordinal)))
            .IsFalse();
    }

    /// <summary>
    /// A read-only invocation carries two suppressions of its own. Overrides passed alongside them must be
    /// appended and the count must cover all of them — a count that stops short leaves git ignoring the tail,
    /// which is a containment override silently dropped.
    /// </summary>
    [Test]
    public async Task Overrides_compose_with_the_source_read_only_entries() {
        var records = await EffectiveConfigAsync(
            sourceReadOnly: true,
            new GitConfigOverride("filter.evil=x.smudge", ""),
            new GitConfigOverride("filter.evil=x.required", "false"));

        // Every entry resolves, whichever end of the composed set it sits at.
        await Assert.That(records).Contains("maintenance.auto\nfalse");
        await Assert.That(records).Contains("core.fsmonitor\nfalse");
        await Assert.That(records).Contains("filter.evil=x.smudge\n");
        await Assert.That(records).Contains("filter.evil=x.required\nfalse");
    }

    /// <summary>
    /// An inherited <c>GIT_CONFIG_COUNT</c> is appended to, not overwritten. Writing from index 0 would
    /// replace the operator's own entries with ours while reporting a count that covers both — their config
    /// silently gone, ours apparently fine.
    /// </summary>
    [Test]
    [NotInParallel]
    public async Task An_inherited_count_is_appended_to_rather_than_overwritten() {
        var restore = (
            Count: Environment.GetEnvironmentVariable("GIT_CONFIG_COUNT"),
            Key: Environment.GetEnvironmentVariable("GIT_CONFIG_KEY_0"),
            Value: Environment.GetEnvironmentVariable("GIT_CONFIG_VALUE_0"));
        try {
            Environment.SetEnvironmentVariable("GIT_CONFIG_COUNT", "1");
            Environment.SetEnvironmentVariable("GIT_CONFIG_KEY_0", "kcap.inherited");
            Environment.SetEnvironmentVariable("GIT_CONFIG_VALUE_0", "yes");

            var records = await EffectiveConfigAsync(
                sourceReadOnly: false, new GitConfigOverride("filter.evil=x.smudge", ""));

            await Assert.That(records).Contains("kcap.inherited\nyes");
            await Assert.That(records).Contains("filter.evil=x.smudge\n");
        } finally {
            Environment.SetEnvironmentVariable("GIT_CONFIG_COUNT", restore.Count);
            Environment.SetEnvironmentVariable("GIT_CONFIG_KEY_0", restore.Key);
            Environment.SetEnvironmentVariable("GIT_CONFIG_VALUE_0", restore.Value);
        }
    }

    /// <summary>A count we cannot parse means we do not know which indices are taken. Refused rather than
    /// renumbered: git dies on such a value anyway, and overwriting it would silently consume whatever the
    /// operator meant to pass.</summary>
    [Test]
    [Arguments("not-a-number")]
    [Arguments("-1")]
    [Arguments(" 1")]
    [Arguments("1.0")]
    public void An_unparseable_inherited_count_is_refused(string count) {
        var environment = new Dictionary<string, string?> { ["GIT_CONFIG_COUNT"] = count };

        Assert.Throws<GitConfigTransportException>(() => WorktreeManager.ApplyConfigOverrides(
            environment, [new GitConfigOverride("filter.x.smudge", "")]));
    }

    /// <summary>An inherited count with no room left to append to would wrap the index arithmetic and name
    /// entries at negative indices. Refused with a reason rather than left to git's own rejection of the
    /// resulting count.</summary>
    [Test]
    public void An_inherited_count_with_no_room_to_append_is_refused() {
        var environment = new Dictionary<string, string?> {
            ["GIT_CONFIG_COUNT"] = int.MaxValue.ToString(System.Globalization.CultureInfo.InvariantCulture)
        };

        Assert.Throws<GitConfigTransportException>(() => WorktreeManager.ApplyConfigOverrides(
            environment, [new GitConfigOverride("filter.x.smudge", "")]));
    }

    /// <summary>An environment entry ends at its first NUL, so a NUL inside a key would hand git a PREFIX of
    /// the key we checked — the authorised name not being the used name. Unreachable from the filter
    /// inventory, whose records are NUL-separated, and refused here regardless.</summary>
    [Test]
    public void A_NUL_in_an_override_is_refused() {
        var environment = new Dictionary<string, string?>();

        Assert.Throws<GitConfigTransportException>(() => WorktreeManager.ApplyConfigOverrides(
            environment, [new GitConfigOverride("filter.ev\0il.smudge", "")]));
        Assert.Throws<GitConfigTransportException>(() => WorktreeManager.ApplyConfigOverrides(
            environment, [new GitConfigOverride("filter.evil.smudge", "ca\0t")]));
    }

    /// <summary>An empty override set leaves the environment alone — including an inherited count it has no
    /// entries to append to, which must not be renumbered or validated into a failure.</summary>
    [Test]
    public async Task An_empty_override_set_touches_nothing() {
        var environment = new Dictionary<string, string?> { ["GIT_CONFIG_COUNT"] = "bogus" };

        WorktreeManager.ApplyConfigOverrides(environment, []);

        await Assert.That(environment).Count().IsEqualTo(1);
        await Assert.That(environment["GIT_CONFIG_COUNT"]).IsEqualTo("bogus");
    }

    // ── the runtime proof ──

    /// <summary>
    /// The transport is proved by running git before any containment override is handed out. A git older than
    /// 2.31 IGNORES these variables: every filter and hook override would be dropped and every command would
    /// still succeed. This asserts the measurement passes here — it is the gate every worktree creation now
    /// goes through.
    ///
    /// <para>The UNCACHED probe on purpose: the cached gate short-circuits once any other test has created a
    /// worktree, so calling it here would assert nothing about git.</para>
    /// </summary>
    [Test]
    public async Task The_transport_is_proved_against_the_git_on_this_machine() {
        await WorktreeManager.ProbeConfigTransportAsync(Path.GetTempPath());
    }

    /// <summary>The proof's own predicate, against listings git could return. Without this the proof would be
    /// a positive control with nothing establishing it can fail: a verifier that accepts a listing missing
    /// the entry would pass on the very git it exists to reject.</summary>
    [Test]
    public void The_proof_rejects_a_listing_that_does_not_report_the_probe() {
        GitConfigOverride[] probe = [
            new("filter.probe=1.smudge", ""),
            new("filter.probe=1.required", "false")
        ];

        // Honoured: both records exactly as passed, alongside unrelated config.
        WorktreeManager.RequireProbeApplied(
            "user.name\nT\0filter.probe=1.smudge\n\0filter.probe=1.required\nfalse\0", probe);

        // Ignored entirely — the old-git case.
        Assert.Throws<GitConfigTransportException>(() =>
            WorktreeManager.RequireProbeApplied("user.name\nT\0", probe));

        // The tail dropped — the miscounted case.
        Assert.Throws<GitConfigTransportException>(() =>
            WorktreeManager.RequireProbeApplied("filter.probe=1.smudge\n\0", probe));

        // Present but not the value passed: an empty value that arrived as something else.
        Assert.Throws<GitConfigTransportException>(() =>
            WorktreeManager.RequireProbeApplied(
                "filter.probe=1.smudge\ncat\0filter.probe=1.required\nfalse\0", probe));

        // The key cut at its '=' — what the transport this replaces did.
        Assert.Throws<GitConfigTransportException>(() =>
            WorktreeManager.RequireProbeApplied("filter.probe\n1.smudge=\0", probe));
    }
}
