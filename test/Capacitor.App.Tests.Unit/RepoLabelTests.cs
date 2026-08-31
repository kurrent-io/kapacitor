using Capacitor.App.ViewModels;

namespace Capacitor.App.Tests.Unit;

public class RepoLabelTests {
    /// Pins the repository leaf behind both worktree layouts an agent can run in — Claude's
    /// `.claude/worktrees/…` and the daemon's `.capacitor/worktrees/agent-…` — and the
    /// plain cases around them.
    [Test]
    public async Task The_leaf_is_the_repository_behind_either_worktree_layout() {
        await Assert.That(RepoLabel.Leaf("/repo/myproj/.claude/worktrees/feature")).IsEqualTo("myproj");
        await Assert.That(RepoLabel.Leaf("/repo/myproj/.capacitor/worktrees/agent-6da2")).IsEqualTo("myproj");
        await Assert.That(RepoLabel.Leaf("/repo/myproj")).IsEqualTo("myproj");
        await Assert.That(RepoLabel.Leaf("/repo/myproj/")).IsEqualTo("myproj");
        await Assert.That(RepoLabel.Leaf(null)).IsEqualTo("—");
    }
}
