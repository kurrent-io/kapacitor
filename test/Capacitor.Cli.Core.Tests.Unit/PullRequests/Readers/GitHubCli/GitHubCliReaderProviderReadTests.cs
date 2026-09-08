using System.Text.Json;
using Capacitor.Cli.Core.PullRequests;
using Capacitor.Cli.Core.PullRequests.Readers.GitHubCli;

namespace Capacitor.Cli.Core.Tests.Unit.PullRequests.Readers.GitHubCli;

public class GitHubCliReaderProviderReadTests {
    [TempDir] public required TempDir Tmp { get; init; }

    static readonly PullRequestSubjectDto Subject = new() { Provider = "github", Host = "github.com", RepoHash = "hash", Owner = "example", RepoName = "repo", Number = 12 };

    static async Task<GhHarness> Ready(TempDir tmp, string? view = null) {
        var h = new GhHarness(tmp); h.SignedIn("github.com");
        h.Process.When(["pr", "view"], view ?? GhHarness.Fixture("pr-view.json"));
        await h.Provider.ProbeAsync(false, default);
        return h;
    }

    [Test]
    public async Task Overview_maps_lifecycle_decision_rollup_and_summaries_with_a_constant_lease() {
        using var h = await Ready(Tmp);
        var read = await h.Provider.OverviewAsync("session", Subject, default);
        await Assert.That(h.LastArgs).IsEquivalentTo(new[] { "pr", "view", "12", "--repo", "github.com/example/repo", "--json",
            "title,url,state,isDraft,headRefName,baseRefName,headRefOid,body,updatedAt,reviewDecision,author,statusCheckRollup,reviewRequests,latestReviews,reviews,comments" });
        await Assert.That(read.Kind).IsEqualTo(PullRequestReadKind.Ready);
        await Assert.That(read.AccessValidForSeconds).IsEqualTo(30);
        await Assert.That(read.PollAfterSeconds).IsEqualTo(30);
        await Assert.That(read.Subject).IsEqualTo(Subject);
        var data = read.Data!;
        await Assert.That(data.Title).IsEqualTo("Add the thing");
        await Assert.That(data.Lifecycle).IsEqualTo("open");
        await Assert.That(data.HeadSha).IsEqualTo("8dc30b635dcd4aac3970e376d5c2d55fc33b91da");
        await Assert.That(data.Description).IsEqualTo("Adds the thing.\n\nCloses #1");
        await Assert.That(data.ReviewDecision).IsEqualTo("changes_requested");
        await Assert.That(data.Checks!.Rollup).IsEqualTo("failure");
        await Assert.That(data.Checks.Counts!["pending"].Value).IsEqualTo(1);
        await Assert.That(data.Reviews!.Published!.Value).IsEqualTo(2);
        await Assert.That(data.Reviews.Approved!.Value).IsEqualTo(1);
        await Assert.That(data.Reviews.ChangesRequested!.Value).IsEqualTo(1);
        await Assert.That(data.Reviews.OutstandingUsers!.Value).IsEqualTo(1);
        await Assert.That(data.Reviews.OutstandingTeams!.Value).IsEqualTo(1);
        await Assert.That(data.Conversation!.Count!.Value).IsEqualTo(2);
    }

    [Test]
    [Arguments("MERGED", false, "merged")] [Arguments("CLOSED", true, "closed")] [Arguments("OPEN", true, "draft")] [Arguments("WEIRD", false, "unknown")]
    public async Task Lifecycle_prefers_merged_over_closed_and_draft_over_open(string state, bool draft, string lifecycle) {
        var json = GhHarness.Fixture("pr-view.json").Replace("\"state\":\"OPEN\"", $"\"state\":\"{state}\"").Replace("\"isDraft\":false", $"\"isDraft\":{(draft ? "true" : "false")}");
        using var h = await Ready(Tmp, json);
        await Assert.That((await h.Provider.OverviewAsync("session", Subject, default)).Data!.Lifecycle).IsEqualTo(lifecycle);
    }

    [Test]
    public async Task Checks_page_maps_check_runs_and_commit_statuses_as_one_complete_page() {
        using var h = await Ready(Tmp);
        var read = await h.Provider.PageAsync<PullRequestCheckDto>("session", Subject, "checks", null, null, null, default);
        var page = read.Data!;
        await Assert.That(page.Coverage).IsEqualTo("complete");
        await Assert.That(page.HasMore).IsFalse();
        await Assert.That(page.HeadSha).IsEqualTo("8dc30b635dcd4aac3970e376d5c2d55fc33b91da");
        await Assert.That(page.Total.Kind).IsEqualTo("exact");
        await Assert.That(page.Total.Value).IsEqualTo(3);
        await Assert.That(PullRequestWire.ValidHandle(page.SnapshotId)).IsTrue();
        await Assert.That(PullRequestWire.ValidHandle(page.PageCursor)).IsTrue();
        await Assert.That(page.Items.Select(item => item.Outcome!).ToArray()).IsEquivalentTo(new[] { "success", "pending", "failure" });
        await Assert.That(page.Items[0].Name).IsEqualTo("Build and test (ubuntu-latest)");
        await Assert.That(page.Items[0].AppName).IsEqualTo("CI");
        await Assert.That(page.Items[0].Url).IsEqualTo("https://github.com/example/repo/actions/runs/1/job/2");
        await Assert.That(page.Items[2].Name).IsEqualTo("license/cla");
        await Assert.That(page.Items[2].Source).IsEqualTo("status");
        await Assert.That(page.Items.Select(item => item.Id).Distinct().Count()).IsEqualTo(3);
    }

    [Test]
    public async Task Reviewers_union_requests_and_latest_reviews_and_reviews_drop_pending_drafts() {
        using var h = await Ready(Tmp);
        var reviewers = (await h.Provider.PageAsync<PullRequestReviewerDto>("session", Subject, "reviewers", null, null, null, default)).Data!;
        await Assert.That(reviewers.Items.Select(item => (item.Actor!.Login ?? item.Actor.Name)!).ToArray()).IsEquivalentTo(new[] { "carol", "Core", "alice", "bob" });
        await Assert.That(reviewers.Items[0].Requested).IsTrue();
        await Assert.That(reviewers.Items[1].Actor!.Kind).IsEqualTo("team");
        await Assert.That(reviewers.Items[2].ReviewState).IsEqualTo("approved");
        var reviews = (await h.Provider.PageAsync<PullRequestReviewDto>("session", Subject, "reviews", null, null, null, default)).Data!;
        await Assert.That(reviews.Items.Select(item => item.State!).ToArray()).IsEquivalentTo(new[] { "changes_requested", "approved" });
        await Assert.That(reviews.Items[0].Author!.Login).IsEqualTo("bob");
        await Assert.That(reviews.Items[0].Id).IsEqualTo("PRR_kwDOR9HOJ88AAAABMkzATw");
        var conversation = (await h.Provider.PageAsync<PullRequestCommentDto>("session", Subject, "conversation", null, null, null, default)).Data!;
        await Assert.That(conversation.Items.Select(item => item.Body!).ToArray()).IsEquivalentTo(new[] { "Automated summary", "Thanks, addressed." });
        await Assert.That(conversation.Items[0].Url).IsEqualTo("https://github.com/example/repo/pull/12#issuecomment-5581290203");
    }

    [Test]
    public async Task A_list_at_the_tool_limit_is_limited_paged_by_fifty_and_reloadable_by_cursor() {
        using var fixture = JsonDocument.Parse(GhHarness.Fixture("pr-view.json"));
        var comments = Enumerable.Range(0, 100).Select(i => $$"""{"author":{"login":"u{{i}}"},"body":"c{{i}}","createdAt":"2026-09-08T08:00:00Z","id":"IC_{{i}}","url":"https://github.com/example/repo/pull/12#issuecomment-{{i}}"}""");
        var json = GhHarness.Fixture("pr-view.json").Replace(fixture.RootElement.GetProperty("comments").GetRawText(), "[" + string.Join(',', comments) + "]");
        using var h = await Ready(Tmp, json);
        var first = (await h.Provider.PageAsync<PullRequestCommentDto>("session", Subject, "conversation", null, null, null, default)).Data!;
        await Assert.That(first.Coverage).IsEqualTo("limited");
        await Assert.That(first.Total.Kind).IsEqualTo("lower_bound");
        await Assert.That(first.Items.Length).IsEqualTo(50);
        await Assert.That(first.HasMore).IsTrue();
        var second = (await h.Provider.PageAsync<PullRequestCommentDto>("session", Subject, "conversation", first.NextCursor, null, null, default)).Data!;
        await Assert.That(second.SnapshotId).IsEqualTo(first.SnapshotId);
        await Assert.That(second.Items[0].Id).IsEqualTo("IC_50");
        await Assert.That(second.HasMore).IsFalse();
        var again = (await h.Provider.PageAsync<PullRequestCommentDto>("session", Subject, "conversation", first.PageCursor, null, null, default)).Data!;
        await Assert.That(again.Items[0].Id).IsEqualTo("IC_0");
        await Assert.That(h.Process.Calls.Count(call => call.Args[0] == "pr")).IsEqualTo(1);
        var stale = await h.Provider.PageAsync<PullRequestCommentDto>("session", Subject, "conversation", new string('f', 64), null, null, default);
        await Assert.That(stale.Kind).IsEqualTo(PullRequestReadKind.Restart);
        await Assert.That(stale.Reason).IsEqualTo("snapshot_expired");
    }

    [Test]
    public async Task Oversized_bodies_are_cut_with_the_flag_set() {
        var json = GhHarness.Fixture("pr-view.json").Replace("\"body\":\"Adds the thing.\\n\\nCloses #1\"", "\"body\":\"" + new string('x', 262_145) + "\"");
        using var h = await Ready(Tmp, json);
        var data = (await h.Provider.OverviewAsync("session", Subject, default)).Data!;
        await Assert.That(data.Description!.Length).IsEqualTo(262_144);
        await Assert.That(data.DescriptionTruncated).IsTrue();
    }

    [Test]
    public async Task Concurrent_reads_of_one_subject_share_a_single_spawn_and_a_completed_view_is_reused_for_ten_seconds() {
        using var h = new GhHarness(Tmp); h.SignedIn("github.com");
        var pending = new TaskCompletionSource<ProcessResult>();
        h.Process.WhenPending(["pr", "view"], pending);
        await h.Provider.ProbeAsync(false, default);
        var overview = h.Provider.OverviewAsync("session", Subject, default);
        var checks = h.Provider.PageAsync<PullRequestCheckDto>("session", Subject, "checks", null, null, null, default);
        await Task.Delay(50);
        await Assert.That(h.Process.Calls.Count(call => call.Args[0] == "pr")).IsEqualTo(1);
        pending.SetResult(new(0, GhHarness.Fixture("pr-view.json"), "", false));
        await Assert.That((await overview).Kind).IsEqualTo(PullRequestReadKind.Ready);
        await Assert.That((await checks).Kind).IsEqualTo(PullRequestReadKind.Ready);
        h.Time.Advance(TimeSpan.FromSeconds(9));
        await h.Provider.OverviewAsync("session", Subject, default);
        await Assert.That(h.Process.Calls.Count(call => call.Args[0] == "pr")).IsEqualTo(1);
        h.Time.Advance(TimeSpan.FromSeconds(2));
        h.Process.When(["pr", "view"], GhHarness.Fixture("pr-view.json"));
        await h.Provider.OverviewAsync("session", Subject, default);
        await Assert.That(h.Process.Calls.Count(call => call.Args[0] == "pr")).IsEqualTo(2);
    }

    [Test]
    public async Task A_cancelled_caller_returns_promptly_while_the_shared_spawn_finishes_for_its_peers() {
        using var h = new GhHarness(Tmp); h.SignedIn("github.com");
        var pending = new TaskCompletionSource<ProcessResult>();
        h.Process.WhenPending(["pr", "view"], pending);
        await h.Provider.ProbeAsync(false, default);
        using var cancel = new CancellationTokenSource();
        var cancelled = h.Provider.OverviewAsync("session", Subject, cancel.Token);
        var peer = h.Provider.OverviewAsync("session", Subject, default);
        cancel.Cancel();
        var threw = false;
        try { await cancelled; } catch (OperationCanceledException) { threw = true; }
        await Assert.That(threw).IsTrue();
        pending.SetResult(new(0, GhHarness.Fixture("pr-view.json"), "", false));
        await Assert.That((await peer).Kind).IsEqualTo(PullRequestReadKind.Ready);
    }

    [Test]
    [Arguments(1, "GraphQL: Could not resolve to a PullRequest with the number of 12. (repository.pullRequest)", PullRequestReadKind.Unavailable, "not_found", "invalid")]
    [Arguments(1, "HTTP 401: Bad credentials (https://api.github.com/graphql)", PullRequestReadKind.Unavailable, "tool_signed_out", "invalid")]
    [Arguments(1, "HTTP 403: API rate limit exceeded for user ID 1. (https://api.github.com/graphql)", PullRequestReadKind.Unavailable, "rate_limited", null)]
    [Arguments(1, "HTTP 403: Resource not accessible by integration", PullRequestReadKind.Unavailable, "tool_denied", "denied")]
    [Arguments(1, "something else went wrong", PullRequestReadKind.Unavailable, "tool_failed", "transient")]
    public async Task Failed_exits_map_by_message(int exit, string stderr, PullRequestReadKind kind, string reason, string? failure) {
        using var h = new GhHarness(Tmp); h.SignedIn("github.com");
        h.Process.When(["pr", "view"], "", exitCode: exit, stderr: stderr);
        await h.Provider.ProbeAsync(false, default);
        var read = await h.Provider.OverviewAsync("session", Subject, default);
        await Assert.That(read.Kind).IsEqualTo(kind);
        await Assert.That(read.Reason).IsEqualTo(reason);
        await Assert.That(read.AccessFailure).IsEqualTo(failure);
        if (reason == "rate_limited") await Assert.That(read.RetryAt).IsEqualTo(h.Time.GetUtcNow().UtcDateTime.AddSeconds(60));
    }

    [Test]
    public async Task Timeouts_oversized_and_malformed_output_map_to_transport_and_protocol_failures() {
        using var h = new GhHarness(Tmp); h.SignedIn("github.com");
        h.Process.When(["pr", "view", "1"], "", timedOut: true);
        h.Process.When(["pr", "view", "2"], new string('{', GitHubCliRunner.OutputLimit + 1));
        h.Process.When(["pr", "view", "3"], "not json");
        await h.Provider.ProbeAsync(false, default);
        var timeout = await h.Provider.OverviewAsync("session", Subject with { Number = 1 }, default);
        await Assert.That(timeout.Kind).IsEqualTo(PullRequestReadKind.TransportFailure);
        await Assert.That(timeout.AccessFailure).IsEqualTo("transient");
        await Assert.That((await h.Provider.OverviewAsync("session", Subject with { Number = 2 }, default)).Kind).IsEqualTo(PullRequestReadKind.InvalidProtocol);
        await Assert.That((await h.Provider.OverviewAsync("session", Subject with { Number = 3 }, default)).Kind).IsEqualTo(PullRequestReadKind.InvalidProtocol);
    }

    [Test]
    public async Task An_unserved_host_or_invalid_subject_never_spawns() {
        using var h = new GhHarness(Tmp); h.SignedIn("github.com");
        await h.Provider.ProbeAsync(false, default);
        var calls = h.Process.Calls.Count;
        await Assert.That((await h.Provider.OverviewAsync("session", Subject with { Host = "ghe.example" }, default)).Reason).IsEqualTo("no_reader");
        await Assert.That((await h.Provider.OverviewAsync("session", Subject with { Owner = "bad owner" }, default)).Kind).IsEqualTo(PullRequestReadKind.InvalidProtocol);
        await Assert.That((await h.Provider.PageAsync<PullRequestCheckDto>("session", Subject, "checks", "not-a-handle", null, null, default)).Kind).IsEqualTo(PullRequestReadKind.InvalidProtocol);
        await Assert.That((await h.Provider.PageAsync<PullRequestReviewDto>("session", Subject, "checks", null, null, null, default)).Kind).IsEqualTo(PullRequestReadKind.InvalidProtocol);
        await Assert.That(h.Process.Calls.Count).IsEqualTo(calls);
    }
}
