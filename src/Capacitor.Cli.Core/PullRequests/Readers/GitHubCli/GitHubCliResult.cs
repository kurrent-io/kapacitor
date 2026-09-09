namespace Capacitor.Cli.Core.PullRequests.Readers.GitHubCli;

public sealed record GitHubCliResult(GitHubCliOutcome Outcome, int ExitCode, string Stdout, string Stderr);
