using Capacitor.Cli.Core.PullRequests;
using ReactiveUI;

namespace Capacitor.App.ViewModels;

public sealed partial class PullRequestContextViewModel {
    bool _refreshDiscovery;
    bool _refreshPage;
    PullRequestSectionState? CurrentSection => _sections.GetValueOrDefault(SectionKey);

    void RequestRefresh(bool manual = false) {
        if (_disposed || !_foreground || _session is not { } session) return;
        if (manual) { _source.ResetSession(session); _stopped = false; _refreshDiscovery = true; _refreshPage = true; }
        if (_stopped || _retryAt > _time.GetUtcNow().UtcDateTime) return;
        if (_refreshing || _lastRefresh is { } last && _time.GetElapsedTime(last).TotalSeconds < 15) {
            _queuedRefresh |= manual;
            return;
        }
        var refresh = _refreshDiscovery;
        _refreshDiscovery = false;
        _queuedRefresh = false;
        _refreshing = true;
        _lastRefresh = _time.GetTimestamp();
        Notify();
        Start(async ct => {
            var capability = await _source.DiscoverAsync(refresh, ct).ConfigureAwait(false);
            var links = capability.Kind switch {
                PullRequestCapabilityKind.Supported => await _source.ListAsync(session, ct).ConfigureAwait(false),
                PullRequestCapabilityKind.Legacy or PullRequestCapabilityKind.Unsupported => await _source.LegacyLinksAsync(session, ct).ConfigureAwait(false),
                _ => null
            };
            return () => {
                _refreshing = false;
                _legacy = capability.Kind is PullRequestCapabilityKind.Legacy or PullRequestCapabilityKind.Unsupported;
                if (links is null) {
                    _retryAt = capability.RetryAt;
                    ClearProtected();
                    SetNotice(capability.Kind == PullRequestCapabilityKind.SignedOut ? "Sign in to see pull requests."
                        : "Couldn't discover pull request support. Retry when the server is reachable.");
                    return;
                }
                if (links.Kind != PullRequestReadKind.Ready || links.Data is null) {
                    if (links.Kind is PullRequestReadKind.SubjectUnavailable or PullRequestReadKind.SignedOut || links.AccessFailure is "invalid" or "denied") {
                        CancelReads(); _choices.Clear(); _selected = null; ClearProtected();
                        _stopped = links.Reason == "retries_stopped";
                    } else EnterGrace();
                    SetNotice(_stopped ? "This session's pull requests are unavailable. Use Retry to check again."
                        : links.Kind == PullRequestReadKind.SignedOut ? "Sign in to see pull requests." : "Couldn't refresh the linked pull requests.");
                    return;
                }
                _retryAt = null;
                _pollAfter = Math.Max(15, links.PollAfterSeconds);
                var incoming = links.Data.Items.Select(link => new PullRequestChoice(link)).ToArray();
                if (!_choices.SequenceEqual(incoming)) { _choices.Clear(); _choices.AddRange(incoming); }
                var previous = _selected?.Subject;
                var selected = _explicitSelection ? incoming.FirstOrDefault(choice => choice.Subject == previous) : null;
                if (selected is null) {
                    var primary = _primaryRepo?.Invoke();
                    var matching = primary is null ? [] : incoming.Where(choice => choice.Link.RepoHash == primary && choice.Link.HeadRef == _branch && _branch is not null).ToArray();
                    selected = matching.Length == 1 ? matching[0] : incoming.FirstOrDefault();
                }
                if (previous != selected?.Subject) Select(selected);
                else { _selected = selected; this.RaisePropertyChanged(nameof(Selected)); }
                if (_legacy) { ClearProtected(); SetNotice("Open on GitHub. Native PR reading requires a compatible server and app."); }
                else if (selected is null) { ClearProtected(); SetNotice("No pull requests linked to this session."); }
                else RequestOverview();
                Notify();
            };
        }, () => _refreshing = false);
    }

    void RequestOverview() {
        if (_disposed || !_foreground || _legacy || _stopped || _overviewPending || _session is not { } session || _selected is not { } choice
            || _retryAt > _time.GetUtcNow().UtcDateTime || _lastOverview is { } last && _time.GetElapsedTime(last).TotalSeconds < 15) return;
        if (!PullRequestWire.IsGitHub(choice.Subject)) { ClearProtected(); SetNotice("Native reading is unavailable for this provider. Open the linked pull request in your browser."); return; }
        _overviewPending = true;
        _lastOverview = _time.GetTimestamp();
        Notify();
        Start(async ct => {
            var read = await _source.OverviewAsync(session, choice.Subject, ct).ConfigureAwait(false);
            return () => {
                if (read.Kind is PullRequestReadKind.Ready or PullRequestReadKind.Stale && read.Data is not null && AcceptAccess(read)) {
                    if (_overview?.HeadSha is { } old && old != read.Data.HeadSha) {
                        foreach (var state in _sections.Values.Where(state => state.Key == "checks")) { state.Pages.Clear(); state.Stopped = true; state.Error = "The PR head changed. Refresh checks."; }
                    }
                    _overview = read.Data; _overviewRead = read;
                    SetNotice(read.Kind == PullRequestReadKind.Stale ? "Showing an earlier snapshot while GitHub is unavailable." : "");
                    if (_readerVisible && _section != "overview" && (CurrentSection is null || _refreshPage)) RequestPage(null, refresh: _refreshPage);
                    _refreshPage = false;
                } else Fail(read);
            };
        }, () => _overviewPending = false);
    }

    void RequestPage(string? cursor, bool refresh = false, bool earlier = false) {
        if (_disposed || !CanReveal || _session is not { } session || _selected is not { } choice || _section == "overview"
            || _retryAt > _time.GetUtcNow().UtcDateTime) return;
        var key = SectionKey;
        if (_pageRequests.Contains(key)) return;
        if (!refresh && CurrentSection is { Stopped: true }) return;
        var section = _section;
        var filter = section == "threads" ? _resolved : null;
        var thread = _thread;
        _pageRequests.Add(key);
        Notify();
        switch (section) {
            case "checks": Page<PullRequestCheckDto>(ToRow); break;
            case "reviewers": Page<PullRequestReviewerDto>(ToRow); break;
            case "reviews": Page<PullRequestReviewDto>(ToRow); break;
            case "threads": Page<PullRequestThreadDto>(ToRow); break;
            default: Page<PullRequestCommentDto>(ToRow); break;
        }
        void Page<T>(Func<T, PullRequestSubjectDto, PullRequestRow> project) where T : class => Start(async ct => {
            var read = await _source.PageAsync<T>(session, choice.Subject, section, cursor, filter, thread, ct).ConfigureAwait(false);
            return () => {
                if (read.Kind is PullRequestReadKind.Ready or PullRequestReadKind.Stale && read.Data is { } page && AcceptAccess(read)) {
                    var state = _sections.GetValueOrDefault(key) ?? new PullRequestSectionState(key);
                    if (state.Snapshot is not null && state.Snapshot != page.SnapshotId && cursor is not null) { FailProtocol(); return; }
                    if (refresh || state.Snapshot != page.SnapshotId) { state.Pages.Clear(); state.Earlier.Clear(); }
                    var rows = page.Items.Select(item => project(item, choice.Subject)).ToArray();
                    var existing = state.Pages.FindIndex(item => item.Cursor == page.PageCursor);
                    var saved = new PullRequestSectionState.Page(page.PageCursor, page.NextCursor, rows, _time.GetTimestamp());
                    if (existing >= 0) state.Pages[existing] = saved;
                    else if (earlier) { state.Pages.Insert(0, saved); state.Earlier.Remove(page.PageCursor); }
                    else {
                        var known = state.Pages.SelectMany(p => p.Rows).Select(row => row.Id).ToHashSet(StringComparer.Ordinal);
                        if (rows.Any(row => !known.Add(row.Id))) { FailProtocol(); return; }
                        state.Pages.Add(saved);
                    }
                    state.Snapshot = page.SnapshotId; state.Started = page.SnapshotStartedAt; state.Completed = page.SnapshotCompletedAt;
                    state.Fetched = read.FetchedAt; state.Head = page.HeadSha; state.Coverage = page.Coverage; state.CoverageReason = page.CoverageReason;
                    state.Total = page.Total; state.Excluded = page.ExcludedByFilter; state.Stopped = false; state.Error = null;
                    _sections[key] = state;
                    EnforcePageBudget(state, saved, earlier);
                    state.Next = state.Pages.LastOrDefault()?.Next;
                    SetNotice(read.Kind == PullRequestReadKind.Stale ? "Showing an earlier page while GitHub is unavailable." : "");
                } else if (read.Kind == PullRequestReadKind.Restart && read.Reason is not ("identity_changed" or "integration_changed")) {
                    var state = _sections.GetValueOrDefault(key) ?? new PullRequestSectionState(key);
                    state.Stopped = true; state.Error = read.Reason == "head_changed" ? "The PR head changed. Refresh checks." : "This snapshot can no longer load pages. Refresh to start again.";
                    if (read.Reason == "head_changed") state.Pages.Clear();
                    _sections[key] = state;
                    Notify();
                } else Fail(read);
            };
        }, () => _pageRequests.Remove(key));
    }
    void EnforcePageBudget(PullRequestSectionState state, PullRequestSectionState.Page newest, bool earlier) {
        while (state.Pages.Count > 8) {
            var evict = earlier ? state.Pages.Last(page => !ReferenceEquals(page, newest)) : state.Pages.First(page => !ReferenceEquals(page, newest));
            Evict(state, evict, rememberEarlier: !earlier);
        }
        while (_sections.Values.Sum(section => section.Bytes) + 2L * (_overview?.Description?.Length ?? 0) > 32 * 1024 * 1024) {
            var empty = _sections.Values.FirstOrDefault(section => section.Key != SectionKey && section.Pages.Count == 0);
            if (empty is not null) { _sections.Remove(empty.Key); continue; }
            var candidates = _sections.Values.Where(section => section.Pages.Count > 0)
                .Select(section => (Section: section, Page: section.Pages[0]))
                .Where(item => !ReferenceEquals(item.Page, newest)).OrderBy(item => item.Section.Key == SectionKey).ThenBy(item => item.Page.Touched).ToArray();
            if (candidates.Length == 0) break;
            var evict = candidates[0]; Evict(evict.Section, evict.Page, rememberEarlier: true);
        }
    }
    static void Evict(PullRequestSectionState section, PullRequestSectionState.Page page, bool rememberEarlier) {
        section.Pages.Remove(page);
        if (rememberEarlier) {
            if (section.Earlier.Count >= 5000) section.Earlier.RemoveAt(0);
            section.Earlier.Add(page.Cursor);
        }
    }
    void Fail<T>(PullRequestRead<T> read) where T : class {
        _retryAt = read.RetryAt;
        if (read.AccessFailure == "transient" || read.Kind is PullRequestReadKind.Ready or PullRequestReadKind.Stale
            || read.AccessFailure is null && read.Reason is "timeout" or "provider_unavailable" or "rate_limited" or "budget_exhausted" or "capacity_exhausted") EnterGrace();
        else ClearProtected();
        SetNotice(Reason(read));
    }
    void FailProtocol() { ClearProtected(); SetNotice("The server returned an inconsistent PR response. Retry after updating the server and app."); }
}
