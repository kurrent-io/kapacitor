namespace Capacitor.Cli.Tests.Unit;

/// <summary>
/// Pins that a worktree or clone destination is read the way git reads it — relative to the source
/// repository. The returned repository resolves its own working directory against the test process,
/// so an unresolved relative destination would come back pointing somewhere else entirely.
///
/// <para>The relative paths here stay under the source repository so its dispose still removes them;
/// a destination that escaped it would leak whatever these create.</para>
/// </summary>
public class GitRepoDestinationTests {
    [Test]
    public async Task A_relative_worktree_destination_comes_back_usable() {
        using var repo = GitRepo.CreateWithCommit();

        var worktree = repo.AddWorktree(Path.Combine("nested", "linked"), "side");

        await Assert.That(worktree.Path).IsEqualTo(repo.PathTo("nested", "linked"));
        await Assert.That(worktree.CurrentBranch).IsEqualTo("side");
    }

    [Test]
    public async Task A_relative_clone_destination_comes_back_usable() {
        using var repo = GitRepo.CreateWithCommit();

        var clone = repo.Clone(Path.Combine("nested", "cloned"));

        await Assert.That(clone.Path).IsEqualTo(repo.PathTo("nested", "cloned"));
        await Assert.That(clone.Head).IsEqualTo(repo.Head);
    }
}
