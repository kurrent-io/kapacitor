namespace Capacitor.Cli.Core.PullRequests.Readers;

public sealed record PullRequestReaderStatus(PullRequestReaderStatusKind Kind, string? Reason = null) {
    public bool IsReady => Kind == PullRequestReaderStatusKind.Ready;
}
