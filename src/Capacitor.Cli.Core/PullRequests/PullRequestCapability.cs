namespace Capacitor.Cli.Core.PullRequests;

public sealed record PullRequestCapability(PullRequestCapabilityKind Kind, int? Version = null, string? Reason = null, DateTime? RetryAt = null);
