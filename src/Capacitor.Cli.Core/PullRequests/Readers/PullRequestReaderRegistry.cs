namespace Capacitor.Cli.Core.PullRequests.Readers;

/// <summary>
/// Session links always come from <paramref name="sessionLinks"/>; reading routes to the first
/// ready provider serving the subject's kind and host. Nothing here names a provider.
/// </summary>
public sealed class PullRequestReaderRegistry(IPullRequestSource sessionLinks, IReadOnlyList<IPullRequestReaderProvider> providers)
        : IPullRequestSource, IPullRequestReaders {
    readonly Lock _lock = new();
    readonly Dictionary<string, (PullRequestRepository? Repository, string? Branch, string? ReadyKey)> _sessions = new(StringComparer.Ordinal);
    PullRequestReaderStatus[] _statuses = [.. providers.Select(_ => new PullRequestReaderStatus(PullRequestReaderStatusKind.Failed, "not_probed"))];
    string _readyKey = "";

    public async Task<PullRequestCapability> DiscoverAsync(bool refresh, CancellationToken ct) {
        var probes = providers.Select(provider => provider.ProbeAsync(refresh, ct)).ToArray();
        var links = sessionLinks.DiscoverAsync(refresh, ct);
        var statuses = await Task.WhenAll(probes).ConfigureAwait(false);
        var capability = await links.ConfigureAwait(false);
        var key = string.Join(",", providers.Where((_, i) => statuses[i].IsReady).Select(provider => provider.Name));
        lock (_lock) { _statuses = statuses; _readyKey = key; }
        return statuses.Any(status => status.IsReady) ? new(PullRequestCapabilityKind.Supported, 1) : capability;
    }

    public void ResetSession(string sessionId) {
        lock (_lock) _sessions.Remove(sessionId);
        sessionLinks.ResetSession(sessionId);
        foreach (var provider in providers) provider.ResetSession(sessionId);
    }

    public void DescribeSession(string sessionId, PullRequestRepository? repository, string? branch) {
        lock (_lock) {
            if (_sessions.Count >= 1024 && !_sessions.ContainsKey(sessionId)) _sessions.Remove(_sessions.Keys.First());
            var readyKey = _sessions.TryGetValue(sessionId, out var existing) ? existing.ReadyKey : null;
            _sessions[sessionId] = (repository, branch, readyKey);
        }
    }

    public async Task<PullRequestRead<PullRequestLinkListDto>> ListAsync(string sessionId, CancellationToken ct) {
        var capability = await sessionLinks.DiscoverAsync(false, ct).ConfigureAwait(false);
        var links = capability.Kind switch {
            PullRequestCapabilityKind.Supported => await sessionLinks.ListAsync(sessionId, ct).ConfigureAwait(false),
            PullRequestCapabilityKind.Legacy or PullRequestCapabilityKind.Unsupported => await sessionLinks.LegacyLinksAsync(sessionId, ct).ConfigureAwait(false),
            PullRequestCapabilityKind.SignedOut => new(PullRequestReadKind.SignedOut, AccessFailure: "invalid"),
            _ => new PullRequestRead<PullRequestLinkListDto>(PullRequestReadKind.Unavailable, Reason: capability.Reason ?? "discovery_unavailable", AccessFailure: "transient", RetryAt: capability.RetryAt)
        };
        if (links.Kind != PullRequestReadKind.Ready || links.Data is null) return links;
        var items = links.Data.Items.Select(Resolve).ToList();
        (PullRequestRepository? Repository, string? Branch, string? ReadyKey) context;
        lock (_lock) context = _sessions.GetValueOrDefault(sessionId);
        if (context is { Repository: { } repository, Branch: { Length: > 0 } branch })
            foreach (var provider in Ready().Where(provider => provider.Serves(repository.Provider, repository.Host)))
                items.AddRange(await provider.DiscoverAsync(repository, branch, ct).ConfigureAwait(false));
        var merged = items.DistinctBy(item => (item.Provider, item.Host, item.Owner.ToLowerInvariant(), item.RepoName.ToLowerInvariant(), item.Number))
            .OrderBy(item => item.Owner.ToLowerInvariant(), StringComparer.Ordinal).ThenBy(item => item.RepoName.ToLowerInvariant(), StringComparer.Ordinal).ThenBy(item => item.Number).ToArray();
        return links with { Data = new() { Items = merged } };
    }

    public Task<PullRequestRead<PullRequestLinkListDto>> LegacyLinksAsync(string sessionId, CancellationToken ct) => sessionLinks.LegacyLinksAsync(sessionId, ct);

    public Task<PullRequestRead<PullRequestOverviewDto>> OverviewAsync(string sessionId, PullRequestSubjectDto subject, CancellationToken ct) {
        if (Route(subject) is not { } provider) return Task.FromResult(NoReader<PullRequestOverviewDto>(subject));
        if (TakeChange(sessionId)) return Task.FromResult(new PullRequestRead<PullRequestOverviewDto>(PullRequestReadKind.Restart, Subject: subject, Reason: "integration_changed"));
        return provider.OverviewAsync(sessionId, subject, ct);
    }

    public Task<PullRequestRead<PullRequestPageDto<T>>> PageAsync<T>(string sessionId, PullRequestSubjectDto subject, string section,
            string? cursor, string? resolved, string? threadId, CancellationToken ct) where T : class {
        if (Route(subject) is not { } provider) return Task.FromResult(NoReader<PullRequestPageDto<T>>(subject));
        if (TakeChange(sessionId)) return Task.FromResult(new PullRequestRead<PullRequestPageDto<T>>(PullRequestReadKind.Restart, Subject: subject, Reason: "integration_changed"));
        return provider.PageAsync<T>(sessionId, subject, section, cursor, resolved, threadId, ct);
    }

    public PullRequestReaderNote? NoteFor(string provider, string host) {
        if (Ready().Any(reader => reader.Serves(provider, host))) return null;
        PullRequestReaderStatus[] statuses;
        lock (_lock) statuses = _statuses;
        for (var i = 0; i < providers.Count; i++) {
            var reader = providers[i];
            if (reader.ProviderKind != provider || reader.Tool is not { } tool) continue;
            var text = statuses[i].Kind switch {
                PullRequestReaderStatusKind.ToolMissing => $"Install {tool.Name} to read pull requests here.",
                PullRequestReaderStatusKind.SignedOut => $"{tool.Name} is not signed in. Run {tool.SignInCommand(null)} to read pull requests here.",
                PullRequestReaderStatusKind.Ready => $"{tool.Name} is not signed in for {host}. Run {tool.SignInCommand(host)} to read it here.",
                _ => null
            };
            if (text is not null) return new(text, statuses[i].Kind == PullRequestReaderStatusKind.ToolMissing ? tool.InstallUrl : null, tool.Name);
        }
        return null;
    }

    public string? PrLink(string? url, PullRequestSubjectDto subject) {
        var owner = providers.FirstOrDefault(provider => provider.ProviderKind == subject.Provider);
        return owner is null ? PullRequestWire.SafeLink(url) : owner.PrLink(url, subject);
    }

    IEnumerable<IPullRequestReaderProvider> Ready() {
        PullRequestReaderStatus[] statuses;
        lock (_lock) statuses = _statuses;
        return providers.Where((_, i) => statuses[i].IsReady);
    }
    IPullRequestReaderProvider? Route(PullRequestSubjectDto subject) => Ready().FirstOrDefault(provider => provider.Serves(subject.Provider, subject.Host));
    bool TakeChange(string sessionId) {
        lock (_lock) {
            _sessions.TryGetValue(sessionId, out var entry);
            if (entry.ReadyKey == _readyKey) return false;
            _sessions[sessionId] = (entry.Repository, entry.Branch, _readyKey);
            return entry.ReadyKey is not null;
        }
    }
    PullRequestLinkDto Resolve(PullRequestLinkDto link) {
        if (link.Provider != "unknown") return link;
        foreach (var provider in providers) {
            if (provider.ParseLink(link.Url) is not { } subject) continue;
            var hash = link.RepoHash == "legacy" ? RepoHashHelper.ComputeRepoHash(subject.Owner, subject.RepoName) : link.RepoHash;
            return link with { Provider = subject.Provider, Host = subject.Host, Owner = subject.Owner, RepoName = subject.RepoName, Number = subject.Number, RepoHash = hash };
        }
        return link;
    }
    static PullRequestRead<T> NoReader<T>(PullRequestSubjectDto subject) where T : class
        => new(PullRequestReadKind.Unavailable, Subject: subject, Reason: "no_reader", AccessFailure: "invalid");
}
