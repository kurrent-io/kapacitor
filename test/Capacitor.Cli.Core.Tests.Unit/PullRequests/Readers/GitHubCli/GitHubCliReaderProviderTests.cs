using Capacitor.Cli.Core.PullRequests.Readers;

namespace Capacitor.Cli.Core.Tests.Unit.PullRequests.Readers.GitHubCli;

public class GitHubCliReaderProviderTests {
    [TempDir] public required TempDir Tmp { get; init; }

    static readonly PullRequestRepository Repository = new("github", "github.com", "example", "repo", "hash");

    [Test]
    public async Task Probe_reports_the_tool_missing_without_spawning() {
        var h = new GhHarness(Tmp, installed: false);
        var status = await h.Provider.ProbeAsync(false, default);
        await Assert.That(status.Kind).IsEqualTo(PullRequestReaderStatusKind.ToolMissing);
        await Assert.That(h.Process.Calls).IsEmpty();
        await Assert.That(h.Provider.Serves("github", "github.com")).IsFalse();
        await Assert.That(h.Provider.Tool!.Name).IsEqualTo("GitHub CLI");
        await Assert.That(h.Provider.Tool.SignInCommand("ghe.example")).IsEqualTo("gh auth login --hostname ghe.example");
    }

    [Test]
    public async Task Probe_reports_signed_out_and_ready_from_the_hosts_payload() {
        var h = new GhHarness(Tmp);
        h.Process.When(["auth", "status"], """{"hosts":{}}""", exitCode: 1);
        await Assert.That((await h.Provider.ProbeAsync(false, default)).Kind).IsEqualTo(PullRequestReaderStatusKind.SignedOut);
        await Assert.That(h.LastArgs).IsEquivalentTo(new[] { "auth", "status", "--json", "hosts" });
        var fresh = new GhHarness(Tmp);
        fresh.Process.When(["auth", "status"], GhHarness.Fixture("auth-status.json"));
        await Assert.That((await fresh.Provider.ProbeAsync(false, default)).Kind).IsEqualTo(PullRequestReaderStatusKind.Ready);
        await Assert.That(fresh.Provider.Serves("github", "github.com")).IsTrue();
        await Assert.That(fresh.Provider.Serves("github", "GitHub.com")).IsTrue();
        await Assert.That(fresh.Provider.Serves("github", "ghe.example")).IsFalse();
        await Assert.That(fresh.Provider.Serves("gitlab", "github.com")).IsFalse();
    }

    [Test]
    public async Task Probe_results_are_cached_for_five_minutes_and_refresh_reprobes() {
        var h = new GhHarness(Tmp); h.SignedIn("github.com");
        await h.Provider.ProbeAsync(false, default);
        await h.Provider.ProbeAsync(false, default);
        await Assert.That(h.Process.Calls.Count).IsEqualTo(1);
        h.Time.Advance(TimeSpan.FromMinutes(5));
        await h.Provider.ProbeAsync(false, default);
        await Assert.That(h.Process.Calls.Count).IsEqualTo(2);
        await h.Provider.ProbeAsync(true, default);
        await Assert.That(h.Process.Calls.Count).IsEqualTo(3);
    }

    [Test]
    public async Task A_probe_that_cannot_run_backs_off_instead_of_caching_absence() {
        var h = new GhHarness(Tmp);
        h.Process.When(["auth", "status"], "", timedOut: true);
        var status = await h.Provider.ProbeAsync(false, default);
        await Assert.That(status.Kind).IsEqualTo(PullRequestReaderStatusKind.Failed);
        await h.Provider.ProbeAsync(false, default);
        await Assert.That(h.Process.Calls.Count).IsEqualTo(1);
        h.Time.Advance(TimeSpan.FromSeconds(31));
        await h.Provider.ProbeAsync(false, default);
        await Assert.That(h.Process.Calls.Count).IsEqualTo(2);
    }

    [Test]
    [Arguments("https://github.com/example/repo/pull/12", true)]
    [Arguments("https://github.com/example/repo/pull/12/files", true)]
    [Arguments("https://ghe.example/example/repo/pull/12", false)]
    [Arguments("https://github.com/example/repo/issues/12", false)]
    [Arguments("http://github.com/example/repo/pull/12", false)]
    [Arguments("https://github.com/-bad/repo/pull/12", false)]
    public async Task Links_parse_only_on_github_com_or_a_signed_in_host(string url, bool parsed) {
        var h = new GhHarness(Tmp); h.SignedIn("github.com");
        await h.Provider.ProbeAsync(false, default);
        var subject = h.Provider.ParseLink(url);
        await Assert.That(subject is not null).IsEqualTo(parsed);
        if (parsed) {
            await Assert.That(subject!.Number).IsEqualTo(12);
            await Assert.That(subject.RepoHash).IsEqualTo(RepoHashHelper.ComputeRepoHash("example", "repo"));
        }
    }

    [Test]
    public async Task A_signed_in_enterprise_host_parses_and_validates_its_own_links() {
        var h = new GhHarness(Tmp); h.SignedIn("github.com", "ghe.example");
        await h.Provider.ProbeAsync(false, default);
        var subject = h.Provider.ParseLink("https://ghe.example/example/repo/pull/3")!;
        await Assert.That(subject.Host).IsEqualTo("ghe.example");
        await Assert.That(h.Provider.PrLink("https://ghe.example/example/repo/pull/3", subject)).IsEqualTo("https://ghe.example/example/repo/pull/3");
        await Assert.That(h.Provider.PrLink("https://ghe.example/example/repo/pull/4", subject)).IsNull();
        await Assert.That(h.Provider.PrLink("https://github.com/example/repo/pull/3", subject)).IsNull();
    }

    [Test]
    public async Task Live_discovery_runs_pr_list_for_the_branch_and_maps_valid_rows() {
        var h = new GhHarness(Tmp); h.SignedIn("github.com");
        h.Process.When(["pr", "list"], GhHarness.Fixture("pr-list.json"));
        await h.Provider.ProbeAsync(false, default);
        var links = await h.Provider.DiscoverAsync(Repository, "feature", default);
        await Assert.That(h.LastArgs).IsEquivalentTo(new[] { "pr", "list", "--repo", "github.com/example/repo", "--head", "feature", "--state", "all",
            "--limit", "20", "--json", "number,title,url,headRefName,state,isDraft" });
        await Assert.That(links.Select(link => link.Number).ToArray()).IsEquivalentTo(new[] { 12, 9 });
        await Assert.That(links[0].Provider).IsEqualTo("github");
        await Assert.That(links[0].RepoHash).IsEqualTo("hash");
        await Assert.That(links[0].HeadRef).IsEqualTo("feature");
        await Assert.That(links[0].Title).IsEqualTo("Add the thing");
    }

    [Test]
    public async Task Live_discovery_never_spawns_for_an_unserved_host_or_an_invalid_branch() {
        var h = new GhHarness(Tmp); h.SignedIn("github.com");
        await h.Provider.ProbeAsync(false, default);
        var calls = h.Process.Calls.Count;
        await Assert.That(await h.Provider.DiscoverAsync(Repository with { Host = "ghe.example" }, "feature", default)).IsEmpty();
        await Assert.That(await h.Provider.DiscoverAsync(Repository, "-bad", default)).IsEmpty();
        await Assert.That(await h.Provider.DiscoverAsync(Repository with { Owner = "bad owner" }, "feature", default)).IsEmpty();
        await Assert.That(h.Process.Calls.Count).IsEqualTo(calls);
    }
}
