using System.Globalization;
using System.Net;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;

namespace Capacitor.Cli.Core.PullRequests;

public sealed class PullRequestClient(HttpClient http, string serverUrl, TimeProvider? clock = null) : IPullRequestSource, IDisposable {
    readonly Uri _base = ServerOrigin(serverUrl);
    readonly TimeProvider _time = clock ?? TimeProvider.System;
    readonly SemaphoreSlim _discoveryGate = new(1, 1);
    PullRequestCapability? _capability;
    long _discovered;
    long _discoveryRevision;
    int _discoveryFailures;
    TimeSpan _discoveryDelay;

    public async Task<PullRequestCapability> DiscoverAsync(bool refresh, CancellationToken ct) {
        var revision = Volatile.Read(ref _discoveryRevision);
        await _discoveryGate.WaitAsync(ct).ConfigureAwait(false);
        try {
            if (_capability is not null && (revision != _discoveryRevision || _time.GetElapsedTime(_discovered) <
                (refresh && _discoveryFailures == 0 ? TimeSpan.FromSeconds(15) : _discoveryDelay))) return _capability;
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(35), _time);
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, deadline.Token);
            try {
                using var request = new HttpRequestMessage(HttpMethod.Get, new Uri(_base, "auth/config"));
                using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, linked.Token).ConfigureAwait(false);
                if (!SameOrigin(response)) return Save(new(PullRequestCapabilityKind.InvalidProtocol, Reason: "unexpected_origin"));
                if (response.StatusCode == HttpStatusCode.Unauthorized) return Save(new(PullRequestCapabilityKind.SignedOut));
                if (!response.IsSuccessStatusCode) return Save(new(PullRequestCapabilityKind.Unavailable, Reason: "discovery_unavailable"));
                using var document = await ReadDocumentAsync(response, linked.Token).ConfigureAwait(false);
                var root = document.RootElement;
                if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("provider", out var provider)
                    || provider.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(provider.GetString())) return Save(new(PullRequestCapabilityKind.InvalidProtocol));
                if (!root.TryGetProperty("pull_request_reads_versions", out var versions)) return Save(new(PullRequestCapabilityKind.Legacy));
                if (versions.ValueKind != JsonValueKind.Array || versions.GetArrayLength() > 100
                    || versions.EnumerateArray().Any(version => version.ValueKind != JsonValueKind.Number || !version.TryGetInt32(out var value) || value <= 0))
                    return Save(new(PullRequestCapabilityKind.InvalidProtocol));
                return Save(versions.EnumerateArray().Any(version => version.GetInt32() == 1) ? new(PullRequestCapabilityKind.Supported, 1) : new(PullRequestCapabilityKind.Unsupported));
            } catch (OperationCanceledException) when (!ct.IsCancellationRequested) { return Save(new(PullRequestCapabilityKind.Unavailable, Reason: "timeout")); }
            catch (Exception exception) when (exception is HttpRequestException or IOException) { return Save(new(PullRequestCapabilityKind.Unavailable, Reason: "discovery_unavailable")); }
            catch (Exception exception) when (exception is JsonException or NotSupportedException) { return Save(new(PullRequestCapabilityKind.InvalidProtocol)); }
        } finally { _discoveryGate.Release(); }
    }
    PullRequestCapability Save(PullRequestCapability value) {
        var failed = value.Kind is PullRequestCapabilityKind.Unavailable or PullRequestCapabilityKind.InvalidProtocol;
        _discoveryFailures = failed ? Math.Min(_discoveryFailures + 1, 3) : 0;
        _discoveryDelay = TimeSpan.FromSeconds(_discoveryFailures switch { 1 => 30, 2 => 60, _ => 300 });
        _capability = failed ? value with { RetryAt = (_time.GetUtcNow() + _discoveryDelay).UtcDateTime } : value;
        _discovered = _time.GetTimestamp();
        Interlocked.Increment(ref _discoveryRevision);
        return _capability;
    }

    public Task<PullRequestRead<PullRequestLinkListDto>> ListAsync(string sessionId, CancellationToken ct) => ReadAsync<PullRequestLinkListDto>(
        SessionPath(sessionId), null, ct);
    public void ResetSession(string sessionId) { }

    public async Task<PullRequestRead<PullRequestLinkListDto>> LegacyLinksAsync(string sessionId, CancellationToken ct) {
        if (!PullRequestWire.ValidSegment(sessionId)) return Invalid<PullRequestLinkListDto>();
        var capability = await DiscoverAsync(false, ct).ConfigureAwait(false);
        if (capability.Kind is not (PullRequestCapabilityKind.Legacy or PullRequestCapabilityKind.Unsupported)) return Invalid<PullRequestLinkListDto>();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(35), _time);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, deadline.Token);
        try {
            using var request = new HttpRequestMessage(HttpMethod.Get, new Uri(_base, "api/sessions/" + Uri.EscapeDataString(sessionId) + "/summary"));
            using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, linked.Token).ConfigureAwait(false);
            if (!SameOrigin(response)) return Invalid<PullRequestLinkListDto>();
            if (response.StatusCode == HttpStatusCode.Unauthorized) return new(PullRequestReadKind.SignedOut, AccessFailure: "invalid");
            if (response.StatusCode == HttpStatusCode.NotFound) return new(PullRequestReadKind.SubjectUnavailable, AccessFailure: "invalid");
            if (!response.IsSuccessStatusCode) return new(PullRequestReadKind.Unavailable, AccessFailure: (int)response.StatusCode >= 500 ? "transient" : "invalid");
            using var document = await ReadDocumentAsync(response, linked.Token).ConfigureAwait(false);
            if (document.RootElement.TryGetProperty("pull_requests", out var links) && links.ValueKind != JsonValueKind.Array)
                return Invalid<PullRequestLinkListDto>();
            var summary = document.Deserialize(CapacitorJsonContext.Default.SessionSummaryDto);
            if (summary is null || summary.SessionId != sessionId || summary.PullRequests is null
                || summary.PullRequests.Any(pr => pr is null || pr.Owner is null || pr.RepoName is null)) return Invalid<PullRequestLinkListDto>();
            var rows = summary.PullRequests.Select(pr => Legacy(pr.RepoHash, pr.Owner, pr.RepoName, pr.Number, pr.Url, pr.Title, pr.HeadRef)).ToList();
            if (summary.PrNumber is > 0 and var number && !rows.Any(row => row.Number == number
                && string.Equals(row.Owner, summary.RepoOwner, StringComparison.OrdinalIgnoreCase) && string.Equals(row.RepoName, summary.RepoName, StringComparison.OrdinalIgnoreCase)))
                rows.Add(Legacy("legacy", summary.RepoOwner ?? "unknown", summary.RepoName ?? "unknown", number, summary.PrUrl, summary.PrTitle, summary.RepoBranch));
            if (rows.Count > 5000 || rows.Any(row => !PullRequestWire.ValidSubject(PullRequestWire.Subject(row)))) return Invalid<PullRequestLinkListDto>();
            return new(PullRequestReadKind.Ready, new() { Items = rows.DistinctBy(row => (row.Owner, row.RepoName, row.Number))
                .OrderBy(row => row.Owner, StringComparer.Ordinal).ThenBy(row => row.RepoName, StringComparer.Ordinal).ThenBy(row => row.Number).ToArray() },
                FetchedAt: _time.GetUtcNow().UtcDateTime);
        } catch (OperationCanceledException) when (!ct.IsCancellationRequested) { return new(PullRequestReadKind.TransportFailure, AccessFailure: "transient"); }
        catch (Exception exception) when (exception is HttpRequestException or IOException) { return new(PullRequestReadKind.TransportFailure, AccessFailure: "transient"); }
        catch (Exception exception) when (exception is JsonException or NotSupportedException) { return Invalid<PullRequestLinkListDto>(); }
    }
    static PullRequestLinkDto Legacy(string hash, string owner, string name, int number, string? url, string? title, string? branch) {
        var safe = PullRequestWire.SafeLink(url);
        return new() { Provider = "unknown", Host = safe is null ? "unknown" : new Uri(safe).IdnHost, RepoHash = hash,
            Owner = owner.ToLowerInvariant(), RepoName = name.ToLowerInvariant(), Number = number, Url = safe, Title = title, HeadRef = branch };
    }
    public Task<PullRequestRead<PullRequestOverviewDto>> OverviewAsync(string sessionId, PullRequestSubjectDto subject, CancellationToken ct)
        => ReadAsync<PullRequestOverviewDto>(SubjectPath(sessionId, subject), subject, ct);
    public Task<PullRequestRead<PullRequestPageDto<T>>> PageAsync<T>(string sessionId, PullRequestSubjectDto subject, string section,
        string? cursor, string? resolved, string? threadId, CancellationToken ct) where T : class {
        var path = SubjectPath(sessionId, subject);
        var valid = section switch {
            "checks" => typeof(T) == typeof(PullRequestCheckDto), "reviewers" => typeof(T) == typeof(PullRequestReviewerDto),
            "reviews" => typeof(T) == typeof(PullRequestReviewDto), "threads" => typeof(T) == typeof(PullRequestThreadDto),
            "conversation" or "thread_comments" => typeof(T) == typeof(PullRequestCommentDto), _ => false
        };
        if (path is null || !valid || cursor is not null && !PullRequestWire.ValidHandle(cursor)
            || resolved is not null && (section != "threads" || resolved is not ("unresolved" or "all"))
            || section == "thread_comments" && !PullRequestWire.ValidSegment(threadId)) return Task.FromResult(Invalid<PullRequestPageDto<T>>());
        path += section == "thread_comments" ? "/threads/" + Uri.EscapeDataString(threadId!) + "/comments" : "/" + section;
        if (cursor is not null) path += "?cursor=" + Uri.EscapeDataString(cursor);
        if (resolved is not null) path += (cursor is null ? "?" : "&") + "resolved=" + Uri.EscapeDataString(resolved);
        return ReadAsync<PullRequestPageDto<T>>(path, subject, ct);
    }
    static string? SessionPath(string session) => PullRequestWire.ValidSegment(session) ? "api/sessions/" + Uri.EscapeDataString(session) + "/pull-requests" : null;
    static string? SubjectPath(string session, PullRequestSubjectDto subject) => SessionPath(session) is { } path && PullRequestWire.ValidSubject(subject)
        ? path + "/" + Uri.EscapeDataString(subject.RepoHash) + "/" + subject.Number.ToString(CultureInfo.InvariantCulture) : null;

    async Task<PullRequestRead<T>> ReadAsync<T>(string? path, PullRequestSubjectDto? subject, CancellationToken ct) where T : class {
        if (path is null || PullRequestJsonContext.Default.GetTypeInfo(typeof(PullRequestEnvelopeDto<T>)) is not JsonTypeInfo<PullRequestEnvelopeDto<T>> info) return Invalid<T>();
        var capability = await DiscoverAsync(false, ct).ConfigureAwait(false);
        if (capability.Kind != PullRequestCapabilityKind.Supported) return new(PullRequestReadKind.Unavailable, Reason: "unsupported", AccessFailure: "invalid");
        var started = _time.GetTimestamp();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(35), _time);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, deadline.Token);
        try {
            using var request = new HttpRequestMessage(HttpMethod.Get, new Uri(_base, path + (path.Contains('?', StringComparison.Ordinal) ? "&" : "?") + "version=1"));
            using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, linked.Token).ConfigureAwait(false);
            if (!SameOrigin(response)) return Invalid<T>("unexpected_origin");
            var status = (int)response.StatusCode;
            if (status == 401) return new(PullRequestReadKind.SignedOut, Subject: subject, AccessFailure: "invalid", StatusCode: status);
            if (status == 404) return new(PullRequestReadKind.SubjectUnavailable, Subject: subject, AccessFailure: "invalid", StatusCode: status);
            if (status is >= 300 and < 400) return Invalid<T>("unexpected_origin");
            if (status is >= 500 or 429) return new(PullRequestReadKind.Unavailable, Subject: subject, Reason: "server_unavailable", AccessFailure: "transient", StatusCode: status);
            using var document = await ReadDocumentAsync(response, linked.Token).ConfigureAwait(false);
            if (!PullRequestWire.ValidJson(document.RootElement)) return Invalid<T>();
            if (status == 409) {
                var error = document.Deserialize(PullRequestJsonContext.Default.PullRequestErrorDto);
                return error?.Error == "restart_required" ? new(PullRequestReadKind.Restart, Subject: subject, Reason: error.Reason, StatusCode: status) : Invalid<T>();
            }
            if (!response.IsSuccessStatusCode) return new(PullRequestReadKind.Unavailable, Subject: subject, Reason: "server_unavailable",
                AccessFailure: status is >= 500 or 429 ? "transient" : "invalid", StatusCode: status);
            var envelope = document.Deserialize(info);
            if (envelope is null || envelope.Status is not ("ready" or "stale" or "unavailable") || envelope.AccessValidForSeconds is < 0 or > 30 || envelope.PollAfterSeconds < 0
                || subject is not null && envelope.Subject != subject || subject is null && (envelope.Subject is not null || envelope.Status == "stale")) return Invalid<T>();
            var failure = envelope.AccessFailure switch { null => null, "transient" => "transient", "denied" => "denied", _ => "invalid" };
            if (envelope.Status == "unavailable" || failure is not null) return new(PullRequestReadKind.Unavailable, Subject: subject, Reason: envelope.Reason,
                AccessFailure: failure, RetryAt: envelope.RetryAt, PollAfterSeconds: envelope.PollAfterSeconds, StatusCode: status);
            if (envelope.Data is null || !PullRequestWire.ValidData(envelope.Data) || envelope.FetchedAt is null) return Invalid<T>();
            return new(envelope.Status == "stale" ? PullRequestReadKind.Stale : PullRequestReadKind.Ready, envelope.Data, subject, envelope.FetchedAt,
                envelope.Reason, null, envelope.RetryAt, envelope.PollAfterSeconds, envelope.AccessValidForSeconds, started, status);
        } catch (OperationCanceledException) when (!ct.IsCancellationRequested) { return new(PullRequestReadKind.TransportFailure, Subject: subject, Reason: "timeout", AccessFailure: "transient"); }
        catch (Exception exception) when (exception is HttpRequestException or IOException) { return new(PullRequestReadKind.TransportFailure, Subject: subject, AccessFailure: "transient"); }
        catch (Exception exception) when (exception is JsonException or NotSupportedException) { return Invalid<T>(); }
    }
    bool SameOrigin(HttpResponseMessage response) => response.RequestMessage?.RequestUri is { } actual
        && actual.Scheme == _base.Scheme && actual.IdnHost == _base.IdnHost && actual.Port == _base.Port && actual.UserInfo.Length == 0;
    static Uri ServerOrigin(string url) {
        var origin = new Uri(url.TrimEnd('/') + "/", UriKind.Absolute);
        if (origin.Scheme is not ("http" or "https") || origin.UserInfo.Length != 0 || origin.Query.Length != 0 || origin.Fragment.Length != 0)
            throw new ArgumentException("The server URL must be an HTTP or HTTPS origin without credentials, query or fragment.", nameof(url));
        return origin;
    }
    static async Task<JsonDocument> ReadDocumentAsync(HttpResponseMessage response, CancellationToken ct) {
        const int limit = 4 * 1024 * 1024;
        if (response.Content.Headers.ContentLength > limit) throw new JsonException();
        await using var input = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        var bytes = new byte[limit + 1];
        var count = await input.ReadAtLeastAsync(bytes, bytes.Length, false, ct).ConfigureAwait(false);
        if (count > limit) throw new JsonException();
        return JsonDocument.Parse(bytes.AsMemory(0, count), new JsonDocumentOptions { MaxDepth = 64 });
    }
    static PullRequestRead<T> Invalid<T>(string reason = "protocol_error") where T : class => new(PullRequestReadKind.InvalidProtocol, Reason: reason, AccessFailure: "invalid");
    public void Dispose() => _discoveryGate.Dispose();
}
