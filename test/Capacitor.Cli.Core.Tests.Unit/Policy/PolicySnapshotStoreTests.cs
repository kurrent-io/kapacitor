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
        var policyFile = Tmp.CreateFile("repo/.kcap/approvals.yaml", ValidDoc);
        var store = new PolicySnapshotStore(Config.Root);
        var first = store.LoadOrBuild("s1", repo);
        File.Delete(policyFile);
        var second = store.LoadOrBuild("s1", repo);
        await Assert.That(second.Id).IsEqualTo(first.Id);
        await Assert.That(second.Documents.Count).IsEqualTo(1);
    }

    [Test]
    public async Task Corrupt_persisted_snapshot_rebuilds_with_a_degradation() {
        var store = new PolicySnapshotStore(Config.Root);
        var path = Config.CreateDir("policy", "sessions").CreateFile("bad.json", "{not json");
        var snap = store.LoadOrBuild("bad", repoRoot: null);
        await Assert.That(snap.Documents.Count).IsEqualTo(0);
        await Assert.That(snap.Degraded).IsTrue();
        await Assert.That(snap.Degradations.Any(d => d.Contains(path))).IsTrue();
        await Assert.That(snap.Degradations.Any(d => d.Contains("unloadable"))).IsTrue();
    }

    /// <summary>A snapshot that never reached disk leaves the session unfrozen — the next hook
    /// rebuilds from files that may have changed since — so the loss must reach the caller rather
    /// than hiding behind a clean-looking snapshot.</summary>
    [Test]
    public async Task An_unpersistable_snapshot_is_returned_degraded() {
        // A file where the sessions directory belongs: the save cannot create its parent.
        Config.CreateDir("policy").CreateFile("sessions", "");
        var snap = new PolicySnapshotStore(Config.Root).LoadOrBuild("s1", repoRoot: null);
        await Assert.That(snap.Degraded).IsTrue();
        await Assert.That(snap.Degradations.Any(d => d.Contains("could not be persisted"))).IsTrue();
        await Assert.That(snap.Degradations.Any(d => d.Contains("may not stay frozen"))).IsTrue();
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
