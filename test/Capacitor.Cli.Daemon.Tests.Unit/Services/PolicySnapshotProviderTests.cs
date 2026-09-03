namespace Capacitor.Cli.Daemon.Tests.Unit.Services;

using Capacitor.Cli.Core.Policy;
using Capacitor.Cli.Daemon.Services;

public class PolicySnapshotProviderTests {
    [TempDir] public required TempDir Tmp { get; init; }
    [TempConfigRoot] public required TempConfigRoot Config { get; init; }

    [Test]
    public async Task Builds_from_the_worktree_repo_file_and_daemon_user_file() {
        var repo = Tmp.CreateDir("wt");
        Tmp.CreateDir("wt/.kcap");
        Tmp.CreateFile("wt/.kcap/approvals.yaml",
            "version: 1\nrules:\n  - match: { kind: shell, command: \"rm -rf*\" }\n    outcome: deny\n");
        var snap = new PolicySnapshotProvider(Config.Root).BuildFor(repo);
        await Assert.That(snap.IsEmpty).IsFalse();
        await Assert.That(snap.Documents[0].Scope).IsEqualTo(PolicyScope.Repo);
    }
}
