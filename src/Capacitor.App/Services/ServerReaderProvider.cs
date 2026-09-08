using Capacitor.Cli.Core.PullRequests;
using Capacitor.Cli.Core.PullRequests.Readers;

namespace Capacitor.App.Services;

/// <summary>The server route as one reader among others. The same source also serves the registry's session links.</summary>
public sealed class ServerReaderProvider(ServerPullRequestSource source) : IPullRequestReaderProvider {
    PullRequestCapability _capability = new(PullRequestCapabilityKind.Unavailable, Reason: "not_probed");

    public string Name => "server";
    public string ProviderKind => "github";
    public PullRequestReaderTool? Tool => null;

    public async Task<PullRequestReaderStatus> ProbeAsync(bool refresh, CancellationToken ct) {
        _capability = await source.DiscoverAsync(refresh, ct).ConfigureAwait(false);
        return _capability.Kind == PullRequestCapabilityKind.Supported
            ? new(PullRequestReaderStatusKind.Ready) : new(PullRequestReaderStatusKind.Failed, _capability.Kind.ToString());
    }
    public bool Serves(string provider, string host) => provider == "github" && host == "github.com" && _capability.Kind == PullRequestCapabilityKind.Supported;
    public PullRequestSubjectDto? ParseLink(string? url) => null;
    public string? PrLink(string? url, PullRequestSubjectDto subject) => PullRequestWire.PrLink(url, subject);
    public Task<IReadOnlyList<PullRequestLinkDto>> DiscoverAsync(PullRequestRepository repository, string branch, CancellationToken ct)
        => Task.FromResult<IReadOnlyList<PullRequestLinkDto>>([]);
    public Task<PullRequestRead<PullRequestOverviewDto>> OverviewAsync(string sessionId, PullRequestSubjectDto subject, CancellationToken ct)
        => source.OverviewAsync(sessionId, subject, ct);
    public Task<PullRequestRead<PullRequestPageDto<T>>> PageAsync<T>(string sessionId, PullRequestSubjectDto subject, string section,
        string? cursor, string? resolved, string? threadId, CancellationToken ct) where T : class
        => source.PageAsync<T>(sessionId, subject, section, cursor, resolved, threadId, ct);
    public void ResetSession(string sessionId) => source.ResetSession(sessionId);
}
