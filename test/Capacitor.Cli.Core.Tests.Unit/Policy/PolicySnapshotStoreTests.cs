namespace Capacitor.Cli.Core.Tests.Unit.Policy;

using Capacitor.Cli.Core.Policy;

public class PolicySnapshotStoreTests {
    [TempDir] public required TempDir Tmp { get; init; }
    [TempConfigRoot] public required TempConfigRoot Config { get; init; }

    const string ValidDoc = "version: 1\nrules:\n  - match: { kind: shell, command: \"rm -rf*\" }\n    outcome: deny\n";

    [Test]
    public async Task Save_then_load_round_trips_and_rebinds_documents() {
        var repo = Tmp.CreateDir("repo");
        Tmp.CreateDir("repo/.kcap");
        Tmp.CreateFile("repo/.kcap/approvals.yaml", ValidDoc);
        var store = new PolicySnapshotStore(Config.Root);
        var built = PolicySnapshotBuilder.Build(repo, Config.Root);
        store.Save("abc123", built);
        var loaded = store.TryLoad("abc123")!;
        await Assert.That(loaded.Id).IsEqualTo(built.Id);
        await Assert.That(loaded.Documents[0].Document.Rules.Count).IsEqualTo(1);
    }

    [Test]
    public async Task LoadOrBuild_is_sticky_against_later_file_edits() {
        var repo = Tmp.CreateDir("repo");
        Tmp.CreateDir("repo/.kcap");
        Tmp.CreateFile("repo/.kcap/approvals.yaml", ValidDoc);
        var store = new PolicySnapshotStore(Config.Root);
        var first = store.LoadOrBuild("s1", repo);
        File.Delete(Path.Combine(repo, ".kcap", "approvals.yaml"));
        var second = store.LoadOrBuild("s1", repo);
        await Assert.That(second.Id).IsEqualTo(first.Id);
        await Assert.That(second.Documents.Count).IsEqualTo(1);
    }

    [Test]
    public async Task Corrupt_persisted_snapshot_falls_back_to_rebuild() {
        var store = new PolicySnapshotStore(Config.Root);
        Directory.CreateDirectory(Config.Root.Path("policy", "sessions"));
        File.WriteAllText(Config.Root.Path("policy", "sessions", "bad.json"), "{not json");
        var snap = store.LoadOrBuild("bad", repoRoot: null);
        await Assert.That(snap.IsEmpty).IsTrue();
    }
}
