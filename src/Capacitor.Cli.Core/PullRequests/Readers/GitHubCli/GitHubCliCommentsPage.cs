namespace Capacitor.Cli.Core.PullRequests.Readers.GitHubCli;

public sealed record GitHubCliCommentsPage(bool Found, int Total, bool HasNext, string? EndCursor, PullRequestCommentDto[] Comments);
