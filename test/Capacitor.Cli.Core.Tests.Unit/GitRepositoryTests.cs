namespace Capacitor.Cli.Core.Tests.Unit;

public class GitRepositoryTests {
    [Test]
    public async Task FindRoot_returns_null_for_directory_with_no_git_entry_anywhere() {
        using var tmp = new TempDir();
        var nested = tmp.CreateDir("a", "b", "c");

        await Assert.That(GitRepository.FindRoot(nested)).IsNull();
    }

    [Test]
    public async Task FindRoot_returns_directory_when_dot_git_directory_is_present() {
        using var tmp = new TempDir();
        tmp.CreateDir(".git");

        await Assert.That(GitRepository.FindRoot(tmp.Path)).IsEqualTo(tmp.Path);
    }

    [Test]
    public async Task FindRoot_returns_directory_when_dot_git_is_a_file_as_in_worktrees_or_submodules() {
        using var tmp = new TempDir();
        tmp.CreateFile(".git", "gitdir: ../parent/.git/worktrees/x\n");

        await Assert.That(GitRepository.FindRoot(tmp.Path)).IsEqualTo(tmp.Path);
    }

    [Test]
    public async Task FindRoot_walks_up_and_returns_the_ancestor_holding_the_dot_git_entry() {
        using var tmp = new TempDir();
        tmp.CreateDir(".git");
        var nested = tmp.CreateDir("a", "b", "c");

        await Assert.That(GitRepository.FindRoot(nested)).IsEqualTo(tmp.Path);
    }

    [Test]
    public async Task FindRoot_returns_null_for_empty_input() {
        await Assert.That(GitRepository.FindRoot("")).IsNull();
    }

    [Test]
    public async Task IsInsideRepo_matches_FindRoot_result() {
        using var tmp = new TempDir();
        await Assert.That(GitRepository.IsInsideRepo(tmp.Path)).IsFalse();

        tmp.CreateDir(".git");

        await Assert.That(GitRepository.IsInsideRepo(tmp.Path)).IsTrue();
    }

    // ── ResolveMainRepoRoot ──────────────────────────────────────────────────

    [Test]
    public async Task Resolve_returns_the_main_repo_for_a_linked_worktree_with_an_absolute_gitdir() {
        using var tmp = new TempDir();
        var main = tmp.CreateDir("main");
        tmp.CreateDir("main", ".git", "worktrees", "wt1");
        var wt = tmp.CreateDir("wt");
        File.WriteAllText(Path.Combine(wt, ".git"), $"gitdir: {Path.Combine(main, ".git", "worktrees", "wt1")}\n");

        await Assert.That(GitRepository.ResolveMainRepoRoot(wt)).IsEqualTo(main);
    }

    [Test]
    public async Task Resolve_returns_the_main_repo_for_a_linked_worktree_with_a_relative_gitdir() {
        using var tmp = new TempDir();
        var main = tmp.CreateDir("main");
        tmp.CreateDir("main", ".git", "worktrees", "wt1");
        var wt = tmp.CreateDir("main", ".claude", "worktrees", "wt1");
        File.WriteAllText(Path.Combine(wt, ".git"), "gitdir: ../../../.git/worktrees/wt1\n");

        await Assert.That(GitRepository.ResolveMainRepoRoot(wt)).IsEqualTo(main);
    }

    /// A submodule's .git is also a file, but pointing into .git/modules — a submodule is a real
    /// repository of its own and must never collapse into the superproject.
    [Test]
    public async Task Resolve_leaves_a_submodule_checkout_alone() {
        using var tmp = new TempDir();
        tmp.CreateDir("super", ".git", "modules", "sub");
        var sub = tmp.CreateDir("super", "sub");
        File.WriteAllText(Path.Combine(sub, ".git"), "gitdir: ../.git/modules/sub\n");

        await Assert.That(GitRepository.ResolveMainRepoRoot(sub)).IsEqualTo(sub);
    }

    [Test]
    public async Task Resolve_leaves_a_normal_repository_alone() {
        using var tmp = new TempDir();
        tmp.CreateDir(".git");

        await Assert.That(GitRepository.ResolveMainRepoRoot(tmp.Path)).IsEqualTo(tmp.Path);
    }

    /// A stored worktree path whose directory is gone (the worktree was removed) can no longer be
    /// resolved through its .git file — the agent-infra path patterns are the fallback.
    [Test]
    public async Task Resolve_strips_the_agent_worktree_tail_from_a_path_that_no_longer_exists() {
        await Assert.That(GitRepository.ResolveMainRepoRoot("/gone/repo/.claude/worktrees/snappy-leaf"))
            .IsEqualTo("/gone/repo");
        await Assert.That(GitRepository.ResolveMainRepoRoot("/gone/repo/.capacitor/worktrees/wt-1"))
            .IsEqualTo("/gone/repo");
    }

    [Test]
    public async Task Resolve_leaves_a_missing_path_without_a_worktree_pattern_alone() {
        await Assert.That(GitRepository.ResolveMainRepoRoot("/gone/plain-repo"))
            .IsEqualTo("/gone/plain-repo");
    }

    [Test]
    public async Task Resolve_treats_an_unreadable_or_malformed_git_file_as_not_a_worktree() {
        using var tmp = new TempDir();
        tmp.CreateFile(".git", "this is not a gitdir line");

        await Assert.That(GitRepository.ResolveMainRepoRoot(tmp.Path)).IsEqualTo(tmp.Path);
    }
}
