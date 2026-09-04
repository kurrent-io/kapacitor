using System.Reactive;
using System.Reactive.Disposables;
using System.Reactive.Disposables.Fluent;
using System.Reactive.Linq;
using Capacitor.App.Services;
using Capacitor.Cli.Core;
using Capacitor.Cli.Core.LocalIpc;
using DynamicData;
using ReactiveUI;

namespace Capacitor.App.ViewModels;

public enum WorkspaceTab { Chat, Terminal }

/// The session workspace: header (title/repo/vendor chip) + Chat and Terminal tabs, for one agent
/// id. Constructed once per workspace, like TerminalTabViewModel/HomeViewModel -- ctor-scoped, not
/// WhenActivated -- since the header projections and the Terminal tab must be live from
/// construction, not deferred to a window's activation.
///
/// Every header projection below derives from ONE Scan-based aggregate over a single Filter'd view
/// of daemon.Agents.Connect() (filtered to this agent id, ObserveOn the UI scheduler BEFORE
/// anything touches bound state -- the same rule every other daemon.Agents/Snapshots consumer in
/// this app follows), multicast via Replay(1)/RefCount so the several OAPHs below share ONE
/// subscription to the daemon's cache rather than each opening (and independently re-deriving
/// state from) their own. Replay(1) specifically -- not Publish -- because the 8 downstream
/// subscribers below all attach within the SAME synchronous constructor call: a plain Publish
/// would only deliver an already-fired synchronous replay (e.g. the agent already being in the
/// cache when this VM is built) to whichever subscriber happened to attach first, leaving the rest
/// with no initial value until the NEXT change -- which may never come. Scan's own output already
/// IS the complete current state (dto + sticky-ended), so replaying just the latest one to a late
/// subscriber is exactly correct, unlike replaying a raw changeset to a fresh DynamicData query
/// aggregator would be.
///
/// A removed agent id freezes its LAST known dto rather than blanking the header back to
/// placeholders -- SessionEnded flips (sticky, mirroring TerminalTabViewModel's own
/// Exited/Failed stickiness) but Title/IdentitySubtitle/etc keep identifying the session
/// that just ended instead of reverting to "— · —".
public sealed class WorkspaceViewModel : ReactiveObject {
    const string UnresolvedKind = "unresolved";

    sealed record AgentPresence(AgentStatusDto? Dto, bool SessionEnded);

    public string AgentId { get; }

    readonly ObservableAsPropertyHelper<string> _title;
    public string Title => _title.Value;

    readonly ObservableAsPropertyHelper<string> _repoLabelText;
    /// Checkout path only (`repo / worktree`); tests and Stop labels use this without harness meta.
    public string RepoLabelText => _repoLabelText.Value;

    readonly ObservableAsPropertyHelper<string> _identitySubtitle;
    /// Quiet meta under the title: checkout · transport · vendor (model).
    public string IdentitySubtitle => _identitySubtitle.Value;

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

    WorkspaceTab _activeTab = WorkspaceTab.Chat;
    public WorkspaceTab ActiveTab {
        get => _activeTab;
        private set {
            this.RaiseAndSetIfChanged(ref _activeTab, value);
            this.RaisePropertyChanged(nameof(IsChatActive));
            this.RaisePropertyChanged(nameof(IsTerminalActive));
        }
    }
    public bool IsChatActive => ActiveTab == WorkspaceTab.Chat;
    public bool IsTerminalActive => ActiveTab == WorkspaceTab.Terminal;

    public ReactiveCommand<Unit, Unit> ShowChatCommand { get; }
    public ReactiveCommand<Unit, Unit> ShowTerminalCommand { get; }

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
            Action? requestSignIn = null, IObservable<Unit>? signInCompleted = null) {
        AgentId = agentId;
        Terminal = new TerminalTabViewModel(agentId, daemon, factory, surfaceFactory, time);

        var presence = daemon.Agents.Connect()
            .ObserveOn(RxSchedulers.MainThreadScheduler)
            .Filter(dto => dto.Id == agentId)
            .Scan(new AgentPresence(null, false), Accumulate)
            .Replay(1)
            .RefCount();

        WorkContext = new WorkContextViewModel(presence.Select(p => p.Dto), workContext, time, opener, requestSignIn, signInCompleted);

        presence.Select(p => p.Dto).Subscribe(dto => _latestDto = dto).DisposeWith(_disposables);

        _title = presence.Select(p => TitleFor(p.Dto))
            .ToProperty(this, x => x.Title, TitleFor(null))
            .DisposeWith(_disposables);
        _repoLabelText = presence.Select(p => CheckoutLabelFor(p.Dto))
            .ToProperty(this, x => x.RepoLabelText, CheckoutLabelFor(null))
            .DisposeWith(_disposables);
        _identitySubtitle = presence.Select(p => IdentitySubtitleFor(p.Dto))
            .ToProperty(this, x => x.IdentitySubtitle, IdentitySubtitleFor(null))
            .DisposeWith(_disposables);
        _showsTerminalTab = presence.Select(p => p.Dto is not null && HostedHarnessCatalog.ShowsTerminal(p.Dto.HasTerminal, p.Dto.Vendor))
            .ToProperty(this, x => x.ShowsTerminalTab, initialValue: false)
            .DisposeWith(_disposables);
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
                agentId, daemon, Terminal, TranscriptProjection.For(p.Dto!.Vendor), opener, time, permissions))
            .DisposeWith(_disposables);

        ShowChatCommand = ReactiveCommand.Create(() => { ActiveTab = WorkspaceTab.Chat; });
        ShowTerminalCommand = ReactiveCommand.Create(() => { ActiveTab = WorkspaceTab.Terminal; });
        _disposables.Add(ShowChatCommand);
        _disposables.Add(ShowTerminalCommand);

        OpenInWebCommand = ReactiveCommand.Create(() => actions.OpenInWeb(agentId));
        _disposables.Add(OpenInWebCommand);

        StopCommand = ReactiveCommand.Create(() => {
            var dto = _latestDto;
            // UnresolvedKind fails safe as protected (AgentActionService.IsProtectedKind treats
            // anything other than exactly "agent" as protected) -- the edge case of a Stop click
            // before the first dto ever arrives must never default to the ONE kind that skips
            // the confirm-then-force seam.
            var kind = dto?.Kind ?? UnresolvedKind;
            var label = dto is null ? agentId : $"{dto.Kind} · {dto.Vendor} · {RepoLabel.Leaf(dto.RepoPath)}";
            actions.RequestStop(agentId, label, kind);
        });
        _disposables.Add(StopCommand);
    }

    static AgentPresence Accumulate(AgentPresence state, IChangeSet<AgentStatusDto, string> changes) {
        var dto = state.Dto;
        var ended = state.SessionEnded;
        foreach (var change in changes) {
            if (change.Reason == ChangeReason.Remove) { ended = true; continue; } // dto stays frozen
            dto = change.Current;
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

    /// Checkout, then transport family and vendor/model — one scannable meta line under the title.
    internal static string IdentitySubtitleFor(AgentStatusDto? dto) {
        var checkout = CheckoutLabelFor(dto);
        if (dto is null) return checkout;
        var family = HostedHarnessCatalog.EffectiveFamily(dto.HasTerminal, dto.Vendor);
        var vendor = VendorLabelFor(dto);
        return string.IsNullOrEmpty(family)
            ? $"{checkout} · {vendor}"
            : $"{checkout} · {family} · {vendor}";
    }

    static string VendorLabelFor(AgentStatusDto? dto) {
        if (dto is null) return "—";
        // Empty model is the harness default — name the vendor only; a concrete model rides beside it.
        if (string.IsNullOrWhiteSpace(dto.Model)) return dto.Vendor;
        return $"{dto.Vendor} ({HostedHarnessCatalog.ModelLabelFor(dto.Vendor, dto.Model)})";
    }

    /// Disposes this workspace's own daemon-cache projections, then tears down Chat (if built), the
    /// work-context pane, and Terminal last -- the caller that closes a workspace tab calls this once.
    public async Task TeardownAsync() {
        _disposables.Dispose();
        if (Chat is { } chat) await chat.TeardownAsync();
        await WorkContext.TeardownAsync();
        await Terminal.TeardownAsync();
    }
}
