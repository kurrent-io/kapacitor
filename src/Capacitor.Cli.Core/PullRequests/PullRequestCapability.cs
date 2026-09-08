namespace Capacitor.Cli.Core.PullRequests;

public enum PullRequestCapabilityKind { Supported, Legacy, Unsupported, Unavailable, SignedOut, InvalidProtocol }

public sealed record PullRequestCapability(PullRequestCapabilityKind Kind, int? Version = null, string? Reason = null, DateTime? RetryAt = null);
