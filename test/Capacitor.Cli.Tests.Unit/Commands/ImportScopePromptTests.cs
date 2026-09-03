using Capacitor.Cli.Commands;

namespace Capacitor.Cli.Tests.Unit.Commands;

public class ImportScopePromptTests {
    // --- BuildRepoChoices ---

    [Test]
    public async Task BuildRepoChoices_orders_current_first_then_alphabetical() {
        var choices = ImportScopePrompt.BuildRepoChoices(
            currentRepo: ("EventStore", "kcap"),
            discoveredRepos: [
                ("EventStore", "kurrentdb"),
                ("EventStore", "kcap"), // dup with current
                ("alexeyzimarev", "scratchpad"),
            ]
        );

        await Assert.That(choices)
            .IsEquivalentTo(
                [
                    "EventStore/kcap (current)",
                    "alexeyzimarev/scratchpad",
                    "EventStore/kurrentdb"
                ]
            );
    }

    [Test]
    public async Task BuildRepoChoices_no_current_repo_just_sorts_alphabetically() {
        var choices = ImportScopePrompt.BuildRepoChoices(
            currentRepo: null,
            discoveredRepos: [("Z-org", "z"), ("A-org", "a"), ("M-org", "m")]
        );

        await Assert.That(choices).IsEquivalentTo(["A-org/a", "M-org/m", "Z-org/z"]);
    }

    [Test]
    public async Task BuildRepoChoices_deduplicates_discovered_set() {
        var choices = ImportScopePrompt.BuildRepoChoices(
            currentRepo: null,
            discoveredRepos: [("A", "x"), ("A", "x"), ("A", "y")]
        );

        await Assert.That(choices).IsEquivalentTo(new[] { "A/x", "A/y" });
    }

    // --- BuildOrgChoices ---

    [Test]
    public async Task BuildOrgChoices_distinct_owners_sorted() {
        var owners = ImportScopePrompt.BuildOrgChoices(
            [
                ("EventStore", "kurrentdb"),
                ("EventStore", "kcap"),       // same owner, different repo
                ("alexeyzimarev", "scratchpad"),
            ]
        );

        await Assert.That(owners).IsEquivalentTo(new[] { "alexeyzimarev", "EventStore" });
    }

    [Test]
    public async Task BuildOrgChoices_deduplicates_case_insensitively() {
        var owners = ImportScopePrompt.BuildOrgChoices(
            [("EventStore", "a"), ("eventstore", "b")]
        );

        await Assert.That(owners.Length).IsEqualTo(1);
    }

    [Test]
    public async Task BuildOrgChoices_empty_when_no_repos() {
        var owners = ImportScopePrompt.BuildOrgChoices([]);
        await Assert.That(owners).IsEmpty();
    }

    // --- FormatSummary ---

    [Test]
    public async Task FormatSummary_All_scope() {
        var s = ImportScopePrompt.FormatSummary(
            scope: new ImportScope.All(),
            matchedCount: 47,
            repoSamples: ["EventStore/kcap", "EventStore/kurrentdb"],
            visibilityDescription: "org_public (from profile)"
        );

        await Assert.That(s).Contains("scope:   everything");
        await Assert.That(s).Contains("matched: 47 sessions");
        await Assert.That(s).Contains("visibility: org_public (from profile)");
    }

    /// <summary>Pins the label with the block rather than on its own: the label is what sat at column 0
    /// while its own rows were indented, so an indent that reaches the rows and not the label leaves
    /// exactly the raggedness the shift exists to remove.</summary>
    [Test]
    public async Task FormatSummary_indent_shifts_the_label_with_the_rows_under_it() {
        var s = ImportScopePrompt.FormatSummary(
            scope: new ImportScope.All(),
            matchedCount: 47,
            repoSamples: ["EventStore/kcap"],
            visibilityDescription: "org_public (from profile)",
            indent: "  "
        );

        await Assert.That(s.Split('\n')[0]).IsEqualTo("  About to import:");
        await Assert.That(s).Contains("    scope:   everything");
        await Assert.That(s).Contains("    matched: 47 sessions");
        await Assert.That(s).Contains("    visibility: org_public (from profile)");
    }

    [Test]
    public async Task FormatSummary_Org_includes_org_name() {
        var s = ImportScopePrompt.FormatSummary(
            scope: new ImportScope.Org("EventStore"),
            matchedCount: 5,
            repoSamples: ["EventStore/kcap"],
            visibilityDescription: "private (--private)"
        );

        await Assert.That(s).Contains("org repos only (EventStore)");
        await Assert.That(s).Contains("visibility: private (--private)");
    }

    [Test]
    public async Task FormatSummary_Repo_includes_owner_name() {
        var s = ImportScopePrompt.FormatSummary(
            scope: new ImportScope.Repo("EventStore", "kcap"),
            matchedCount: 3,
            repoSamples: ["EventStore/kcap"],
            visibilityDescription: "org_public (from profile)"
        );

        await Assert.That(s).Contains("repository EventStore/kcap");
    }

    [Test]
    public async Task FormatSummary_multi_repo_names_what_was_asked_for_not_just_a_count() {
        // The `repos:` line lists what MATCHED, so a repo that was requested and found nothing appears
        // nowhere else — and with --yes this text is the only record of the run.
        var s = ImportScopePrompt.FormatSummary(
            scope: new ImportScope.Repo([("EventStore", "kcap"), ("EventStore", "gaffer")]),
            matchedCount: 3,
            repoSamples: ["EventStore/kcap"],
            visibilityDescription: "org_public (from profile)"
        );

        await Assert.That(s).Contains("2 repositories");
        await Assert.That(s).Contains("EventStore/gaffer");
    }

    [Test]
    public async Task FormatSummary_caps_repo_samples_at_5() {
        var samples = Enumerable.Range(1, 9).Select(i => $"EventStore/r{i}").ToArray();

        var s = ImportScopePrompt.FormatSummary(
            scope: new ImportScope.All(),
            matchedCount: 50,
            repoSamples: samples,
            visibilityDescription: "org_public (from profile)"
        );

        await Assert.That(s).Contains("EventStore/r1");
        await Assert.That(s).Contains("EventStore/r5");
        await Assert.That(s).DoesNotContain("EventStore/r6");
        await Assert.That(s).Contains("+4 more");
    }
}
