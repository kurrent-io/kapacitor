using Capacitor.Cli.Commands;

namespace Capacitor.Cli.Tests.Unit.Commands;

public class ImportScopeFilterTests {
    static (string SessionId, string FilePath, string EncodedCwd) T(string id) =>
        (id, $"/tmp/{id}.jsonl", $"-tmp-proj-{id}");

    static Func<(string SessionId, string FilePath, string EncodedCwd), CancellationToken, ValueTask<(string? Owner, string? Name)>>
        Resolver(Dictionary<string, (string Owner, string Name)?> map) =>
        (t, _) => new ValueTask<(string?, string?)>(
            map.TryGetValue(t.SessionId, out var v) && v is { } x ? (x.Owner, x.Name) : (null, null));

    [Test]
    public async Task Apply_All_returns_every_transcript_including_unresolved() {
        var transcripts = new[] { T("a"), T("b"), T("c") };
        var resolver = Resolver(new() { ["a"] = ("EventStore", "kcap"), ["b"] = null });

        var kept = await ImportScopeFilter.Apply(transcripts, new ImportScope.All(), resolver);

        await Assert.That(kept.Select(x => x.SessionId).ToArray()).IsEquivalentTo(["a", "b", "c"]);
    }

    [Test]
    public async Task Apply_Org_keeps_only_matching_owner() {
        var transcripts = new[] { T("a"), T("b"), T("c") };
        var resolver = Resolver(new() {
            ["a"] = ("EventStore", "kcap"),
            ["b"] = ("kurrent-io", "secret"),
            ["c"] = ("EventStore", "kurrentdb"),
        });

        var kept = await ImportScopeFilter.Apply(transcripts, new ImportScope.Org("EventStore"), resolver);

        await Assert.That(kept.Select(x => x.SessionId).ToArray()).IsEquivalentTo(["a", "c"]);
    }

    [Test]
    public async Task Apply_Org_matches_case_insensitively() {
        var transcripts = new[] { T("a") };
        var resolver = Resolver(new() { ["a"] = ("eventstore", "kcap") });

        var kept = await ImportScopeFilter.Apply(transcripts, new ImportScope.Org("EventStore"), resolver);

        await Assert.That(kept).Count().IsEqualTo(1);
    }

    [Test]
    public async Task Apply_Org_drops_unresolved_repos() {
        var transcripts = new[] { T("a") };
        var resolver = Resolver(new() { ["a"] = null });

        var kept = await ImportScopeFilter.Apply(transcripts, new ImportScope.Org("EventStore"), resolver);

        await Assert.That(kept).IsEmpty();
    }

    [Test]
    public async Task Apply_Repo_keeps_only_exact_match() {
        var transcripts = new[] { T("a"), T("b"), T("c") };
        var resolver = Resolver(new() {
            ["a"] = ("EventStore", "kcap"),
            ["b"] = ("EventStore", "kurrentdb"),
            ["c"] = ("EventStore", "kcap"),
        });

        var kept = await ImportScopeFilter.Apply(
            transcripts, new ImportScope.Repo("EventStore", "kcap"), resolver);

        await Assert.That(kept.Select(x => x.SessionId).ToArray()).IsEquivalentTo(["a", "c"]);
    }

    [Test]
    public async Task Repo_scope_keeps_every_named_repo() {
        var transcripts = new[] { T("a"), T("b"), T("c"), T("d") };
        var resolver = Resolver(new() {
            ["a"] = ("EventStore", "kcap"),
            ["b"] = ("EventStore", "kurrentdb"),
            ["c"] = ("Acme", "widgets"),
            ["d"] = ("EventStore", "gaffer"),
        });

        var kept = await ImportScopeFilter.Apply(
            transcripts,
            new ImportScope.Repo([("EventStore", "kcap"), ("Acme", "widgets")]),
            resolver);

        await Assert.That(kept.Select(x => x.SessionId).ToArray()).IsEquivalentTo(["a", "c"]);
    }

    [Test]
    public async Task Repo_scope_matches_each_repo_case_insensitively() {
        var transcripts = new[] { T("a"), T("b") };
        var resolver = Resolver(new() {
            ["a"] = ("EventStore", "kcap"),
            ["b"] = ("Acme", "Widgets"),
        });

        var kept = await ImportScopeFilter.Apply(
            transcripts,
            new ImportScope.Repo([("eventstore", "KCAP"), ("ACME", "widgets")]),
            resolver);

        await Assert.That(kept.Select(x => x.SessionId).ToArray()).IsEquivalentTo(["a", "b"]);
    }

    [Test]
    public async Task Repo_scopes_naming_the_same_set_are_equal_however_written() {
        // A record over a list compares by reference, so this is not free — and the duplicate case is
        // what makes an Except-based Equals and an XOR hash disagree unless the set is canonical.
        var a = new ImportScope.Repo([("EventStore", "kcap"), ("Acme", "widgets")]);
        var b = new ImportScope.Repo([("acme", "WIDGETS"), ("eventstore", "KCAP")]);
        var c = new ImportScope.Repo([("EventStore", "kcap"), ("EventStore", "kcap"), ("Acme", "widgets")]);

        await Assert.That(a).IsEqualTo(b);
        await Assert.That(a).IsEqualTo(c);
        await Assert.That(a.GetHashCode()).IsEqualTo(b.GetHashCode());
        await Assert.That(a.GetHashCode())
                    .IsEqualTo(c.GetHashCode())
                    .Because("equal instances must hash equally, which only holds because the set is "
                           + "stored deduped");
        await Assert.That(c.Repos).Count().IsEqualTo(2);
    }

    [Test]
    public async Task An_empty_repo_set_is_refused_rather_than_guessed_at() {
        // It would have to mean "everything" or "nothing", and a scope that silently picks one when the
        // caller meant the other is worse than a construction that fails.
        await Assert.That(() => new ImportScope.Repo([])).Throws<ArgumentException>();
    }
}
