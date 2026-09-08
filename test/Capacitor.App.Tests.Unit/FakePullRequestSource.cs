using System.Globalization;
using Capacitor.Cli.Core.PullRequests;
using Microsoft.Extensions.Time.Testing;

namespace Capacitor.App.Tests.Unit;

internal sealed class FakePullRequestSource(FakeTimeProvider time) : IPullRequestSource {
    public PullRequestLinkDto[] Links = [Link(1), Link(2)];
    public int Lists;
    public int Overviews;
    public int Pages;
    public int TotalPages = 3;
    public string? Failure;
    public string OverviewTitle = "Private PR";
    public readonly Queue<Func<PullRequestSubjectDto, CancellationToken, Task<PullRequestRead<PullRequestOverviewDto>>>> OverviewResponses = new();
    public readonly List<CancellationToken> OverviewTokens = [];
    public Task<PullRequestCapability> DiscoverAsync(bool refresh, CancellationToken ct) => Task.FromResult(new PullRequestCapability(PullRequestCapabilityKind.Supported, 1));
    public void ResetSession(string sessionId) { }
    public Task<PullRequestRead<PullRequestLinkListDto>> ListAsync(string sessionId, CancellationToken ct) {
        Lists++;
        return Task.FromResult(new PullRequestRead<PullRequestLinkListDto>(PullRequestReadKind.Ready, new() { Items = Links }));
    }
    public Task<PullRequestRead<PullRequestLinkListDto>> LegacyLinksAsync(string sessionId, CancellationToken ct) => ListAsync(sessionId, ct);
    public Task<PullRequestRead<PullRequestOverviewDto>> OverviewAsync(string sessionId, PullRequestSubjectDto subject, CancellationToken ct) {
        Overviews++; OverviewTokens.Add(ct);
        if (OverviewResponses.TryDequeue(out var response)) return response(subject, ct);
        return Task.FromResult(Failure is null ? Overview(subject) : new PullRequestRead<PullRequestOverviewDto>(PullRequestReadKind.Unavailable,
            Subject: subject, AccessFailure: Failure, Reason: Failure == "denied" ? "github_access_denied" : "timeout"));
    }
    public PullRequestRead<PullRequestOverviewDto> Overview(PullRequestSubjectDto subject, string? title = null) => new(PullRequestReadKind.Ready,
        new() { Title = title ?? OverviewTitle, Description = "Private description", HeadSha = new string('a', 40), Lifecycle = "open",
            Checks = new() { Availability = new() { Status = "ready", FetchedAt = time.GetUtcNow().UtcDateTime }, Rollup = "success" } },
        subject, time.GetUtcNow().UtcDateTime, AccessValidForSeconds: 30, RequestStarted: time.GetTimestamp());
    public Task<PullRequestRead<PullRequestPageDto<T>>> PageAsync<T>(string sessionId, PullRequestSubjectDto subject, string section,
        string? cursor, string? resolved, string? threadId, CancellationToken ct) where T : class {
        Pages++;
        var page = cursor is null ? 0 : int.Parse(cursor[^8..], NumberStyles.HexNumber, CultureInfo.InvariantCulture);
        var id = "item-" + page.ToString(CultureInfo.InvariantCulture);
        object item = section switch {
            "checks" => new PullRequestCheckDto { Id = id, Availability = "available", Name = "test", Outcome = "failure", HeadSha = new string('a', 40) },
            "reviewers" => new PullRequestReviewerDto { Id = id, Availability = "available" },
            "reviews" => new PullRequestReviewDto { Id = id, Availability = "available", Body = "Private review", State = "commented" },
            "threads" => new PullRequestThreadDto { Id = id, Availability = "available", Path = "source.cs", DiffHunk = "+private code",
                RootComment = new() { Id = "comment", Availability = "available", Body = "Private thread" } },
            _ => new PullRequestCommentDto { Id = id, Availability = "available", Body = "Private comment" }
        };
        var next = page + 1 < TotalPages ? (page + 1).ToString("x64", CultureInfo.InvariantCulture) : null;
        return Task.FromResult(new PullRequestRead<PullRequestPageDto<T>>(PullRequestReadKind.Ready, new() {
            SnapshotId = new string('a', 64), SnapshotStartedAt = time.GetUtcNow().UtcDateTime, SnapshotCompletedAt = time.GetUtcNow().UtcDateTime,
            Coverage = "complete", HeadSha = section == "checks" ? new string('a', 40) : null, Total = new() { Kind = "exact", Value = TotalPages },
            ExcludedByFilter = new() { Kind = "exact", Value = 0 }, Items = [(T)item], PageCursor = page.ToString("x64", CultureInfo.InvariantCulture), NextCursor = next, HasMore = next is not null
        }, subject, time.GetUtcNow().UtcDateTime, AccessValidForSeconds: 30, RequestStarted: time.GetTimestamp()));
    }
    public static PullRequestLinkDto Link(int number) => new() { Provider = "github", Host = "github.com", RepoHash = "hash",
        Owner = "example", RepoName = "repo", Number = number, Url = $"https://github.com/example/repo/pull/{number}", Title = "Linked PR", HeadRef = "feature" };
}
