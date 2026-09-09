namespace Capacitor.Cli.Core.PullRequests.Readers.GitHubCli;

/// <summary>One GraphQL <c>reviewThreads</c> page. <see cref="Found"/> is false when the PR resolved to null.</summary>
public sealed record GitHubCliThreadsPage(bool Found, string? HeadSha, int Total, bool HasNext, string? EndCursor, PullRequestThreadDto[] Threads);
