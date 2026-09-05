using System.Reactive;
using Avalonia.Collections;
using Capacitor.Cli.Core.LocalIpc;
using Capacitor.Cli.Core.WorkItems;
using ReactiveUI;

namespace Capacitor.App.ViewModels;

/// The server-derived half of the pane and how one read merges into it, section by section: a
/// failed section keeps its last projection and marks the pane stale, an authoritative empty
/// answer clears it, and a terminal phase clears everything the server gave.
public sealed partial class WorkContextViewModel {
    readonly AvaloniaList<WorkContextPartViewModel> _parts = new();
    readonly AvaloniaList<string> _blockedBy = new();
    readonly AvaloniaList<WorkContextLinkViewModel> _links = new();
    readonly AvaloniaList<WorkContextPersonViewModel> _contributors = new();
    // The card's identity is the id the server served, which for an absorbed item is the survivor's
    // rather than the assignment's; the requested id stands in when a read carried no item.
    string? _primaryId;
    string? _requestedId;

    public IAvaloniaReadOnlyList<WorkContextPartViewModel> Parts => _parts;
    public IAvaloniaReadOnlyList<string> BlockedBy => _blockedBy;
    public IAvaloniaReadOnlyList<WorkContextLinkViewModel> Links => _links;
    public IAvaloniaReadOnlyList<WorkContextPersonViewModel> Contributors => _contributors;

    string? _key;
    public string? Key { get => _key; private set => this.RaiseAndSetIfChanged(ref _key, value); }
    string _title = "";
    public string Title { get => _title; private set => this.RaiseAndSetIfChanged(ref _title, value); }
    string? _overview;
    public string? Overview { get => _overview; private set => this.RaiseAndSetIfChanged(ref _overview, value); }
    string? _stateLabel;
    public string? StateLabel { get => _stateLabel; private set => this.RaiseAndSetIfChanged(ref _stateLabel, value); }
    bool _isShipped;
    public bool IsShipped { get => _isShipped; private set => this.RaiseAndSetIfChanged(ref _isShipped, value); }
    bool _isClosed;
    public bool IsClosed { get => _isClosed; private set => this.RaiseAndSetIfChanged(ref _isClosed, value); }
    string? _partOfTitle;
    public string? PartOfTitle { get => _partOfTitle; private set => this.RaiseAndSetIfChanged(ref _partOfTitle, value); }
    string? _cycleNote;
    public string? CycleNote { get => _cycleNote; private set => this.RaiseAndSetIfChanged(ref _cycleNote, value); }

    WorkContextLinkViewModel? _issue;
    public WorkContextLinkViewModel? Issue {
        get => _issue;
        private set {
            if (ReferenceEquals(_issue, value)) return;
            this.RaiseAndSetIfChanged(ref _issue, value);
            this.RaisePropertyChanged(nameof(HasIssue));
        }
    }

    int _sessionCount;
    int SessionCount {
        get => _sessionCount;
        set {
            if (_sessionCount == value) return;
            _sessionCount = value;
            this.RaisePropertyChanged(nameof(SessionCountText));
        }
    }

    public string PartsHeader => _parts.Count switch {
        0     => "0 parts",
        1     => $"{SettledCount} of 1 part",
        var n => $"{SettledCount} of {n} parts",
    };
    int SettledCount => _parts.Count(p => p.IsSettled);
    public bool HasParts => _parts.Count > 0;
    public bool HasBlockers => _blockedBy.Count > 0;
    public bool HasIssue => Issue is not null;
    public bool HasContributors => _contributors.Count > 0;
    public string SessionCountText => _sessionCount switch {
        0     => "",
        1     => "1 session",
        var n => $"{n} sessions",
    };

    string _requester = "You";
    public string Requester { get => _requester; private set => this.RaiseAndSetIfChanged(ref _requester, value); }
    string _requesterRole = "";
    public string RequesterRole { get => _requesterRole; private set => this.RaiseAndSetIfChanged(ref _requesterRole, value); }
    string _requesterInitial = "Y";
    public string RequesterInitial { get => _requesterInitial; private set => this.RaiseAndSetIfChanged(ref _requesterInitial, value); }

    bool _partsExpanded = true;
    public bool PartsExpanded { get => _partsExpanded; private set => this.RaiseAndSetIfChanged(ref _partsExpanded, value); }
    bool _peopleExpanded;
    public bool PeopleExpanded { get => _peopleExpanded; private set => this.RaiseAndSetIfChanged(ref _peopleExpanded, value); }
    bool _sessionExpanded;
    public bool SessionExpanded { get => _sessionExpanded; private set => this.RaiseAndSetIfChanged(ref _sessionExpanded, value); }

    public ReactiveCommand<Unit, Unit> TogglePartsCommand { get; private set; } = null!;
    public ReactiveCommand<Unit, Unit> TogglePeopleCommand { get; private set; } = null!;
    public ReactiveCommand<Unit, Unit> ToggleSessionCommand { get; private set; } = null!;

    void InitializeProjections() {
        TogglePartsCommand   = Toggle(() => PartsExpanded = !PartsExpanded);
        TogglePeopleCommand  = Toggle(() => PeopleExpanded = !PeopleExpanded);
        ToggleSessionCommand = Toggle(() => SessionExpanded = !SessionExpanded);
    }

    ReactiveCommand<Unit, Unit> Toggle(Action flip) {
        var command = ReactiveCommand.Create(flip);
        _disposables.Add(command);
        return command;
    }

    void UpdateRequester(AgentStatusDto dto, string vendorLabel) {
        Requester = FirstNonBlank(dto.RequesterDisplay, dto.Requester) ?? "You";
        RequesterRole = $"This session · {vendorLabel}";
        RequesterInitial = WorkContextPersonViewModel.InitialOf(Requester);
    }

    static string? FirstNonBlank(params string?[] values) {
        foreach (var value in values)
            if (!string.IsNullOrWhiteSpace(value)) return value.Trim();
        return null;
    }

    void ClearServerProjections() {
        ClearCard();
        _links.Clear();
    }

    void ClearCard() {
        _primaryId = null;
        _requestedId = null;
        ClearItem();
        ClearTopology();
    }

    void ClearItem() {
        Key = null;
        Title = "";
        Overview = null;
        ApplyState(null);
        _parts.Clear();
        Issue = null;
        _contributors.Clear();
        SessionCount = 0;
        RaiseCardCounts();
    }

    void ClearTopology() {
        PartOfTitle = null;
        CycleNote = null;
        _blockedBy.Clear();
        RaiseCardCounts();
    }

    void RaiseCardCounts() {
        this.RaisePropertyChanged(nameof(PartsHeader));
        this.RaisePropertyChanged(nameof(HasParts));
        this.RaisePropertyChanged(nameof(HasBlockers));
        this.RaisePropertyChanged(nameof(HasContributors));
    }

    void ApplyReady(WorkContextRead read) {
        if (read.Primary is null) {
            ClearCard();
            ApplyLinks(read);
            Phase = WorkContextPhase.NoWorkItem;
            IsStale = read.SummaryFailed;
            return;
        }

        var requested = read.Primary.WorkItemId;
        var served = read.Item?.WorkItemId;
        var samePrimary = served is not null
            ? Same(served, _primaryId)
            : Same(requested, _requestedId) || Same(requested, _primaryId);
        _requestedId = requested;
        if (served is not null) _primaryId = served;
        else if (!samePrimary) _primaryId = requested;

        if (read.Item is { } item) ApplyItem(item, read.Assignments);
        else if (!samePrimary) {
            ClearItem();
            Title = read.Primary.Label;
        }

        if (!read.TopologyFailed && read.Topology is { } topology) ApplyTopology(topology);
        else if (!samePrimary) ClearTopology();

        ApplyLinks(read);
        Phase = WorkContextPhase.Ready;
        IsStale = read.ItemFailed || read.TopologyFailed || read.SummaryFailed;
    }

    static bool Same(string a, string? b) => string.Equals(a, b, StringComparison.Ordinal);

    void ApplyItem(WorkItemDto item, IReadOnlyList<SessionWorkItemAssignmentDto> assignments) {
        Key = FirstNonBlank(item.Key?.ShortKey);
        Title = FirstNonBlank(item.EnrichedTitle, item.Title) ?? "";
        Overview = item.IsOverviewMechanical ? null : FirstNonBlank(item.Overview);
        ApplyState(item.State?.Kind);

        var attached = new HashSet<string>(assignments.Select(a => a.WorkItemId), StringComparer.Ordinal);
        var parts = item.Parts
            .OrderBy(p => p.Ordinal)
            .Select(p => new WorkContextPartViewModel(p.Title, PartMark(p, attached)))
            .ToList();
        Replace(_parts, parts, p => (p.Title, p.Mark));

        ApplyIssue(item.Links.FirstOrDefault(l => l.Kind == "issue" && l.LinkClass == "link"));

        var now = _time.GetUtcNow();
        var people = item.Contributors
            .Select(c => new WorkContextPersonViewModel(FirstNonBlank(c.DisplayName, c.UserId) ?? "Someone", c.AvatarUrl, c.LastActivityAt, now))
            .ToList();
        Replace(_contributors, people, c => (c.Name, c.LastActivityText));
        SessionCount = item.SessionCount;
        RaiseCardCounts();
    }

    static WorkContextPartMark PartMark(WorkItemPartDto part, HashSet<string> attached) =>
        part.IsSettled ? WorkContextPartMark.Settled
        : attached.Contains(part.WorkItemId) ? WorkContextPartMark.ThisSession
        : WorkContextPartMark.Unknown;

    /// The server may add kinds; an unknown one is shown as sent, in the in-flight look.
    void ApplyState(string? kind) {
        var text = kind?.Replace('_', ' ').Trim().ToUpperInvariant();
        StateLabel = string.IsNullOrEmpty(text) ? null : text;
        IsShipped = kind == "shipped";
        IsClosed  = kind == "closed";
    }

    void ApplyIssue(WorkItemLinkDto? link) {
        if (link is null) {
            Issue = null;
            return;
        }
        var title = FirstNonBlank(link.Title) ?? $"Issue {link.ShortKey}";
        if (Issue is { } current && current.Key == link.ShortKey && current.Title == title && current.Url == link.Url) return;
        Issue = new WorkContextLinkViewModel("ISSUE", link.ShortKey, title, link.Url, _opener);
    }

    void ApplyTopology(WorkItemTopologyDto topology) {
        PartOfTitle = topology.PartOf?.Title;
        Replace(_blockedBy, topology.BlockedBy.Select(b => b.Title).ToList(), b => b);
        CycleNote = topology.Cycle switch {
            "cyclic"        => "Dependencies form a cycle",
            "indeterminate" => "Dependencies could not be fully resolved",
            _               => null,
        };
        RaiseCardCounts();
    }

    void ApplyLinks(WorkContextRead read) {
        if (read.SummaryFailed) return;
        if (read.Summary is not { } summary) return;

        var cards = summary.PullRequests
            .Select(pr => Link(pr.Number, pr.Title, pr.Url))
            .ToList();
        if (summary.PrNumber is { } number && !summary.PullRequests.Any(pr => SamePullRequest(pr, summary, number)))
            cards.Add(Link(number, summary.PrTitle, summary.PrUrl));

        Replace(_links, cards, l => (l.Key, l.Title, l.Url));
    }

    /// A poll that returns the same rows leaves the bound list alone, so the ItemsControl keeps its
    /// containers instead of rebuilding them every 30 seconds.
    static void Replace<T, TKey>(AvaloniaList<T> target, List<T> incoming, Func<T, TKey> key) {
        if (target.Select(key).SequenceEqual(incoming.Select(key))) return;
        target.Clear();
        target.AddRange(incoming);
    }

    WorkContextLinkViewModel Link(int number, string? title, string? url) =>
        new("PULL REQUEST", $"#{number}", title ?? $"Pull request #{number}", url, _opener);

    /// PR numbers are repository-local; without a repository identity on the summary the number
    /// alone decides, which never shows one PR twice.
    internal static bool SamePullRequest(SessionPullRequestDto pr, SessionSummaryDto summary, int number) {
        if (pr.Number != number) return false;
        if (string.IsNullOrEmpty(summary.RepoOwner) || string.IsNullOrEmpty(summary.RepoName)) return true;

        return string.Equals(pr.Owner, summary.RepoOwner, StringComparison.OrdinalIgnoreCase)
            && string.Equals(pr.RepoName, summary.RepoName, StringComparison.OrdinalIgnoreCase);
    }
}
