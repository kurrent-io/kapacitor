namespace Capacitor.Cli.Core.PullRequests;

public enum PullRequestReadKind { Ready, Stale, Unavailable, Restart, SubjectUnavailable, SignedOut, TransportFailure, InvalidProtocol }

public sealed record PullRequestRead<T>(PullRequestReadKind Kind, T? Data = null, PullRequestSubjectDto? Subject = null,
    DateTime? FetchedAt = null, string? Reason = null, string? AccessFailure = null, DateTime? RetryAt = null,
    int PollAfterSeconds = 30, int AccessValidForSeconds = 0, long RequestStarted = 0, int StatusCode = 0) where T : class {
    public double RemainingSeconds(TimeProvider time) => Math.Max(0, AccessValidForSeconds - time.GetElapsedTime(RequestStarted).TotalSeconds);
    public bool CanReveal(TimeProvider time) => Kind is PullRequestReadKind.Ready or PullRequestReadKind.Stale
        && Data is not null && AccessFailure is null && RemainingSeconds(time) >= 5;
}
