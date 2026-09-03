using System.Globalization;
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
    string? _primaryId;

    public IAvaloniaReadOnlyList<WorkContextPartViewModel> Parts => _parts;
    public IAvaloniaReadOnlyList<string> BlockedBy => _blockedBy;
    public IAvaloniaReadOnlyList<WorkContextLinkViewModel> Links => _links;

    string? _key;
    public string? Key { get => _key; private set => this.RaiseAndSetIfChanged(ref _key, value); }
    string _title = "";
    public string Title { get => _title; private set => this.RaiseAndSetIfChanged(ref _title, value); }
    string? _partOfTitle;
    public string? PartOfTitle { get => _partOfTitle; private set => this.RaiseAndSetIfChanged(ref _partOfTitle, value); }
    string? _cycleNote;
    public string? CycleNote { get => _cycleNote; private set => this.RaiseAndSetIfChanged(ref _cycleNote, value); }

    public string PartsHeader => _parts.Count == 1 ? "1 part" : $"{_parts.Count} parts";
    public bool HasParts => _parts.Count > 0;
    public bool HasBlockers => _blockedBy.Count > 0;

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
        TogglePartsCommand   = ReactiveCommand.Create(() => { PartsExpanded = !PartsExpanded; });
        TogglePeopleCommand  = ReactiveCommand.Create(() => { PeopleExpanded = !PeopleExpanded; });
        ToggleSessionCommand = ReactiveCommand.Create(() => { SessionExpanded = !SessionExpanded; });
        _disposables.Add(TogglePartsCommand);
        _disposables.Add(TogglePeopleCommand);
        _disposables.Add(ToggleSessionCommand);
    }

    void UpdateRequester(AgentStatusDto dto, string vendorLabel) {
        Requester = FirstNonBlank(dto.RequesterDisplay, dto.Requester) ?? "You";
        RequesterRole = $"This session · {vendorLabel}";
        RequesterInitial = new StringInfo(Requester).SubstringByTextElements(0, 1).ToUpperInvariant();
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
        Key = null;
        Title = "";
        ClearTopology();
    }

    void ClearTopology() {
        PartOfTitle = null;
        CycleNote = null;
        _parts.Clear();
        _blockedBy.Clear();
        RaiseCardCounts();
    }

    void RaiseCardCounts() {
        this.RaisePropertyChanged(nameof(PartsHeader));
        this.RaisePropertyChanged(nameof(HasParts));
        this.RaisePropertyChanged(nameof(HasBlockers));
    }

    void ApplyReady(WorkContextRead read) {
        if (read.Primary is null) {
            ClearCard();
            ApplyLinks(read);
            Phase = WorkContextPhase.NoWorkItem;
            IsStale = read.SummaryFailed;
            return;
        }

        var samePrimary = string.Equals(read.Primary.WorkItemId, _primaryId, StringComparison.Ordinal);
        _primaryId = read.Primary.WorkItemId;
        var (key, display) = WorkContextLabel.Split(read.Primary.Label);
        Key = key;
        Title = read.Topology?.Item?.Title is { Length: > 0 } itemTitle ? itemTitle : display;

        if (!read.TopologyFailed && read.Topology is { } topology) ApplyTopology(topology, read.Assignments);
        else if (!samePrimary) ClearTopology();

        ApplyLinks(read);
        Phase = WorkContextPhase.Ready;
        IsStale = read.TopologyFailed || read.SummaryFailed;
    }

    void ApplyTopology(WorkItemTopologyDto topology, IReadOnlyList<SessionWorkItemAssignmentDto> assignments) {
        var attached = new HashSet<string>(assignments.Select(a => a.WorkItemId), StringComparer.Ordinal);
        PartOfTitle = topology.PartOf?.Title;
        _parts.Clear();
        _parts.AddRange(topology.Parts
            .OrderBy(p => p.Ordinal)
            .Select(p => new WorkContextPartViewModel(p.Title, attached.Contains(p.WorkItemId) ? WorkContextPartMark.ThisSession : WorkContextPartMark.Unknown)));
        _blockedBy.Clear();
        _blockedBy.AddRange(topology.BlockedBy.Select(b => b.Title));
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

        _links.Clear();
        _links.AddRange(cards);
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
