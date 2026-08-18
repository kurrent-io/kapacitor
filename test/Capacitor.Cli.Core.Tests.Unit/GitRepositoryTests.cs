namespace Capacitor.Cli.Core.Tests.Unit;

public class GitRepositoryTests {
    [Test]
    public async Task FindRoot_returns_null_for_directory_with_no_git_entry_anywhere() {
        using var tmp = new TempDir();
        var nested = tmp.PathTo("a", "b", "c");
        Directory.CreateDirectory(nested);

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
        var nested = tmp.PathTo("a", "b", "c");
        Directory.CreateDirectory(nested);

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
}
