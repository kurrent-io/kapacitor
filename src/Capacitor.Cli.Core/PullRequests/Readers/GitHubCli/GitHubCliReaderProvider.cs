using System.Globalization;

namespace Capacitor.Cli.Core.PullRequests.Readers.GitHubCli;

public sealed class GitHubCliReaderProvider(GitHubCliRunner cli, TimeProvider? time = null) : IPullRequestReaderProvider, IDisposable {
    static readonly PullRequestReaderTool GitHubCliTool = new("GitHub CLI", "https://cli.github.com",
        host => host is null ? "gh auth login" : "gh auth login --hostname " + host);
    readonly TimeProvider _time = time ?? TimeProvider.System;
    readonly SemaphoreSlim _probeGate = new(1, 1);
    readonly GitHubCliCursors _cursors = new();
    readonly Lock _views = new();
    readonly Dictionary<string, Task<(GitHubCliView? View, GitHubCliResult Result)>> _inflight = new(StringComparer.Ordinal);
    readonly Dictionary<string, (long At, GitHubCliView View)> _recent = new(StringComparer.Ordinal);
    HashSet<string> _hosts = new(StringComparer.OrdinalIgnoreCase);
    PullRequestReaderStatus? _status;
    long _probedAt;
    int _failures;
    TimeSpan _ttl;

    public string Name => "github-cli";
    public string ProviderKind => "github";
    public PullRequestReaderTool? Tool => GitHubCliTool;

    public async Task<PullRequestReaderStatus> ProbeAsync(bool refresh, CancellationToken ct) {
        await _probeGate.WaitAsync(ct).ConfigureAwait(false);
        try {
            if (_status is { } cached && !refresh && _time.GetElapsedTime(_probedAt) < _ttl) return cached;
            if (await cli.LocateAsync(refresh, ct).ConfigureAwait(false) is null) return Save(new(PullRequestReaderStatusKind.ToolMissing), []);
            var result = await cli.RunAsync(["auth", "status", "--json", "hosts"], ct).ConfigureAwait(false);
            if (result.Outcome == GitHubCliOutcome.NotStarted) return Save(new(PullRequestReaderStatusKind.ToolMissing), []);
            if (result.Outcome is GitHubCliOutcome.TimedOut or GitHubCliOutcome.Oversized)
                return Save(new(PullRequestReaderStatusKind.Failed, result.Outcome == GitHubCliOutcome.TimedOut ? "timeout" : "oversized"), []);
            var hosts = GitHubCliMapping.SignedInHosts(result.Stdout);
            if (hosts is null) return Save(result.Outcome == GitHubCliOutcome.Failed ? new(PullRequestReaderStatusKind.SignedOut) : new(PullRequestReaderStatusKind.Failed, "malformed"), []);
            return Save(hosts.Count == 0 ? new(PullRequestReaderStatusKind.SignedOut) : new(PullRequestReaderStatusKind.Ready), hosts);
        } finally { _probeGate.Release(); }
    }
    PullRequestReaderStatus Save(PullRequestReaderStatus status, HashSet<string> hosts) {
        var failed = status.Kind == PullRequestReaderStatusKind.Failed;
        _failures = failed ? Math.Min(_failures + 1, 3) : 0;
        _ttl = failed ? TimeSpan.FromSeconds(_failures switch { 1 => 30, 2 => 60, _ => 300 }) : TimeSpan.FromMinutes(5);
        _hosts = hosts;
        _status = status;
        _probedAt = _time.GetTimestamp();
        return status;
    }

    public bool Serves(string provider, string host) => provider == "github" && _status is { IsReady: true } && _hosts.Contains(host);

    public PullRequestSubjectDto? ParseLink(string? url) {
        if (PullRequestWire.SafeLink(url) is not { } safe) return null;
        var uri = new Uri(safe);
        if (!(uri.IdnHost == "github.com" || _hosts.Contains(uri.IdnHost))) return null;
        var parts = uri.AbsolutePath.Trim('/').Split('/');
        if (parts.Length is not (4 or 5) || parts[2] != "pull" || parts.Length == 5 && parts[4] != "files") return null;
        if (!GitHubCliRunner.ValidOwner(parts[0]) || !GitHubCliRunner.ValidRepo(parts[1])
            || !int.TryParse(parts[3], NumberStyles.None, CultureInfo.InvariantCulture, out var number) || number <= 0) return null;
        return new() { Provider = "github", Host = uri.IdnHost, RepoHash = RepoHashHelper.ComputeRepoHash(parts[0], parts[1]),
            Owner = parts[0], RepoName = parts[1], Number = number };
    }

    public string? PrLink(string? url, PullRequestSubjectDto subject) {
        if (PullRequestWire.SafeLink(url) is not { } safe) return null;
        var uri = new Uri(safe);
        var path = $"/{subject.Owner}/{subject.RepoName}/pull/{subject.Number.ToString(CultureInfo.InvariantCulture)}";
        var actual = uri.AbsolutePath.TrimEnd('/');
        return uri.IdnHost.Equals(subject.Host, StringComparison.OrdinalIgnoreCase)
            && (actual.Equals(path, StringComparison.OrdinalIgnoreCase) || actual.Equals(path + "/files", StringComparison.OrdinalIgnoreCase)) ? safe : null;
    }

    public async Task<IReadOnlyList<PullRequestLinkDto>> DiscoverAsync(PullRequestRepository repository, string branch, CancellationToken ct) {
        if (!Serves(repository.Provider, repository.Host) || !GitHubCliRunner.ValidOwner(repository.Owner)
            || !GitHubCliRunner.ValidRepo(repository.RepoName) || !GitHubCliRunner.ValidBranch(branch)) return [];
        var result = await cli.RunAsync(["pr", "list", "--repo", Repo(repository.Host, repository.Owner, repository.RepoName), "--head", branch,
            "--state", "all", "--limit", "20", "--json", "number,title,url,headRefName,state,isDraft"], ct).ConfigureAwait(false);
        return result.Outcome == GitHubCliOutcome.Ok ? GitHubCliMapping.Links(result.Stdout, repository) : [];
    }

    public async Task<PullRequestRead<PullRequestOverviewDto>> OverviewAsync(string sessionId, PullRequestSubjectDto subject, CancellationToken ct) {
        if (Refuse<PullRequestOverviewDto>(subject) is { } refused) return refused;
        var started = _time.GetTimestamp();
        var (view, result) = await ViewAsync(subject, ct).ConfigureAwait(false);
        if (view is null) return result.Outcome == GitHubCliOutcome.Ok ? Invalid<PullRequestOverviewDto>(subject) : GitHubCliMapping.Failure<PullRequestOverviewDto>(result, subject, Now);
        return Ready(view.Overview, subject, view.FetchedAt, started);
    }

    public async Task<PullRequestRead<PullRequestPageDto<T>>> PageAsync<T>(string sessionId, PullRequestSubjectDto subject, string section,
            string? cursor, string? resolved, string? threadId, CancellationToken ct) where T : class {
        if (Refuse<PullRequestPageDto<T>>(subject) is { } refused) return refused;
        var valid = section switch {
            "checks" => typeof(T) == typeof(PullRequestCheckDto), "reviewers" => typeof(T) == typeof(PullRequestReviewerDto),
            "reviews" => typeof(T) == typeof(PullRequestReviewDto), "conversation" => typeof(T) == typeof(PullRequestCommentDto), _ => false
        };
        if (!valid || cursor is not null && !PullRequestWire.ValidHandle(cursor)) return Invalid<PullRequestPageDto<T>>(subject);
        var key = Key(subject) + "|" + section;
        var started = _time.GetTimestamp();
        if (cursor is not null) return Slice<T>(cursor, key, null, subject, started);
        var (view, result) = await ViewAsync(subject, ct).ConfigureAwait(false);
        if (view is null) return result.Outcome == GitHubCliOutcome.Ok ? Invalid<PullRequestPageDto<T>>(subject) : GitHubCliMapping.Failure<PullRequestPageDto<T>>(result, subject, Now);
        (object Items, bool Capped) frozen = section switch {
            "checks" => ((object)view.Checks, false), "reviewers" => ((object)view.Reviewers, false),
            "reviews" => ((object)view.Reviews, view.ReviewsCapped), _ => ((object)view.Comments, view.CommentsCapped)
        };
        var entry = new GitHubCliCursorEntry(GitHubCliCursors.NewHandle(), key, Now, section == "checks" ? view.HeadSha : null, frozen.Items, 0, null, frozen.Capped);
        return Slice<T>(_cursors.Mint(entry), key, entry, subject, started);
    }

    PullRequestRead<PullRequestPageDto<T>> Slice<T>(string handle, string key, GitHubCliCursorEntry? entry, PullRequestSubjectDto subject, long started) where T : class {
        entry ??= _cursors.Get(handle);
        if (entry is null || entry.Key != key || entry.Items is not T[] items)
            return new(PullRequestReadKind.Restart, Subject: subject, Reason: "snapshot_expired");
        var slice = items.Skip(entry.Offset).Take(50).ToArray();
        var hasMore = entry.Offset + 50 < items.Length;
        var capped = entry.Capped;
        var page = new PullRequestPageDto<T> {
            SnapshotId = entry.SnapshotId, SnapshotStartedAt = entry.StartedAt, SnapshotCompletedAt = entry.StartedAt,
            Coverage = capped ? "limited" : "complete", CoverageReason = capped ? "tool_limit" : null, HeadSha = entry.HeadSha,
            Total = new() { Kind = capped ? "lower_bound" : "exact", Value = items.Length }, ExcludedByFilter = new() { Kind = "exact", Value = 0 },
            Items = slice, PageCursor = handle, HasMore = hasMore, NextCursor = hasMore ? _cursors.Mint(entry with { Offset = entry.Offset + 50 }) : null,
        };
        return Ready(page, subject, entry.StartedAt, started);
    }

    Task<(GitHubCliView? View, GitHubCliResult Result)> ViewAsync(PullRequestSubjectDto subject, CancellationToken ct) {
        var key = Key(subject);
        Task<(GitHubCliView?, GitHubCliResult)> task;
        lock (_views) {
            if (_recent.TryGetValue(key, out var recent) && _time.GetElapsedTime(recent.At) < TimeSpan.FromSeconds(10))
                return Task.FromResult<(GitHubCliView?, GitHubCliResult)>((recent.View, new(GitHubCliOutcome.Ok, 0, "", "")));
            if (!_inflight.TryGetValue(key, out task!)) {
                task = FetchAsync(subject, key);
                // A synchronously-completed fetch has already run its own removal (see FetchAsync's finally)
                // before this line: inserting it now would stick forever, since nothing runs the finally twice.
                if (!task.IsCompleted) _inflight[key] = task;
            }
        }
        return task.WaitAsync(ct);
    }

    // A shared fetch runs on its own token: one caller's cancellation must not fail its peers, and the runner's deadline bounds it.
    async Task<(GitHubCliView?, GitHubCliResult)> FetchAsync(PullRequestSubjectDto subject, string key) {
        try {
            var result = await cli.RunAsync(["pr", "view", subject.Number.ToString(CultureInfo.InvariantCulture), "--repo", Repo(subject.Host, subject.Owner, subject.RepoName),
                "--json", GitHubCliMapping.ViewFields], CancellationToken.None).ConfigureAwait(false);
            var view = result.Outcome == GitHubCliOutcome.Ok ? GitHubCliMapping.View(result.Stdout, subject, Now) : null;
            if (view is not null) lock (_views) {
                _recent[key] = (_time.GetTimestamp(), view);
                while (_recent.Count > 64) _recent.Remove(_recent.MinBy(pair => pair.Value.At).Key);
            }
            return (view, result);
        } finally { lock (_views) _inflight.Remove(key); }
    }

    PullRequestRead<T>? Refuse<T>(PullRequestSubjectDto subject) where T : class {
        if (!Serves(subject.Provider, subject.Host)) return new(PullRequestReadKind.Unavailable, Subject: subject, Reason: "no_reader", AccessFailure: "invalid");
        if (!PullRequestWire.ValidSubject(subject) || !GitHubCliRunner.ValidHost(subject.Host) || !GitHubCliRunner.ValidOwner(subject.Owner)
            || !GitHubCliRunner.ValidRepo(subject.RepoName) || !GitHubCliRunner.ValidNumber(subject.Number)) return Invalid<T>(subject);
        return null;
    }
    static PullRequestRead<T> Ready<T>(T data, PullRequestSubjectDto subject, DateTime fetchedAt, long started) where T : class
        => new(PullRequestReadKind.Ready, data, subject, fetchedAt, PollAfterSeconds: 30, AccessValidForSeconds: 30, RequestStarted: started);
    static PullRequestRead<T> Invalid<T>(PullRequestSubjectDto subject) where T : class
        => new(PullRequestReadKind.InvalidProtocol, Subject: subject, Reason: "protocol_error", AccessFailure: "invalid");
    DateTime Now => _time.GetUtcNow().UtcDateTime;
    static string Key(PullRequestSubjectDto subject) => $"{subject.Host}|{subject.Owner}|{subject.RepoName}|{subject.Number.ToString(CultureInfo.InvariantCulture)}".ToLowerInvariant();

    public void ResetSession(string sessionId) { }

    public static string Repo(string host, string owner, string name) => $"{host}/{owner}/{name}";

    public void Dispose() => _probeGate.Dispose();
}
