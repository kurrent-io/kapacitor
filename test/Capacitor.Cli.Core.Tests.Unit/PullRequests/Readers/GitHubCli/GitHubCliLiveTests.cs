using Capacitor.Cli.Core.PullRequests;
using Capacitor.Cli.Core.PullRequests.Readers.GitHubCli;

namespace Capacitor.Cli.Core.Tests.Unit.PullRequests.Readers.GitHubCli;

/// <summary>Runs the real <c>gh</c> against a public PR when it is installed and signed in; skipped otherwise, so CI without a sign-in stays green.</summary>
public class GitHubCliLiveTests {
    [Test]
    public async Task The_installed_gh_reads_a_public_pull_request_end_to_end() {
        using var runner = new GitHubCliRunner(new ProcessRunner(), null, Environment.GetEnvironmentVariable);
        if (await runner.LocateAsync(false, default) is null) Skip.Test("GitHub CLI is not installed");
        using var provider = new GitHubCliReaderProvider(runner);
        var status = await provider.ProbeAsync(false, default);
        if (!provider.Serves("github", "github.com")) Skip.Test($"GitHub CLI is not signed in to github.com ({status.Kind})");
        var subject = new PullRequestSubjectDto { Provider = "github", Host = "github.com", RepoHash = RepoHashHelper.ComputeRepoHash("kurrent-io", "kcap-cli"),
            Owner = "kurrent-io", RepoName = "kcap-cli", Number = 812 };
        var overview = await provider.OverviewAsync("session", subject, default);
        await Assert.That(overview.Kind).IsEqualTo(PullRequestReadKind.Ready);
        await Assert.That(overview.Data!.Lifecycle).IsEqualTo("merged");
        await Assert.That(overview.Data.Title).IsEqualTo("Read linked pull requests in the desktop workspace");
        var checks = await provider.PageAsync<PullRequestCheckDto>("session", subject, "checks", null, null, null, default);
        await Assert.That(checks.Data!.Items.Length).IsGreaterThan(0);
        var threads = await provider.PageAsync<PullRequestThreadDto>("session", subject, "threads", null, "all", null, default);
        await Assert.That(threads.Kind).IsEqualTo(PullRequestReadKind.Ready);
        await Assert.That(threads.Data!.Items.Length).IsGreaterThan(0);
        var replies = await provider.PageAsync<PullRequestCommentDto>("session", subject, "thread_comments", null, null, threads.Data.Items[0].Id, default);
        await Assert.That(replies.Kind).IsEqualTo(PullRequestReadKind.Ready);
    }
}
