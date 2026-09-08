namespace Capacitor.Cli.Core.PullRequests;

public interface IPullRequestSource {
    Task<PullRequestCapability> DiscoverAsync(bool refresh, CancellationToken ct);
    void ResetSession(string sessionId);
    Task<PullRequestRead<PullRequestLinkListDto>> ListAsync(string sessionId, CancellationToken ct);
    Task<PullRequestRead<PullRequestLinkListDto>> LegacyLinksAsync(string sessionId, CancellationToken ct);
    Task<PullRequestRead<PullRequestOverviewDto>> OverviewAsync(string sessionId, PullRequestSubjectDto subject, CancellationToken ct);
    Task<PullRequestRead<PullRequestPageDto<T>>> PageAsync<T>(string sessionId, PullRequestSubjectDto subject, string section,
        string? cursor, string? resolved, string? threadId, CancellationToken ct) where T : class;
}
