using Capacitor.Cli.Core.PullRequests;
using Capacitor.Cli.Core.PullRequests.Readers;
using Microsoft.Extensions.Time.Testing;

namespace Capacitor.App.Tests.Unit;

internal sealed class StubReaderProvider(FakeTimeProvider time, params string[] hosts) : IPullRequestReaderProvider {
    public PullRequestReaderStatusKind Status = PullRequestReaderStatusKind.Ready;
    public readonly List<(PullRequestRepository Repository, string Branch)> Discoveries = [];
    public PullRequestLinkDto[] Discovered = [];
    public string Name => "stub";
    public string ProviderKind => "github";
    public PullRequestReaderTool? Tool => new("GitHub CLI", "https://cli.github.com", host => host is null ? "gh auth login" : "gh auth login --hostname " + host);
    public Task<PullRequestReaderStatus> ProbeAsync(bool refresh, CancellationToken ct) => Task.FromResult(new PullRequestReaderStatus(Status));
    public bool Serves(string provider, string host) => Status == PullRequestReaderStatusKind.Ready && provider == "github" && hosts.Contains(host);
    public PullRequestSubjectDto? ParseLink(string? url) => null;
    public string? PrLink(string? url, PullRequestSubjectDto subject) => PullRequestWire.SafeLink(url) is { } safe
        && new Uri(safe).Host == subject.Host && new Uri(safe).AbsolutePath == $"/{subject.Owner}/{subject.RepoName}/pull/{subject.Number}" ? safe : null;
    public Task<IReadOnlyList<PullRequestLinkDto>> DiscoverAsync(PullRequestRepository repository, string branch, CancellationToken ct) {
        Discoveries.Add((repository, branch));
        return Task.FromResult<IReadOnlyList<PullRequestLinkDto>>(Discovered);
    }
    public Task<PullRequestRead<PullRequestOverviewDto>> OverviewAsync(string sessionId, PullRequestSubjectDto subject, CancellationToken ct)
        => Task.FromResult(new PullRequestRead<PullRequestOverviewDto>(PullRequestReadKind.Ready,
            new() { Title = "Local PR", Description = "Local description", HeadSha = new string('a', 40), Lifecycle = "open" },
            subject, time.GetUtcNow().UtcDateTime, AccessValidForSeconds: 30, RequestStarted: time.GetTimestamp()));
    public Task<PullRequestRead<PullRequestPageDto<T>>> PageAsync<T>(string sessionId, PullRequestSubjectDto subject, string section,
        string? cursor, string? resolved, string? threadId, CancellationToken ct) where T : class
        => Task.FromResult(new PullRequestRead<PullRequestPageDto<T>>(PullRequestReadKind.Unavailable, Subject: subject, Reason: "tool_failed", AccessFailure: "transient"));
    public void ResetSession(string sessionId) { }
}
