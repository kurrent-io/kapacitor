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
    public async Task Corrupt_persisted_snapshot_rebuilds_with_a_degradation() {
        var store = new PolicySnapshotStore(Config.Root);
        var path = Config.Root.Path("policy", "sessions", "bad.json");
        Directory.CreateDirectory(Config.Root.Path("policy", "sessions"));
        File.WriteAllText(path, "{not json");
        var snap = store.LoadOrBuild("bad", repoRoot: null);
        await Assert.That(snap.Documents.Count).IsEqualTo(0);
        await Assert.That(snap.Degraded).IsTrue();
        await Assert.That(snap.Degradations.Any(d => d.Contains(path))).IsTrue();
        await Assert.That(snap.Degradations.Any(d => d.Contains("unloadable"))).IsTrue();
    }

    [Test]
    public async Task Absent_persisted_snapshot_rebuilds_without_a_degradation() {
        var store = new PolicySnapshotStore(Config.Root);
        var snap = store.LoadOrBuild("nope", repoRoot: null);
        await Assert.That(snap.IsEmpty).IsTrue();
        await Assert.That(snap.Degraded).IsFalse();
    }

    [Test]
    [Arguments("a/b")]
    [Arguments("..")]
    [Arguments("")]
    public async Task Sanitize_rewrites_keys_outside_the_safe_charset(string sessionKey) {
        var sanitized = PolicySnapshotStore.Sanitize(sessionKey);
        await Assert.That(sanitized.Length).IsEqualTo(32);
        await Assert.That(sanitized.All(char.IsAsciiHexDigitLower)).IsTrue();
    }

    [Test]
    public async Task Sanitize_rewrites_an_overlong_key() {
        var sanitized = PolicySnapshotStore.Sanitize(new string('a', 65));
        await Assert.That(sanitized.Length).IsEqualTo(32);
        await Assert.That(sanitized.All(char.IsAsciiHexDigitLower)).IsTrue();
    }

    [Test]
    public async Task Sanitize_passes_through_a_plain_hex_key() {
        var key = new string('a', 32);
        await Assert.That(PolicySnapshotStore.Sanitize(key)).IsEqualTo(key);
    }
}
