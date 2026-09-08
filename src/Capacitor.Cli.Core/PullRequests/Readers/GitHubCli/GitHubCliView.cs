namespace Capacitor.Cli.Core.PullRequests.Readers.GitHubCli;

/// <summary>One <c>gh pr view</c> result mapped once; the overview and every whole section read from it.</summary>
public sealed record GitHubCliView(PullRequestOverviewDto Overview, string? HeadSha, DateTime FetchedAt, PullRequestCheckDto[] Checks,
    PullRequestReviewerDto[] Reviewers, PullRequestReviewDto[] Reviews, bool ReviewsCapped, PullRequestCommentDto[] Comments, bool CommentsCapped);
