using Capacitor.Cli.Commands;

namespace Capacitor.Cli.Tests.Unit.Commands;

/// <summary>
/// The figures behind a scope choice: per-repo totals, what could not be attributed, and how much
/// falls inside each candidate <c>--since</c> window.
/// </summary>
public class ImportDiscoverySummaryTests {
    // Pinned rather than derived per call: recomputing "today" per assertion makes a run that
    // straddles UTC midnight compare two different dates.
    static readonly DateTimeOffset Now = new(2026, 6, 15, 12, 0, 0, TimeSpan.Zero);

    static readonly DateOnly?[] Windows = [Day(-30), Day(-90), null];

    static DateOnly Day(int offset) => DateOnly.FromDateTime(Now.UtcDateTime.AddDays(offset));
    static DateTimeOffset At(int daysAgo) => Now.AddDays(daysAgo);

    static ImportDiscoverySummary Build(
            (string Id, DateTimeOffset? At)[] sessions,
            Dictionary<string, (string Owner, string Name)?> repos,
            IReadOnlyList<DateOnly?>? windows = null) =>
        ImportDiscoverySummary.Build(
            sessions.Select(s => (s.Id, s.At)), repos, windows ?? Windows);

    [Test]
    public async Task Counts_sessions_per_repo_and_keeps_the_newest_start() {
        var s = Build(
            [("a", At(-1)), ("b", At(-50)), ("c", At(-3))],
            new() {
                ["a"] = ("EventStore", "kcap"),
                ["b"] = ("EventStore", "kcap"),
                ["c"] = ("Acme", "widgets"),
            });

        var kcap = s.Repos.Single(r => r.Name == "kcap");

        await Assert.That(kcap.SessionCount).IsEqualTo(2);
        await Assert.That(kcap.LastSessionAt!.Value.Date).IsEqualTo(At(-1).Date);
    }

    [Test]
    public async Task Repos_are_ordered_by_most_recent_first() {
        var s = Build(
            [("a", At(-40)), ("b", At(-2))],
            new() { ["a"] = ("Old", "repo"), ["b"] = ("Fresh", "repo") });

        await Assert.That(s.Repos[0].Owner)
                    .IsEqualTo("Fresh")
                    .Because("IsEquivalentTo is order-insensitive, so ordering has to be asserted "
                           + "positionally or the assertion cannot fail");
        await Assert.That(s.Repos[1].Owner).IsEqualTo("Old");
    }

    [Test]
    public async Task Sessions_with_no_repo_are_counted_separately() {
        // `--all` includes these and any repo selection silently drops them, so the number has to be
        // visible rather than inferred from a total that does not add up.
        var s = Build(
            [("a", At(-1)), ("b", At(-1)), ("c", At(-1))],
            new() { ["a"] = ("EventStore", "kcap"), ["b"] = null });

        await Assert.That(s.UnmatchedCount).IsEqualTo(2);
        await Assert.That(s.Repos).Count().IsEqualTo(1);
    }

    [Test]
    public async Task Each_window_counts_what_falls_inside_it() {
        var s = Build(
            [("a", At(-5)), ("b", At(-45)), ("c", At(-200))],
            new() { ["a"] = ("E", "r"), ["b"] = ("E", "r"), ["c"] = ("E", "r") });

        await Assert.That(s.ByWindow.Single(w => w.Since == Day(-30)).SessionCount).IsEqualTo(1);
        await Assert.That(s.ByWindow.Single(w => w.Since == Day(-90)).SessionCount).IsEqualTo(2);
        await Assert.That(s.ByWindow.Single(w => w.Since is null).SessionCount).IsEqualTo(3);
    }

    [Test]
    public async Task An_undated_session_counts_in_every_window() {
        // Every source keeps a candidate whose timestamp it could not determine rather than dropping
        // it, so a window that excluded it here would under-report the import it predicts.
        var s = Build([("a", null)], new() { ["a"] = ("E", "r") });

        await Assert.That(s.ByWindow.Single(w => w.Since is null).SessionCount).IsEqualTo(1);
        await Assert.That(s.ByWindow.Single(w => w.Since == Day(-30)).SessionCount).IsEqualTo(1);
    }

    [Test]
    public async Task The_windows_are_the_callers_so_they_cannot_drift_from_the_ones_offered() {
        var s = Build([("a", At(-10))], new() { ["a"] = ("E", "r") }, [Day(-7)]);

        await Assert.That(s.ByWindow).Count().IsEqualTo(1);
        await Assert.That(s.ByWindow[0].Since).IsEqualTo(Day(-7));
        await Assert.That(s.ByWindow[0].SessionCount).IsEqualTo(0);
    }

    [Test]
    public async Task Repo_totals_match_case_insensitively_the_way_scopes_do() {
        var s = Build(
            [("a", At(-1)), ("b", At(-1))],
            new() { ["a"] = ("EventStore", "kcap"), ["b"] = ("eventstore", "KCAP") });

        await Assert.That(s.Repos).Count().IsEqualTo(1);
        await Assert.That(s.Repos[0].SessionCount).IsEqualTo(2);
    }
}
