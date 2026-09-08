using System.Globalization;
using Capacitor.Cli.Core.PullRequests;
using ReactiveUI;

namespace Capacitor.App.ViewModels;

public sealed partial class PullRequestContextViewModel {
    public string Title => CanDisplay && _overview?.Title is { } title ? title
        : _selected is { IsAvailable: true } choice ? choice.Link.Title ?? choice.Label : _selected?.Label ?? "Pull requests";
    public string Lifecycle => CanDisplay ? _overview?.Lifecycle switch { "draft" => "Draft", "open" => "Open", "merged" => "Merged", "closed" => "Closed", _ => "Unknown" } : "";
    public string Branches => CanDisplay ? (_overview?.HeadRef ?? "?") + " → " + (_overview?.BaseRef ?? "?") : "";
    public string FetchedLabel => CanDisplay && _overviewRead?.FetchedAt is { } at ? "Fetched " + at.ToLocalTime().ToString("HH:mm:ss", CultureInfo.CurrentCulture) : "";
    public string AccessLabel => CanDisplay ? (_grace ? "Access refresh paused" : "Access checked for " + (_overview?.AccessCheckedFor ?? "linked GitHub account")) : "";
    public string ReviewSummary => CanDisplay ? _overview?.ReviewDecision switch {
        "approved" => "Approved", "changes_requested" => "Changes requested", "review_required" => "Review required", _ => "Review decision unknown"
    } : "";
    public string CheckSummary {
        get {
            if (!CanDisplay) return "";
            var checks = _sections.GetValueOrDefault("checks");
            if (checks is { Coverage: "complete", Stopped: false, Total.Kind: "exact", Completed: { } completed }
                && checks.Head == _overview?.HeadSha && _time.GetUtcNow().UtcDateTime - completed < TimeSpan.FromSeconds(30)
                && completed >= _overview?.Checks?.Availability.FetchedAt && checks.Pages.Sum(page => page.Rows.Length) == checks.Total.Value) {
                var rows = checks.Pages.SelectMany(page => page.Rows).ToArray();
                if (rows.Length == 0) return "No checks reported";
                var failed = rows.Count(row => row.Outcome is "failure" or "timed_out" or "action_required");
                var pending = rows.Count(row => row.Outcome == "pending");
                var passed = rows.Count(row => row.Outcome == "success");
                var other = rows.Length - failed - pending - passed;
                return $"{failed} failed · {pending} pending · {passed} passed" + (other > 0 ? $" · {other} other" : "");
            }
            return _overview?.Checks?.Availability.Status == "ready" ? _overview.Checks.Rollup switch {
                "success" => "GitHub summary: successful", "failure" => "GitHub summary: failing", "pending" => "GitHub summary: pending", _ => "GitHub summary: unknown"
            } : "Checks unavailable";
        }
    }
    public string? Description => CanDisplayReader && _section == "overview" ? _overview?.Description : null;
    public bool DescriptionTruncated => CanDisplayReader && _overview?.DescriptionTruncated == true;
    public string DescriptionNote => !CanDisplayReader ? "Refresh access to open PR content." : _overview?.Description is null ? "Description unavailable." : _overview.Description.Length == 0 ? "No description." : "";
    public bool IsOverview => _section == "overview";
    public bool IsThreads => _section == "threads";
    public bool IsThreadComments => _section == "thread_comments";
    public bool HasNotice => _notice.Length > 0;
    public bool ShowsSignIn => _notice.StartsWith("Sign in", StringComparison.Ordinal);
    public bool ShowsLinkGitHub => _notice.StartsWith("Link GitHub", StringComparison.Ordinal);
    public bool ShowReaderContent => CanDisplayReader;
    IReadOnlyList<PullRequestRow> _visibleRows = [];
    public IReadOnlyList<PullRequestRow> Rows => _visibleRows;
    public bool HasMore => CanReveal && CurrentSection is { Stopped: false, Next: not null };
    public bool CanReloadEarlier => CanReveal && CurrentSection is { Stopped: false, Evicted: not null };
    public string PageNote {
        get {
            if (_section == "overview") return "";
            if (!CanDisplayReader) return "Refresh access to open PR content.";
            if (CurrentSection is not { } state) return _pageRequests.Contains(SectionKey) ? "Loading…" : "Choose Refresh to load this section.";
            if (state.Error is { } error) return error;
            if (state.Coverage != "complete") return "Limited snapshot: an ordered subset. More may be available on GitHub.";
            if (state.Pages.Count > 0 && state.Pages.Sum(page => page.Rows.Length) == 0) return _section switch {
                "threads" when state.Excluded is { Kind: "exact", Value: > 0 } => $"No unresolved threads ({state.Excluded.Value} resolved).",
                "threads" => IncludeResolved ? "No threads." : "No unresolved threads.", "checks" => "No checks reported.", "reviewers" => "No reviewers reported.",
                "reviews" => "No published reviews.", _ => "No comments."
            };
            return "";
        }
    }
    public string SnapshotLabel => CanDisplayReader && CurrentSection?.Completed is { } at
        ? "Snapshot " + at.ToLocalTime().ToString("HH:mm:ss", CultureInfo.CurrentCulture) + (CurrentSection.Head is { Length: >= 7 } head ? " · " + head[..7] : "") : "";
    public string SectionTitle => _section switch { "checks" => "Checks", "reviewers" => "Reviewers", "reviews" => "Published reviews",
        "threads" => "Inline threads", "thread_comments" => "Thread replies", "conversation" => "Conversation", _ => "Description" };

    void Notify() {
        _readerNote = _readers is null ? null
            : _selected?.Subject is { } subject ? _readers.NoteFor(subject.Provider, subject.Host)
            : _primaryRepo?.Invoke() is { } repository ? _readers.NoteFor(repository.Provider, repository.Host) : null;
        var rows = CanDisplayReader ? CurrentSection?.Pages.SelectMany(page => page.Rows).ToArray() ?? [] : [];
        if (!_visibleRows.SequenceEqual(rows)) _visibleRows = rows;
        foreach (var property in new[] { nameof(Notice), nameof(IsReading), nameof(HasChoice), nameof(IsLegacy), nameof(Section), nameof(CanReveal), nameof(CanDisplay),
            nameof(Title), nameof(Lifecycle), nameof(Branches), nameof(FetchedLabel), nameof(AccessLabel), nameof(ReviewSummary), nameof(CheckSummary),
            nameof(Description), nameof(DescriptionTruncated), nameof(DescriptionNote), nameof(IsOverview), nameof(IsThreads), nameof(IsThreadComments), nameof(IncludeResolved),
            nameof(HasNotice), nameof(ShowsSignIn), nameof(ShowsLinkGitHub), nameof(ShowReaderContent), nameof(Rows), nameof(HasMore),
            nameof(CanReloadEarlier), nameof(PageNote), nameof(SnapshotLabel), nameof(SectionTitle),
            nameof(ReaderNote), nameof(HasReaderNote), nameof(ShowsInstallTool), nameof(InstallToolLabel) }) this.RaisePropertyChanged(property);
    }
    static string Reason<T>(PullRequestRead<T> read) where T : class => read.Kind switch {
        PullRequestReadKind.SignedOut => "Sign in to see pull requests.",
        PullRequestReadKind.SubjectUnavailable => "This pull request is no longer linked or visible.",
        PullRequestReadKind.InvalidProtocol => "The server returned an invalid PR response. Retry after updating the server and app.",
        PullRequestReadKind.Ready or PullRequestReadKind.Stale => "Refreshing access before opening new content…",
        _ => read.Reason switch {
            "github_not_linked" => "Link GitHub in your account settings to read this pull request.",
            "github_access_denied" => "Your linked GitHub account cannot read this repository.",
            "disabled" => "PR reading is disabled for this workspace.",
            "not_configured" => "PR reading is not configured for this workspace.",
            "integration_capability_unavailable" => "The GitHub integration cannot read this PR. An operator can check its permissions.",
            "rate_limited" or "budget_exhausted" => "GitHub reads are paused temporarily. Retry after the cooldown.",
            "identity_changed" or "integration_changed" => "Access changed. Refresh to reload this pull request.",
            "no_reader" => "No reader is available for this pull request's host.",
            "not_found" => "This pull request could not be found.",
            "tool_signed_out" => "The local CLI is not signed in for this host. Sign in and refresh.",
            "tool_denied" => "Your account cannot read this pull request.",
            "tool_failed" => "The local CLI could not read this pull request. Refresh to try again.",
            _ => "Couldn't load pull request context. Retry when the server is reachable."
        }
    };
    static string Author(PullRequestActorDto? actor) => actor is null ? "Unknown author" : actor.Kind == "team"
        ? actor.Name ?? actor.Login ?? "Team" : actor.Login ?? actor.Name ?? "Unknown author";
    static string Dated(DateTime? at) => at?.ToLocalTime().ToString("g", CultureInfo.CurrentCulture) ?? "";
    static string Outcome(string? value) => value switch {
        "success" => "Passed", "failure" => "Failed", "pending" => "Pending", "neutral" => "Neutral", "skipped" => "Skipped",
        "cancelled" => "Cancelled", "timed_out" => "Timed out", "action_required" => "Action required", "stale" => "Stale", _ => "Unknown"
    };
    static string ReviewState(string? value) => value switch { "approved" => "Approved", "changes_requested" => "Changes requested",
        "commented" => "Commented", "dismissed" => "Dismissed", _ => "Unknown review state" };
    static PullRequestRow Unavailable(string id, string title, string? url, string? availability) => new(id, title, "", null, null, url,
        availability == "redacted" ? "redacted" : "unavailable");
    PullRequestRow ToRow(PullRequestCommentDto item, PullRequestSubjectDto subject) => item.Availability == "available"
        ? new(item.Id, Author(item.Author), Dated(item.CreatedAt), item.Body, null, PrLink(item.Url, subject), "available", item.BodyTruncated)
        : Unavailable(item.Id, "Comment", PrLink(item.Url, subject), item.Availability);
    PullRequestRow ToRow(PullRequestReviewDto item, PullRequestSubjectDto subject) => item.Availability == "available" && item.State is not ("pending" or "PENDING")
        ? new(item.Id, Author(item.Author), ReviewState(item.State) + " · " + Dated(item.SubmittedAt), item.Body, null, PrLink(item.Url, subject), "available", item.BodyTruncated)
        : Unavailable(item.Id, "Review", PrLink(item.Url, subject), item.Availability);
    PullRequestRow ToRow(PullRequestReviewerDto item, PullRequestSubjectDto subject) => item.Availability == "available"
        ? new(item.Id, Author(item.Actor), (item.Requested == true ? "Review requested · " : "") + ReviewState(item.ReviewState),
            null, null, PrLink(item.Url, subject), "available")
        : Unavailable(item.Id, "Reviewer", null, item.Availability);
    PullRequestRow ToRow(PullRequestCheckDto item, PullRequestSubjectDto subject) => item.Availability == "available"
        ? new(item.Id, item.Name ?? "Unnamed check", Outcome(item.Outcome) + " · " + (item.AppName ?? item.Source ?? "Unknown source")
            + (PullRequestWire.CheckLink(item.Url) is { } url ? " · " + new Uri(url).Host : ""),
            null, null, PullRequestWire.CheckLink(item.Url), "available", IsCheck: true, Outcome: item.Outcome ?? "unknown")
        : Unavailable(item.Id, "Check", PullRequestWire.CheckLink(item.Url), item.Availability) with { IsCheck = true, Outcome = "unknown" };
    PullRequestRow ToRow(PullRequestThreadDto item, PullRequestSubjectDto subject) => item.Availability == "available"
        ? new(item.Id, (item.Path ?? "Unknown file") + (item.Line is { } line ? ":" + line.ToString(CultureInfo.InvariantCulture) : ""),
            (item.IsResolved == true ? "Resolved" : item.IsResolved == false ? "Unresolved" : "Resolution unknown")
            + (item.IsOutdated == true ? " · Outdated" : "") + " · " + Author(item.RootComment?.Availability == "available" ? item.RootComment.Author : null),
            item.RootComment?.Availability == "available" ? item.RootComment.Body : null, item.DiffHunk, PrLink(item.Url, subject),
            "available", item.HunkTruncated || item.RootComment?.BodyTruncated == true, IsThread: true)
        : Unavailable(item.Id, "Thread", PrLink(item.Url, subject), item.Availability);
}
