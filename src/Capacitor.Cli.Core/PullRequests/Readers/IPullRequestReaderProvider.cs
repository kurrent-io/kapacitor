namespace Capacitor.Cli.Core.PullRequests.Readers;

public interface IPullRequestReaderProvider {
    string Name { get; }
    /// <summary>The subject provider kind this reader handles, e.g. <c>github</c>.</summary>
    string ProviderKind { get; }
    PullRequestReaderTool? Tool { get; }
    Task<PullRequestReaderStatus> ProbeAsync(bool refresh, CancellationToken ct);
    /// <summary>Decided from the last probe and the host alone, never from a network call.</summary>
    bool Serves(string provider, string host);
    PullRequestSubjectDto? ParseLink(string? url);
    string? PrLink(string? url, PullRequestSubjectDto subject);
    Task<IReadOnlyList<PullRequestLinkDto>> DiscoverAsync(PullRequestRepository repository, string branch, CancellationToken ct);
    Task<PullRequestRead<PullRequestOverviewDto>> OverviewAsync(string sessionId, PullRequestSubjectDto subject, CancellationToken ct);
    Task<PullRequestRead<PullRequestPageDto<T>>> PageAsync<T>(string sessionId, PullRequestSubjectDto subject, string section,
        string? cursor, string? resolved, string? threadId, CancellationToken ct) where T : class;
    void ResetSession(string sessionId);
}
