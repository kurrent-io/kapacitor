using Capacitor.Cli.Core.PullRequests;
using Capacitor.Cli.Core.PullRequests.Readers.GitHubCli;

namespace Capacitor.Cli.Core.Tests.Unit.PullRequests.Readers.GitHubCli;

public class GitHubCliReaderProviderThreadTests {
    [TempDir] public required TempDir Tmp { get; init; }

    static readonly PullRequestSubjectDto Subject = new() { Provider = "github", Host = "github.com", RepoHash = "hash", Owner = "example", RepoName = "repo", Number = 12 };
    const string Cursor1 = "Y3Vyc29yOnYyOpK0MjAyNi0wOS0wOFQwNzo1MTozOVrOoCTmpA==";

    static async Task<GhHarness> Ready(TempDir tmp, string? secondPage = null) {
        var h = new GhHarness(tmp); h.SignedIn("github.com");
        h.Process.WhenAll(["api", "graphql", "after=" + Cursor1], secondPage ?? GhHarness.Fixture("review-threads-2.json"));
        h.Process.WhenAll(["api", "graphql", "-F", "number=12"], GhHarness.Fixture("review-threads.json"));
        h.Process.WhenAll(["api", "graphql", "id=PRRT_2"], GhHarness.Fixture("thread-comments.json"));
        await h.Provider.ProbeAsync(false, default);
        return h;
    }

    [Test]
    public async Task First_threads_page_queries_with_typed_variables_and_hides_resolved_threads_by_default() {
        using var h = await Ready(Tmp);
        var read = await h.Provider.PageAsync<PullRequestThreadDto>("session", Subject, "threads", null, null, null, default);
        await Assert.That(h.LastArgs).IsEquivalentTo(new[] { "api", "graphql", "--hostname", "github.com", "-f", "query=" + GitHubCliMapping.ThreadsQuery,
            "-f", "owner=example", "-f", "repo=repo", "-F", "number=12" });
        var page = read.Data!;
        await Assert.That(page.Items.Select(item => item.Id).ToArray()).IsEquivalentTo(new[] { "PRRT_2" });
        await Assert.That(page.ExcludedByFilter.Value).IsEqualTo(1);
        await Assert.That(page.Total.Kind).IsEqualTo("unknown");
        await Assert.That(page.HasMore).IsTrue();
        await Assert.That(page.HeadSha).IsEqualTo("8dc30b635dcd4aac3970e376d5c2d55fc33b91da");
        var thread = page.Items[0];
        await Assert.That(thread.Path).IsEqualTo("src/B.cs");
        await Assert.That(thread.Line).IsEqualTo(10);
        await Assert.That(thread.DiffSide).IsEqualTo("right");
        await Assert.That(thread.SubjectType).IsEqualTo("line");
        await Assert.That(thread.DiffHunk).IsEqualTo("@@ -5,2 +5,3 @@\n+var y;");
        await Assert.That(thread.RootComment!.Body).IsEqualTo("Open question");
        await Assert.That(thread.RootComment.Author!.Login).IsEqualTo("bob");
        await Assert.That(thread.Comments!.Value).IsEqualTo(1);
        await Assert.That(thread.Url).IsEqualTo("https://github.com/example/repo/pull/12#discussion_r2");
    }

    [Test]
    public async Task Including_resolved_threads_reports_an_exact_total_and_keeps_every_thread() {
        using var h = await Ready(Tmp);
        var page = (await h.Provider.PageAsync<PullRequestThreadDto>("session", Subject, "threads", null, "all", null, default)).Data!;
        await Assert.That(page.Items.Length).IsEqualTo(2);
        await Assert.That(page.Items[0].IsResolved).IsTrue();
        await Assert.That(page.Items[0].IsOutdated).IsTrue();
        await Assert.That(page.Total.Kind).IsEqualTo("exact");
        await Assert.That(page.Total.Value).IsEqualTo(3);
        await Assert.That(page.ExcludedByFilter.Value).IsEqualTo(0);
    }

    [Test]
    public async Task The_next_cursor_continues_the_connection_under_the_same_snapshot() {
        using var h = await Ready(Tmp);
        var first = (await h.Provider.PageAsync<PullRequestThreadDto>("session", Subject, "threads", null, null, null, default)).Data!;
        var second = (await h.Provider.PageAsync<PullRequestThreadDto>("session", Subject, "threads", first.NextCursor, null, null, default)).Data!;
        await Assert.That(h.LastArgs).Contains("after=" + Cursor1);
        await Assert.That(second.SnapshotId).IsEqualTo(first.SnapshotId);
        await Assert.That(second.Items.Single().Id).IsEqualTo("PRRT_3");
        await Assert.That(second.HasMore).IsFalse();
        await Assert.That(second.NextCursor).IsNull();
        var again = (await h.Provider.PageAsync<PullRequestThreadDto>("session", Subject, "threads", first.PageCursor, null, null, default)).Data!;
        await Assert.That(again.Items.Single().Id).IsEqualTo("PRRT_2");
    }

    [Test]
    public async Task A_head_change_between_pages_restarts_the_chain() {
        var moved = GhHarness.Fixture("review-threads-2.json").Replace("8dc30b635dcd4aac3970e376d5c2d55fc33b91da", new string('b', 40));
        using var h = await Ready(Tmp, moved);
        var first = (await h.Provider.PageAsync<PullRequestThreadDto>("session", Subject, "threads", null, null, null, default)).Data!;
        var read = await h.Provider.PageAsync<PullRequestThreadDto>("session", Subject, "threads", first.NextCursor, null, null, default);
        await Assert.That(read.Kind).IsEqualTo(PullRequestReadKind.Restart);
        await Assert.That(read.Reason).IsEqualTo("head_changed");
    }

    [Test]
    public async Task An_all_resolved_page_with_more_behind_it_keeps_fetching_so_a_page_with_more_is_never_empty() {
        var allResolved = GhHarness.Fixture("review-threads.json").Replace("\"isResolved\":false", "\"isResolved\":true");
        using var h = new GhHarness(Tmp); h.SignedIn("github.com");
        h.Process.WhenAll(["api", "graphql", "after=" + Cursor1], GhHarness.Fixture("review-threads-2.json"));
        h.Process.WhenAll(["api", "graphql", "-F", "number=12"], allResolved);
        await h.Provider.ProbeAsync(false, default);
        var page = (await h.Provider.PageAsync<PullRequestThreadDto>("session", Subject, "threads", null, null, null, default)).Data!;
        await Assert.That(h.Process.Calls.Count(call => call.Args[0] == "api")).IsEqualTo(2);
        await Assert.That(page.Items.Single().Id).IsEqualTo("PRRT_3");
        await Assert.That(page.ExcludedByFilter.Value).IsEqualTo(2);
        await Assert.That(page.HasMore).IsFalse();
    }

    [Test]
    public async Task Thread_replies_query_the_thread_node_and_carry_reply_targets() {
        using var h = await Ready(Tmp);
        var read = await h.Provider.PageAsync<PullRequestCommentDto>("session", Subject, "thread_comments", null, null, "PRRT_2", default);
        await Assert.That(h.LastArgs).IsEquivalentTo(new[] { "api", "graphql", "--hostname", "github.com", "-f", "query=" + GitHubCliMapping.ThreadCommentsQuery, "-f", "id=PRRT_2" });
        var page = read.Data!;
        await Assert.That(page.Items.Length).IsEqualTo(2);
        await Assert.That(page.Items[1].ReplyToId).IsEqualTo("PRRC_2");
        await Assert.That(page.Total.Value).IsEqualTo(2);
        await Assert.That(page.HasMore).IsFalse();
    }

    [Test]
    public async Task Invalid_thread_ids_filters_and_a_missing_pull_request_are_refused_or_mapped_without_a_bad_spawn() {
        using var h = await Ready(Tmp);
        var calls = h.Process.Calls.Count;
        await Assert.That((await h.Provider.PageAsync<PullRequestCommentDto>("session", Subject, "thread_comments", null, null, "bad id", default)).Kind).IsEqualTo(PullRequestReadKind.InvalidProtocol);
        await Assert.That((await h.Provider.PageAsync<PullRequestThreadDto>("session", Subject, "threads", null, "resolved", null, default)).Kind).IsEqualTo(PullRequestReadKind.InvalidProtocol);
        await Assert.That(h.Process.Calls.Count).IsEqualTo(calls);
        using var gone = new GhHarness(Tmp); gone.SignedIn("github.com");
        gone.Process.WhenAll(["api", "graphql"], """{"data":{"repository":{"pullRequest":null}}}""");
        await gone.Provider.ProbeAsync(false, default);
        var read = await gone.Provider.PageAsync<PullRequestThreadDto>("session", Subject, "threads", null, null, null, default);
        await Assert.That(read.Kind).IsEqualTo(PullRequestReadKind.Unavailable);
        await Assert.That(read.Reason).IsEqualTo("not_found");
    }
}
