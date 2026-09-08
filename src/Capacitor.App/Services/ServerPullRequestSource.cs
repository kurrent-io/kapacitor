using Capacitor.Cli.Core;
using Capacitor.Cli.Core.Config;
using Capacitor.Cli.Core.PullRequests;

namespace Capacitor.App.Services;

public sealed class ServerPullRequestSource : IPullRequestSource, IAsyncDisposable {
    readonly AuthenticatedServerReads<PullRequestClient> _reads;
    readonly Lock _lock = new();
    readonly Dictionary<string, int> _missing = new(StringComparer.Ordinal);
    long _generation;
    public ServerPullRequestSource(ConfigRoot config, ProfileContext? profiles,
        AuthenticatedServerReads<PullRequestClient>.ClientFactory? factory = null, TimeProvider? time = null) {
        _reads = new(config, profiles, (http, url) => new PullRequestClient(http, url, time), factory, allowAutoRedirect: false);
    }
    public Task<PullRequestCapability> DiscoverAsync(bool refresh, CancellationToken ct) => _reads.ReadAsync((channel, token) => channel.DiscoverAsync(refresh, token),
        read => read.Kind == PullRequestCapabilityKind.SignedOut, new PullRequestCapability(PullRequestCapabilityKind.SignedOut),
        new PullRequestCapability(PullRequestCapabilityKind.Unavailable, Reason: "disposed"), ct);
    public void ResetSession(string sessionId) { lock (_lock) { _generation++; _missing.Remove(sessionId); } }
    public void InvalidateAuthentication() { lock (_lock) { _generation++; _missing.Clear(); } _reads.Invalidate(); }
    public async Task<PullRequestRead<PullRequestLinkListDto>> ListAsync(string sessionId, CancellationToken ct) {
        long generation;
        lock (_lock) {
            if (_missing.GetValueOrDefault(sessionId) >= 3) return new(PullRequestReadKind.SubjectUnavailable, Reason: "retries_stopped", AccessFailure: "invalid");
            generation = _generation;
        }
        var read = await ReadAsync((channel, token) => channel.ListAsync(sessionId, token), ct).ConfigureAwait(false);
        lock (_lock) {
            if (generation != _generation) return read;
            if (read.Kind == PullRequestReadKind.SubjectUnavailable) {
                if (_missing.Count >= 1024 && !_missing.ContainsKey(sessionId)) _missing.Remove(_missing.Keys.First());
                _missing[sessionId] = Math.Min(3, _missing.GetValueOrDefault(sessionId) + 1);
                if (_missing[sessionId] == 3) read = read with { Reason = "retries_stopped" };
            } else if (read.Kind == PullRequestReadKind.Ready) _missing.Remove(sessionId);
        }
        return read;
    }
    public Task<PullRequestRead<PullRequestLinkListDto>> LegacyLinksAsync(string sessionId, CancellationToken ct)
        => ReadAsync((channel, token) => channel.LegacyLinksAsync(sessionId, token), ct);
    public Task<PullRequestRead<PullRequestOverviewDto>> OverviewAsync(string sessionId, PullRequestSubjectDto subject, CancellationToken ct)
        => ReadAsync((channel, token) => channel.OverviewAsync(sessionId, subject, token), ct);
    public Task<PullRequestRead<PullRequestPageDto<T>>> PageAsync<T>(string sessionId, PullRequestSubjectDto subject, string section,
        string? cursor, string? resolved, string? threadId, CancellationToken ct) where T : class
        => ReadAsync((channel, token) => channel.PageAsync<T>(sessionId, subject, section, cursor, resolved, threadId, token), ct);
    Task<PullRequestRead<T>> ReadAsync<T>(Func<PullRequestClient, CancellationToken, Task<PullRequestRead<T>>> read, CancellationToken ct) where T : class
        => _reads.ReadAsync(read, value => value.Kind == PullRequestReadKind.SignedOut,
            new PullRequestRead<T>(PullRequestReadKind.SignedOut, AccessFailure: "invalid"),
            new PullRequestRead<T>(PullRequestReadKind.TransportFailure, Reason: "disposed", AccessFailure: "invalid"), ct);
    public ValueTask DisposeAsync() => _reads.DisposeAsync();
}
