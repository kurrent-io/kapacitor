namespace Capacitor.Cli.Core.Tests.Unit.Policy;

using Capacitor.Cli.Core.Policy;

public class PolicySnapshotBuilderTests {
    [TempDir] public required TempDir Tmp { get; init; }
    [TempConfigRoot] public required TempConfigRoot Config { get; init; }

    const string ValidDoc = "version: 1\nrules:\n  - match: { kind: shell, command: \"rm -rf*\" }\n    outcome: deny\n";

    [Test]
    public async Task Builds_from_repo_and_user_files() {
        var repo = Tmp.CreateDir("repo");
        Tmp.CreateDir("repo/.kcap");
        Tmp.CreateFile("repo/.kcap/approvals.yaml", ValidDoc);
        File.WriteAllText(Config.Root.Path("approvals.yaml"), ValidDoc);
        var snap = PolicySnapshotBuilder.Build(repo, Config.Root);
        await Assert.That(snap.Documents.Count).IsEqualTo(2);
        await Assert.That(snap.Documents[0].Scope).IsEqualTo(PolicyScope.Repo);
        await Assert.That(snap.Documents[1].Scope).IsEqualTo(PolicyScope.User);
        await Assert.That(snap.Degraded).IsFalse();
        await Assert.That(snap.IsEmpty).IsFalse();
    }

    [Test]
    public async Task No_files_yields_the_empty_snapshot() {
        var snap = PolicySnapshotBuilder.Build(Tmp.CreateDir("repo"), Config.Root);
        await Assert.That(snap.IsEmpty).IsTrue();
    }

    [Test]
    public async Task Malformed_file_is_ignored_loudly_never_silently() {
        var repo = Tmp.CreateDir("repo");
        Tmp.CreateDir("repo/.kcap");
        Tmp.CreateFile("repo/.kcap/approvals.yaml", "version: 1\ncaps: { narrower_widening: off }\n");
        File.WriteAllText(Config.Root.Path("approvals.yaml"), ValidDoc);
        var snap = PolicySnapshotBuilder.Build(repo, Config.Root);
        await Assert.That(snap.Documents.Count).IsEqualTo(1);      // user doc survives
        await Assert.That(snap.Degraded).IsTrue();
        await Assert.That(snap.Degradations.Count).IsEqualTo(1);
        await Assert.That(snap.Degradations[0]).Contains("approvals.yaml");
    }

    [Test]
    public async Task Snapshot_id_is_content_stable_and_content_sensitive() {
        var repo = Tmp.CreateDir("repo");
        Tmp.CreateDir("repo/.kcap");
        Tmp.CreateFile("repo/.kcap/approvals.yaml", ValidDoc);
        var a = PolicySnapshotBuilder.Build(repo, Config.Root);
        var b = PolicySnapshotBuilder.Build(repo, Config.Root);
        await Assert.That(a.Id).IsEqualTo(b.Id);
        File.WriteAllText(Path.Combine(repo, ".kcap", "approvals.yaml"), ValidDoc + "  - match: { kind: shell }\n    outcome: ask\n");
        var c = PolicySnapshotBuilder.Build(repo, Config.Root);
        await Assert.That(c.Id).IsNotEqualTo(a.Id);
    }

    [Test]
    public async Task Null_repo_root_reads_only_the_user_scope() {
        File.WriteAllText(Config.Root.Path("approvals.yaml"), ValidDoc);
        var snap = PolicySnapshotBuilder.Build(repoRoot: null, Config.Root);
        await Assert.That(snap.Documents.Count).IsEqualTo(1);
        await Assert.That(snap.Documents[0].Scope).IsEqualTo(PolicyScope.User);
    }
}
