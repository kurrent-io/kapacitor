namespace Capacitor.Cli.Core.PullRequests.Readers;

public sealed record PullRequestRepository(string Provider, string Host, string Owner, string RepoName, string RepoHash);
