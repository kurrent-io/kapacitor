using Capacitor.Cli.Daemon.Services;

namespace Capacitor.Cli.Tests.Unit;

/// <summary>The pure decision rules behind the standalone snapshot copy, tested without a filesystem.
///
/// <para>These are the two places the copy can be wrong without any I/O being involved, so they get direct
/// coverage rather than being inferred from end-to-end behaviour.</para></summary>
public class SnapshotPathRulesTests {
    [Test]
    [Arguments(".git"), Arguments(".GIT"), Arguments(".Git")]
    public async Task Git_entry_names_match_case_insensitively(string name) =>
        await Assert.That(WorktreeManager.IsGitEntryName(name)).IsTrue();

    [Test]
    [Arguments(".gitignore"), Arguments(".gitmodules"), Arguments("git"), Arguments("")]
    public async Task Non_git_entry_names_do_not_match(string name) =>
        await Assert.That(WorktreeManager.IsGitEntryName(name)).IsFalse();

    [Test]
    [Arguments("", "releases/v2")]
    [Arguments("", "./releases/v2")]
    [Arguments("a", "../b/file")]
    [Arguments("a/b", "../../c")]
    [Arguments("a/b", "c/../d")]
    public async Task Targets_that_never_rise_above_the_root_are_admissible(string dir, string target) =>
        await Assert.That(WorktreeManager.IsAdmissibleLinkTarget(dir, target)).IsTrue();

    /// <summary>The relocation bug. Each of these RESOLVES inside the source, so a final-resolution rule
    /// admits it — but recreated verbatim at the snapshot's own depth the same raw target lands beside the
    /// snapshot instead, in a sibling agent's worktree.</summary>
    [Test]
    [Arguments("", "../proj/secret")]
    [Arguments("a", "../../a/b")]
    [Arguments("a/b", "../../../a/b/c")]
    public async Task Targets_that_escape_and_reenter_are_rejected(string dir, string target) =>
        await Assert.That(WorktreeManager.IsAdmissibleLinkTarget(dir, target)).IsFalse();

    [Test]
    [Arguments("", "../outside")]
    [Arguments("a", "../../outside")]
    [Arguments("a/b", "../../../outside")]
    public async Task Targets_that_escape_are_rejected(string dir, string target) =>
        await Assert.That(WorktreeManager.IsAdmissibleLinkTarget(dir, target)).IsFalse();

    /// <summary>Every rooted form, not just the fully-qualified one: on Windows <c>\foo</c> and
    /// <c>C:foo</c> are rooted-but-not-fully-qualified and would otherwise reach the depth walk as though
    /// they were relative.</summary>
    [Test]
    [Arguments("/etc/passwd")]
    [Arguments("\\foo")]
    [Arguments("C:foo")]
    [Arguments("C:\\foo")]
    public async Task Rooted_targets_are_rejected(string target) =>
        await Assert.That(WorktreeManager.IsAdmissibleLinkTarget("", target)).IsFalse();

    [Test]
    public async Task Empty_target_is_rejected() =>
        await Assert.That(WorktreeManager.IsAdmissibleLinkTarget("", "")).IsFalse();

    /// <summary>Backslash-separated components are split too. A raw target is authored by whoever wrote the
    /// source tree, so parsing it with one hardcoded separator would let an escape through on the
    /// filesystem where that separator is the live one.</summary>
    [Test]
    public async Task Backslash_separated_escapes_are_rejected() =>
        await Assert.That(WorktreeManager.IsAdmissibleLinkTarget("a", "..\\..\\outside")).IsFalse();
}
