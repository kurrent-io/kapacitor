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
        Config.CreateFile("approvals.yaml", ValidDoc);
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
        Config.CreateFile("approvals.yaml", ValidDoc);
        var snap = PolicySnapshotBuilder.Build(repo, Config.Root);
        await Assert.That(snap.Documents.Count).IsEqualTo(1);
        await Assert.That(snap.Documents[0].Scope).IsEqualTo(PolicyScope.User);      // user doc survives
        await Assert.That(snap.Degraded).IsTrue();
        await Assert.That(snap.Degradations.Count).IsEqualTo(1);
        await Assert.That(snap.Degradations[0]).Contains("repo");
        await Assert.That(snap.Degradations[0]).Contains("approvals.yaml");
    }

    [Test]
    public async Task Both_files_malformed_yields_no_documents_but_is_not_empty() {
        var repo = Tmp.CreateDir("repo");
        Tmp.CreateDir("repo/.kcap");
        Tmp.CreateFile("repo/.kcap/approvals.yaml", "version: 1\ncaps: { narrower_widening: off }\n");
        Config.CreateFile("approvals.yaml", "version: 2\n");
        var snap = PolicySnapshotBuilder.Build(repo, Config.Root);
        await Assert.That(snap.Documents.Count).IsEqualTo(0);
        await Assert.That(snap.Degradations.Count).IsEqualTo(2);
        await Assert.That(snap.Degraded).IsTrue();
        await Assert.That(snap.IsEmpty).IsFalse();
    }

    [Test]
    public async Task Oversized_file_is_ignored_with_a_degradation() {
        var repo = Tmp.CreateDir("repo");
        Tmp.CreateDir("repo/.kcap");
        Tmp.CreateFile("repo/.kcap/approvals.yaml", new string('a', 1024 * 1024 + 1));
        var snap = PolicySnapshotBuilder.Build(repo, Config.Root);
        await Assert.That(snap.Documents.Count).IsEqualTo(0);
        await Assert.That(snap.Degraded).IsTrue();
        await Assert.That(snap.Degradations[0]).Contains("exceeds 1 MB");
    }

    [Test]
    public async Task Snapshot_id_is_content_stable_and_content_sensitive() {
        var repo = Tmp.CreateDir("repo");
        Tmp.CreateDir("repo/.kcap");
        Tmp.CreateFile("repo/.kcap/approvals.yaml", ValidDoc);
        var a = PolicySnapshotBuilder.Build(repo, Config.Root);
        var b = PolicySnapshotBuilder.Build(repo, Config.Root);
        await Assert.That(a.Id).IsEqualTo(b.Id);
        Tmp.CreateFile("repo/.kcap/approvals.yaml", ValidDoc + "  - match: { kind: shell }\n    outcome: ask\n");
        var c = PolicySnapshotBuilder.Build(repo, Config.Root);
        await Assert.That(c.Id).IsNotEqualTo(a.Id);
    }

    [Test]
    public async Task Snapshot_id_differs_on_scope_swap() {
        const string DocX = ValidDoc;
        const string DocY = "version: 1\n";

        var repoX = Tmp.CreateDir("repo-x");
        Tmp.CreateDir("repo-x/.kcap");
        Tmp.CreateFile("repo-x/.kcap/approvals.yaml", DocX);
        Config.CreateFile("approvals.yaml", DocY);
        var xy = PolicySnapshotBuilder.Build(repoX, Config.Root);

        var repoY = Tmp.CreateDir("repo-y");
        Tmp.CreateDir("repo-y/.kcap");
        Tmp.CreateFile("repo-y/.kcap/approvals.yaml", DocY);
        Config.CreateFile("approvals.yaml", DocX);
        var yx = PolicySnapshotBuilder.Build(repoY, Config.Root);

        await Assert.That(xy.Id).IsNotEqualTo(yx.Id);
    }

    // The raw (repo-content + user-content) bytes are identical in both builds below — only
    // the length prefix ComputeId writes before each token can distinguish where one document
    // ends and the next begins.
    [Test]
    public async Task Snapshot_id_resists_a_boundary_shift_between_documents() {
        const string Doc = "version: 1\n";
        const string Pad = "#pad\n";

        var repoA = Tmp.CreateDir("repo-a");
        Tmp.CreateDir("repo-a/.kcap");
        Tmp.CreateFile("repo-a/.kcap/approvals.yaml", Doc + Pad);
        Config.CreateFile("approvals.yaml", Doc);
        var a = PolicySnapshotBuilder.Build(repoA, Config.Root);

        var repoB = Tmp.CreateDir("repo-b");
        Tmp.CreateDir("repo-b/.kcap");
        Tmp.CreateFile("repo-b/.kcap/approvals.yaml", Doc);
        Config.CreateFile("approvals.yaml", Pad + Doc);
        var b = PolicySnapshotBuilder.Build(repoB, Config.Root);

        await Assert.That(a.Documents.Count).IsEqualTo(2);
        await Assert.That(b.Documents.Count).IsEqualTo(2);
        await Assert.That(a.Id).IsNotEqualTo(b.Id);
    }

    [Test]
    public async Task Null_repo_root_reads_only_the_user_scope() {
        Config.CreateFile("approvals.yaml", ValidDoc);
        var snap = PolicySnapshotBuilder.Build(repoRoot: null, Config.Root);
        await Assert.That(snap.Documents.Count).IsEqualTo(1);
        await Assert.That(snap.Documents[0].Scope).IsEqualTo(PolicyScope.User);
    }
}
