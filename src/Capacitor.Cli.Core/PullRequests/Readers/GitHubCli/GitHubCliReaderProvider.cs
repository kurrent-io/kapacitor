using System.Globalization;

namespace Capacitor.Cli.Core.PullRequests.Readers.GitHubCli;

public sealed class GitHubCliReaderProvider(GitHubCliRunner cli, TimeProvider? time = null) : IPullRequestReaderProvider, IDisposable {
    static readonly PullRequestReaderTool GitHubCliTool = new("GitHub CLI", "https://cli.github.com",
        host => host is null ? "gh auth login" : "gh auth login --hostname " + host);
    readonly TimeProvider _time = time ?? TimeProvider.System;
    readonly SemaphoreSlim _probeGate = new(1, 1);
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

    public Task<PullRequestRead<PullRequestOverviewDto>> OverviewAsync(string sessionId, PullRequestSubjectDto subject, CancellationToken ct)
        => Task.FromResult(new PullRequestRead<PullRequestOverviewDto>(PullRequestReadKind.Unavailable, Subject: subject, Reason: "unsupported", AccessFailure: "invalid"));
    public Task<PullRequestRead<PullRequestPageDto<T>>> PageAsync<T>(string sessionId, PullRequestSubjectDto subject, string section,
            string? cursor, string? resolved, string? threadId, CancellationToken ct) where T : class
        => Task.FromResult(new PullRequestRead<PullRequestPageDto<T>>(PullRequestReadKind.Unavailable, Subject: subject, Reason: "unsupported", AccessFailure: "invalid"));
    public void ResetSession(string sessionId) { }

    public static string Repo(string host, string owner, string name) => $"{host}/{owner}/{name}";

    public void Dispose() => _probeGate.Dispose();
}
