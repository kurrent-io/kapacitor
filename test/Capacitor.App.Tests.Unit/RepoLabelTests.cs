using Capacitor.App.ViewModels;

namespace Capacitor.App.Tests.Unit;

public class RepoLabelTests {
    /// The wire says which path is a worktree; the leaf never guesses from a path's shape, so a
    /// worktree path yields the worktree's own name and only null yields the dash.
    [Test]
    public async Task The_leaf_is_the_last_segment_and_never_a_guess_from_the_path_shape() {
        await Assert.That(RepoLabel.Leaf("/repo/myproj")).IsEqualTo("myproj");
        await Assert.That(RepoLabel.Leaf("/repo/myproj/")).IsEqualTo("myproj");
        await Assert.That(RepoLabel.Leaf("/repo/myproj/.claude/worktrees/feature")).IsEqualTo("feature");
        await Assert.That(RepoLabel.Leaf("/repo/myproj/.capacitor/worktrees/agent-6da2")).IsEqualTo("agent-6da2");
        await Assert.That(RepoLabel.Leaf(null)).IsEqualTo("—");
    }
}
