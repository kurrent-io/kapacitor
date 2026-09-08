using System.Reactive;
using System.Reactive.Disposables;
using System.Reactive.Disposables.Fluent;
using System.Reactive.Linq;
using Capacitor.App.Services;
using Capacitor.Cli.Core;
using Capacitor.Cli.Core.LocalIpc;
using Capacitor.Cli.Core.PullRequests;
using DynamicData;
using ReactiveUI;

namespace Capacitor.App.ViewModels;

public enum WorkspaceTab { Chat, Terminal, PullRequest }

/// Owns the persistent Chat, Terminal and PR surfaces for one agent. Presence is
/// replayed as accumulated state so each subscriber receives an already-cached agent.
/// Ended or removed agents retain their last known session context.
public sealed class WorkspaceViewModel : ReactiveObject {
    const string UnresolvedKind = "unresolved";

    sealed record AgentPresence(AgentStatusDto? Dto, bool SessionEnded);

    public string AgentId { get; }

    readonly ObservableAsPropertyHelper<string> _title;
    public string Title => _title.Value;

    readonly ObservableAsPropertyHelper<string> _repoLabelText;
    /// Checkout under the title (`repo / worktree`); harness/transport live in the work-context pane.
    public string RepoLabelText => _repoLabelText.Value;

    readonly ObservableAsPropertyHelper<bool> _showsTerminalTab;
    public bool ShowsTerminalTab => _showsTerminalTab.Value;

    readonly ObservableAsPropertyHelper<string> _noTerminalNote;
    public string NoTerminalNote => _noTerminalNote.Value;

    readonly ObservableAsPropertyHelper<bool> _sessionEnded;
    public bool SessionEnded => _sessionEnded.Value;

    public TerminalTabViewModel Terminal { get; }

    ChatTabViewModel? _chat;
    /// Built once, on the first dto that passes the PTY gate -- the projection is chosen by the
    /// dto's vendor. Null for a non-PTY session.
    public ChatTabViewModel? Chat {
        get => _chat;
        private set => this.RaiseAndSetIfChanged(ref _chat, value);
    }

    /// The right pane. Fed by the same presence stream as the header, so the daemon cache has one
    /// subscription per workspace, not two.
    public WorkContextViewModel WorkContext { get; }
    public PullRequestContextViewModel? PullRequests { get; }
    public bool ShowsPullRequestTab => PullRequests is not null;

    WorkspaceTab _activeTab = WorkspaceTab.Chat;
    public WorkspaceTab ActiveTab {
        get => _activeTab;
        private set {
            this.RaiseAndSetIfChanged(ref _activeTab, value);
            this.RaisePropertyChanged(nameof(IsChatActive));
            this.RaisePropertyChanged(nameof(IsTerminalActive));
            this.RaisePropertyChanged(nameof(IsPullRequestActive));
            this.RaisePropertyChanged(nameof(ShowsTerminalBanners));
            PullRequests?.SetReaderVisible(value == WorkspaceTab.PullRequest);
        }
    }
    public bool IsChatActive => ActiveTab == WorkspaceTab.Chat;
    public bool IsTerminalActive => ActiveTab == WorkspaceTab.Terminal;
    public bool IsPullRequestActive => ActiveTab == WorkspaceTab.PullRequest;
    public bool ShowsTerminalBanners => !IsPullRequestActive && (IsTerminalActive || !ShowsTerminalTab);

    public ReactiveCommand<Unit, Unit> ShowChatCommand { get; }
    public ReactiveCommand<Unit, Unit> ShowTerminalCommand { get; }
    public ReactiveCommand<Unit, Unit> ShowPullRequestCommand { get; }

    public ReactiveCommand<Unit, Unit> OpenInWebCommand { get; }
    public ReactiveCommand<Unit, Unit> StopCommand { get; }

    readonly CompositeDisposable _disposables = new();

    // Read by StopCommand at click time -- the DTO's own Kind decides protected-ness
    // (AgentActionService.IsProtectedKind), so Stop must see whatever the LATEST resolved dto
    // says, not a value captured once at construction.
    AgentStatusDto? _latestDto;

    public WorkspaceViewModel(
            string agentId, IDaemonClientService daemon, AgentActionService actions,
            TerminalAttachClientFactory factory, Func<ITerminalSurface> surfaceFactory, TimeProvider time,
            IUrlOpener opener, IPermissionService permissions, IWorkContextSource workContext,
            Action? requestSignIn = null, IObservable<Unit>? signInCompleted = null, IPullRequestSource? pullRequests = null, Action? linkGitHub = null) {
        AgentId = agentId;
        Terminal = new TerminalTabViewModel(agentId, daemon, factory, surfaceFactory, time);

        var presence = daemon.Agents.Connect()
            .ObserveOn(RxSchedulers.MainThreadScheduler)
            .Filter(dto => dto.Id == agentId)
            .Scan(new AgentPresence(null, false), Accumulate)
            .Replay(1)
            .RefCount();

        WorkContext = new WorkContextViewModel(presence.Select(p => p.Dto), workContext, time, opener, requestSignIn, signInCompleted);
        PullRequests = pullRequests is null ? null : new PullRequestContextViewModel(presence.Select(p => p.Dto), pullRequests, time, opener,
            () => ActiveTab = WorkspaceTab.PullRequest, requestSignIn, linkGitHub, signInCompleted, () => WorkContext.PrimaryRepository);
        WorkContext.PullRequests = PullRequests;
        daemon.Status.Select(status => status.State).DistinctUntilChanged().Skip(1)
            .Where(state => state == AttachState.Connected).ObserveOn(RxSchedulers.MainThreadScheduler)
            .Subscribe(_ => PullRequests?.Reconnected()).DisposeWith(_disposables);

        presence.Select(p => p.Dto).Subscribe(dto => _latestDto = dto).DisposeWith(_disposables);

        _title = presence.Select(p => TitleFor(p.Dto))
            .ToProperty(this, x => x.Title, TitleFor(null))
            .DisposeWith(_disposables);
        _repoLabelText = presence.Select(p => CheckoutLabelFor(p.Dto))
            .ToProperty(this, x => x.RepoLabelText, CheckoutLabelFor(null))
            .DisposeWith(_disposables);
        _showsTerminalTab = presence.Select(p => p.Dto is not null && HostedHarnessCatalog.ShowsTerminal(p.Dto.HasTerminal, p.Dto.Vendor))
            .ToProperty(this, x => x.ShowsTerminalTab, initialValue: false)
            .DisposeWith(_disposables);
        presence.Subscribe(_ => this.RaisePropertyChanged(nameof(ShowsTerminalBanners))).DisposeWith(_disposables);
        // Blank whenever ShowsTerminalTab is true (or the dto isn't resolved yet): the note
        // replaces the Terminal tab button in the tab strip, so it must never render alongside it.
        _noTerminalNote = presence
            .Select(p => p.Dto is null || HostedHarnessCatalog.ShowsTerminal(p.Dto.HasTerminal, p.Dto.Vendor) ? "" : HostedHarnessCatalog.NoTerminalNote(p.Dto.HasTerminal, p.Dto.Vendor))
            .ToProperty(this, x => x.NoTerminalNote, "")
            .DisposeWith(_disposables);
        _sessionEnded = presence.Select(p => p.SessionEnded)
            .ToProperty(this, x => x.SessionEnded, initialValue: false)
            .DisposeWith(_disposables);

        presence
            .Where(p => p.Dto is not null && HostedHarnessCatalog.ShowsTerminal(p.Dto.HasTerminal, p.Dto.Vendor))
            .Take(1)
            .Subscribe(p => Chat = new ChatTabViewModel(
                agentId, daemon, Terminal, TranscriptChat.For(p.Dto!.Vendor), opener, time, permissions))
            .DisposeWith(_disposables);

        ShowChatCommand = ReactiveCommand.Create(() => { ActiveTab = WorkspaceTab.Chat; });
        ShowTerminalCommand = ReactiveCommand.Create(() => { ActiveTab = WorkspaceTab.Terminal; });
        ShowPullRequestCommand = ReactiveCommand.Create(() => { if (PullRequests is not null) ActiveTab = WorkspaceTab.PullRequest; });
        _disposables.Add(ShowChatCommand);
        _disposables.Add(ShowTerminalCommand);
        _disposables.Add(ShowPullRequestCommand);

        OpenInWebCommand = ReactiveCommand.Create(() => actions.OpenInWeb(agentId));
        _disposables.Add(OpenInWebCommand);

        var canStop = presence.Select(p => !p.SessionEnded)
            .CombineLatest(actions.StopsInFlight, (alive, inFlight) => alive && !inFlight.Contains(agentId));
        StopCommand = ReactiveCommand.Create(() => {
            var dto = _latestDto;
            // UnresolvedKind fails safe as protected (AgentActionService.IsProtectedKind treats
            // anything other than exactly "agent" as protected) -- the edge case of a Stop click
            // before the first dto ever arrives must never default to the ONE kind that skips
            // the confirm-then-force seam.
            var kind = dto?.Kind ?? UnresolvedKind;
            var label = dto is null ? agentId : $"{dto.Kind} · {dto.Vendor} · {RepoLabel.Leaf(dto.RepoPath)}";
            actions.RequestStop(agentId, label, kind);
        }, canStop);
        _disposables.Add(StopCommand);
    }

    static AgentPresence Accumulate(AgentPresence state, IChangeSet<AgentStatusDto, string> changes) {
        var dto = state.Dto;
        var ended = state.SessionEnded;
        foreach (var change in changes) {
            if (change.Reason == ChangeReason.Remove) { ended = true; continue; } // dto stays frozen
            dto = change.Current;
            if (SessionStatusDots.IsTerminal(dto.Status)) ended = true;
        }
        return new AgentPresence(dto, ended);
    }

    /// `repo / checkout`, with the marker for a borrowed reviewer; the repository alone from an
    /// older daemon.
    internal static string CheckoutLabelFor(AgentStatusDto? dto) {
        var repo = RepoLabel.Leaf(dto?.RepoPath);
        if (dto is null || CheckoutLabel.CheckoutPathFor(dto) is not { } checkout) return repo;

        var label = $"{repo} / {CheckoutLabel.Format(checkout, dto.RepoPath ?? "")}";

        return dto.WorkLocation == WorkLocationText.Borrowed ? $"{label} · borrowed" : label;
    }

    static string TitleFor(AgentStatusDto? dto) => dto?.Title ?? RepoLabel.Leaf(dto?.RepoPath);

    /// Disposes this workspace's own daemon-cache projections, then tears down Chat (if built), the
    /// work-context pane, and Terminal last -- the caller that closes a workspace tab calls this once.
    public async Task TeardownAsync() {
        _disposables.Dispose();
        if (PullRequests is { } pullRequests) await pullRequests.TeardownAsync();
        if (Chat is { } chat) await chat.TeardownAsync();
        await WorkContext.TeardownAsync();
        await Terminal.TeardownAsync();
    }
}
