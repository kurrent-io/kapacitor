namespace Capacitor.Cli.Core.PullRequests.Readers;

/// <summary>What the card needs to tell a user how to get a CLI provider working.</summary>
public sealed record PullRequestReaderTool(string Name, string InstallUrl, Func<string?, string> SignInCommand);
