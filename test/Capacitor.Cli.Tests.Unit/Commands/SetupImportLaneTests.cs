using Capacitor.Cli.Commands;
using Capacitor.Cli.Core.FirstRun;
using Capacitor.Cli.Core.Setup;
using Capacitor.Cli.Harness.Claude;

namespace Capacitor.Cli.Tests.Unit.Commands;

/// <summary>
/// Turning what discovery found into what the Import screen is told, and choosing which sources it
/// scanned in the first place. Both are places a figure can quietly stop matching the disk.
/// </summary>
public class SetupImportLaneTests {
    [TempConfigRoot] public required TempConfigRoot Config { get; init; }

    static ImportCommand.ImportDiscoveryResult Found(
            IEnumerable<ImportDiscoverySummary.RepoTotals> repos,
            IReadOnlyDictionary<string, int>?              unmatched = null,
            IReadOnlyList<string>?                        scanned   = null) =>
        new(new ImportDiscoverySummary(
                [.. repos],
                unmatched?.Values.Sum() ?? 0,
                [],
                unmatched ?? new Dictionary<string, int>()),
            scanned ?? []);

    static ImportDiscoverySummary.RepoTotals Repo(string owner, string name, int sessions = 1) =>
        new(owner, name, sessions, null,
            new Dictionary<string, int> { [FirstRunImportWindows.Everything] = sessions });

    [Test]
    public async Task Carries_each_repositorys_counts_keyed_by_window() {
        var report = SetupImportLane.Report(Found([Repo("kurrent-io", "kcap-server", 12)], scanned: ["claude"]));

        var repo = report.Repos.Single();

        await Assert.That(repo.Owner).IsEqualTo("kurrent-io");
        await Assert.That(repo.Sessions[FirstRunImportWindows.Everything]).IsEqualTo(12);
        await Assert.That(report.Vendors).IsEquivalentTo(["claude"]);
    }

    [Test]
    public async Task Reports_the_total_before_the_cap_so_what_it_hid_is_disclosable() {
        // A cap with no companion figure is data loss wearing a bound.
        var many = Enumerable.Range(0, ReportFirstRunImportRequest.MaxRepos + 5)
                             .Select(i => Repo("owner", $"repo-{i}"));

        var report = SetupImportLane.Report(Found(many));

        await Assert.That(report.Repos.Count).IsEqualTo(ReportFirstRunImportRequest.MaxRepos);
        await Assert.That(report.RepoTotal).IsEqualTo(ReportFirstRunImportRequest.MaxRepos + 5);
    }

    [Test]
    public async Task Keeps_the_order_discovery_produced_so_the_cap_keeps_the_newest() {
        var report = SetupImportLane.Report(Found([
            Repo("owner", "first"), Repo("owner", "second"), Repo("owner", "third")
        ]));

        await Assert.That(report.Repos.Select(r => r.Name)).IsEquivalentTo(["first", "second", "third"]);
    }

    [Test]
    public async Task An_over_long_identity_is_dropped_and_still_counted_in_the_total() {
        // Dropped, never truncated: owner and name are what resolve back to `--repo owner/name`, so a
        // shortened one names a repository that does not exist. It still counts, because it IS a
        // repository — one we cannot name — and the screen should say it is hiding it.
        var report = SetupImportLane.Report(Found([
            Repo("owner", "fine"),
            Repo(new string('o', ReportFirstRunImportRequest.MaxOwnerLength + 1), "nope"),
            Repo("owner", new string('n', ReportFirstRunImportRequest.MaxNameLength + 1))
        ]));

        await Assert.That(report.Repos.Single().Name).IsEqualTo("fine");
        await Assert.That(report.RepoTotal).IsEqualTo(3);
    }

    [Test]
    public async Task Unmatched_sessions_travel_per_window() {
        var report = SetupImportLane.Report(Found(
            [], new Dictionary<string, int> { [FirstRunImportWindows.Last30] = 8 }));

        await Assert.That(report.Unmatched[FirstRunImportWindows.Last30]).IsEqualTo(8);
    }

    [Test]
    public async Task An_empty_scan_is_a_report_rather_than_nothing_to_say() {
        // The one user with no history has to be told so, not left watching a spinner.
        var report = SetupImportLane.Report(Found([]));

        await Assert.That(report.Repos).IsEmpty();
        await Assert.That(report.RepoTotal).IsEqualTo(0);
    }

    static FirstRunImportAnswer Answer(
            IReadOnlyList<string>? vendors = null, params (string Name, FirstRunImportLevel Level)[] repos) =>
        new([.. repos.Select(r => new FirstRunImportChoice("kurrent-io", r.Name, r.Level))],
            FirstRunImportWindows.Last30,
            FirstRunImportTitles.Server,
            vendors,
            DateTimeOffset.UnixEpoch,
            0);

    SetupImportLane Lane(Func<SetupImportLane.Pass, Task<int>> runner) =>
        new(Config.Root, Resolutions.None(Config.Root), runner);

    [Test]
    public async Task One_pass_per_level_because_private_is_per_invocation() {
        var passes = new List<SetupImportLane.Pass>();

        await Lane(p => { passes.Add(p); return Task.FromResult(0); }).ImportAsync(
            Answer(repos: [("mine", FirstRunImportLevel.OnlyMe), ("ours", FirstRunImportLevel.Shared)]),
            new DateOnly(2026, 6, 15), CancellationToken.None);

        await Assert.That(passes.Select(p => p.Level))
                    .IsEquivalentTo([FirstRunImportLevel.OnlyMe, FirstRunImportLevel.Shared]);
        await Assert.That(passes[0].Repos.Single().Name).IsEqualTo("mine");
        await Assert.That(passes[1].Repos.Single().Name).IsEqualTo("ours");
    }

    [Test]
    public async Task Both_passes_take_the_same_since_boundary() {
        // Resolved once for the decision: two passes either side of UTC midnight would otherwise
        // import against different windows.
        var passes = new List<SetupImportLane.Pass>();

        await Lane(p => { passes.Add(p); return Task.FromResult(0); }).ImportAsync(
            Answer(repos: [("mine", FirstRunImportLevel.OnlyMe), ("ours", FirstRunImportLevel.Shared)]),
            new DateOnly(2026, 6, 15), CancellationToken.None);

        await Assert.That(passes.Select(p => p.Since).Distinct().Count()).IsEqualTo(1);
        await Assert.That(passes[0].Since).IsEqualTo(new DateOnly(2026, 5, 16));
    }

    [Test]
    public async Task A_pass_that_returns_non_zero_is_recorded_as_a_failure() {
        var lane = Lane(_ => Task.FromResult(1));

        await lane.ImportAsync(
            Answer(repos: ("ours", FirstRunImportLevel.Shared)), new DateOnly(2026, 6, 15), CancellationToken.None);

        await Assert.That(lane.Failed).IsTrue();
    }

    [Test]
    public async Task A_pass_that_throws_is_recorded_as_a_failure_too() {
        // The closing summary reads Failed, so a swallowed throw would draw a tick over history that
        // never moved.
        var lane = Lane(_ => throw new HttpRequestException("server went away"));

        await lane.ImportAsync(
            Answer(repos: ("ours", FirstRunImportLevel.Shared)), new DateOnly(2026, 6, 15), CancellationToken.None);

        await Assert.That(lane.Failed).IsTrue();
    }

    [Test]
    public async Task A_throwing_private_pass_does_not_cancel_the_shared_one() {
        // They are separate promises about separate repositories; one failing is not a reason to
        // silently drop the other.
        var seen = new List<FirstRunImportLevel>();

        var lane = Lane(p => {
            seen.Add(p.Level);

            return p.Level is FirstRunImportLevel.OnlyMe
                ? throw new HttpRequestException("private pass died")
                : Task.FromResult(0);
        });

        await lane.ImportAsync(
            Answer(repos: [("mine", FirstRunImportLevel.OnlyMe), ("ours", FirstRunImportLevel.Shared)]),
            new DateOnly(2026, 6, 15), CancellationToken.None);

        await Assert.That(seen).IsEquivalentTo([FirstRunImportLevel.OnlyMe, FirstRunImportLevel.Shared]);
        await Assert.That(lane.Failed).IsTrue();
    }

    [Test]
    public async Task A_cancel_propagates_rather_than_being_recorded_and_swallowed() {
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        var lane = Lane(_ => throw new OperationCanceledException());

        await Assert.That(async () => await lane.ImportAsync(
                        Answer(repos: ("ours", FirstRunImportLevel.Shared)), new DateOnly(2026, 6, 15), cts.Token))
                    .Throws<OperationCanceledException>();
    }

    // The window counts are only comparable with the import that follows them if both resolve from
    // the same instant. This pins the half that carries it into discovery; the flow supplies it.
    [Test]
    public async Task Discovery_resolves_its_windows_against_the_instant_it_is_given() {
        var projects = Config.PathTo("claude-projects");
        var cwdDir   = Path.Combine(projects, "-tmp-asof-proj");
        Directory.CreateDirectory(cwdDir);
        File.WriteAllLines(Path.Combine(cwdDir, "asof-session.jsonl"),
            Enumerable.Range(0, 20).Select(i =>
                $$$"""{"type":"user","timestamp":"2026-03-15T10:00:00Z","cwd":"/tmp/asof-proj","message":{"content":"l{{{i}}}"}}"""));

        async Task<int?> ThirtyDayCountAsOf(DateTimeOffset asOf) {
            ImportCommand.ImportDiscoveryResult? found = null;

            await new ImportCommand(Config.Root, Resolutions.None(Config.Root)).HandleImport(
                filterCwd:    null,
                minLines:     1,
                sources:      [new ClaudeImportSource(Config.Root, projects)],
                discoverOnly: true,
                discoverJson: true,
                windowsAsOf:  asOf,
                onDiscovered: r => found = r);

            return found?.Summary.ByWindow
                        .Single(w => w.Key == FirstRunImportWindows.Last30)
                        .SessionCount;
        }

        // The session is 29 days old against the first instant and 31 against the second, so the
        // 30-day window has to answer differently for each.
        await Assert.That(await ThirtyDayCountAsOf(new DateTimeOffset(2026, 4, 13, 0, 0, 0, TimeSpan.Zero)))
                    .IsEqualTo(1);
        await Assert.That(await ThirtyDayCountAsOf(new DateTimeOffset(2026, 4, 16, 0, 0, 0, TimeSpan.Zero)))
                    .IsEqualTo(0);
    }

    [Test]
    public async Task Every_catalogue_vendor_has_a_source_when_nothing_filters_them() {
        var built = SetupCommand.BuildImportSources(Config.Root);

        await Assert.That(built.Select(b => b.Vendor))
                    .IsEquivalentTo(HarnessCatalog.All.Select(h => h.VendorId));
    }

    [Test]
    public async Task Only_the_named_vendors_sources_are_built() {
        // The filter is applied to what gets scanned, which is what makes a reported figure already
        // scoped rather than needing subtraction afterwards.
        var built = SetupCommand.BuildImportSources(Config.Root, ["claude", "codex"]);

        await Assert.That(built.Select(s => s.Vendor)).IsEquivalentTo(["claude", "codex"]);
    }

    [Test]
    public async Task An_empty_vendor_list_builds_nothing_rather_than_everything() {
        // "Scan nothing" is a real answer — every agent on the machine was left unrecorded — and
        // collapsing it to "no filter" would import exactly what the user declined.
        await Assert.That(SetupCommand.BuildImportSources(Config.Root, [])).IsEmpty();
    }
}
