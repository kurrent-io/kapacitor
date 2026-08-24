using System.Reactive;
using System.Reactive.Disposables;
using System.Reactive.Disposables.Fluent;
using System.Reactive.Linq;
using Capacitor.App.Services;
using Capacitor.Cli.Core.LocalIpc;
using DynamicData;
using ReactiveUI;

namespace Capacitor.App.ViewModels;

/// The session workspace: header (title/repo/vendor chip) + one Terminal tab, for a single agent
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
/// Exited/Failed stickiness) but Title/RepoLabelText/VendorChip/etc keep identifying the session
/// that just ended instead of reverting to "— · —".
public sealed class WorkspaceViewModel : ReactiveObject {
    const string UnresolvedKind = "unresolved";

    sealed record AgentPresence(AgentStatusDto? Dto, bool SessionEnded);

    public string AgentId { get; }

    readonly ObservableAsPropertyHelper<string> _title;
    public string Title => _title.Value;

    readonly ObservableAsPropertyHelper<string> _repoLabelText;
    public string RepoLabelText => _repoLabelText.Value;

    readonly ObservableAsPropertyHelper<string> _vendorChip;
    public string VendorChip => _vendorChip.Value;

    readonly ObservableAsPropertyHelper<string> _familyDot;
    public string FamilyDot => _familyDot.Value;

    readonly ObservableAsPropertyHelper<bool> _showsTerminalTab;
    public bool ShowsTerminalTab => _showsTerminalTab.Value;

    readonly ObservableAsPropertyHelper<string> _noTerminalNote;
    public string NoTerminalNote => _noTerminalNote.Value;

    readonly ObservableAsPropertyHelper<bool> _sessionEnded;
    public bool SessionEnded => _sessionEnded.Value;

    public TerminalTabViewModel Terminal { get; }

    public ReactiveCommand<Unit, Unit> OpenInWebCommand { get; }
    public ReactiveCommand<Unit, Unit> StopCommand { get; }

    // Task 14 injects this once the window owns navigation between Home and a workspace; null
    // (button hidden/disabled) until then, so THIS view never has to be re-touched to wire it.
    ReactiveCommand<Unit, Unit>? _backCommand;
    public ReactiveCommand<Unit, Unit>? BackCommand {
        get => _backCommand;
        set => this.RaiseAndSetIfChanged(ref _backCommand, value);
    }

    readonly CompositeDisposable _disposables = new();

    // Read by StopCommand at click time -- the DTO's own Kind decides protected-ness
    // (AgentActionService.IsProtectedKind), so Stop must see whatever the LATEST resolved dto
    // says, not a value captured once at construction.
    AgentStatusDto? _latestDto;

    public WorkspaceViewModel(
            string agentId, IDaemonClientService daemon, AgentActionService actions,
            TerminalAttachClientFactory factory, Func<ITerminalSurface> surfaceFactory, TimeProvider time) {
        AgentId = agentId;
        Terminal = new TerminalTabViewModel(agentId, daemon, factory, surfaceFactory, time);

        var presence = daemon.Agents.Connect()
            .ObserveOn(RxSchedulers.MainThreadScheduler)
            .Filter(dto => dto.Id == agentId)
            .Scan(new AgentPresence(null, false), Accumulate)
            .Replay(1)
            .RefCount();

        presence.Select(p => p.Dto).Subscribe(dto => _latestDto = dto).DisposeWith(_disposables);

        _title = presence.Select(p => TitleFor(p.Dto))
            .ToProperty(this, x => x.Title, TitleFor(null))
            .DisposeWith(_disposables);
        _repoLabelText = presence.Select(p => RepoLabel.Leaf(p.Dto?.RepoPath))
            .ToProperty(this, x => x.RepoLabelText, RepoLabel.Leaf(null))
            .DisposeWith(_disposables);
        _vendorChip = presence.Select(p => VendorChipFor(p.Dto))
            .ToProperty(this, x => x.VendorChip, VendorChipFor(null))
            .DisposeWith(_disposables);
        _familyDot = presence.Select(p => p.Dto is null ? "" : HostedHarnessCatalog.EffectiveFamily(p.Dto.HasTerminal, p.Dto.Vendor))
            .ToProperty(this, x => x.FamilyDot, "")
            .DisposeWith(_disposables);
        _showsTerminalTab = presence.Select(p => p.Dto is not null && HostedHarnessCatalog.ShowsTerminal(p.Dto.HasTerminal, p.Dto.Vendor))
            .ToProperty(this, x => x.ShowsTerminalTab, initialValue: false)
            .DisposeWith(_disposables);
        // TerminalTabViewModel.NoteFor is the one source for this wording (post-review rule: bare
        // for a non-ACP family, " — runs over ACP" suffix only when the family is reliably known
        // to be ACP, never a vendor name) -- reused verbatim rather than re-derived here. Blank
        // whenever ShowsTerminalTab is true (or the dto isn't resolved yet): the note replaces the
        // Terminal tab button in the tab strip, so it must never render alongside it.
        _noTerminalNote = presence
            .Select(p => p.Dto is null || HostedHarnessCatalog.ShowsTerminal(p.Dto.HasTerminal, p.Dto.Vendor) ? "" : TerminalTabViewModel.NoteFor(p.Dto))
            .ToProperty(this, x => x.NoTerminalNote, "")
            .DisposeWith(_disposables);
        _sessionEnded = presence.Select(p => p.SessionEnded)
            .ToProperty(this, x => x.SessionEnded, initialValue: false)
            .DisposeWith(_disposables);

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

    static string TitleFor(AgentStatusDto? dto) => $"{RepoLabel.Leaf(dto?.RepoPath)} · {dto?.Vendor ?? "—"}";

    static string VendorChipFor(AgentStatusDto? dto) {
        if (dto is null) return "—";
        return dto.Model is null ? dto.Vendor : $"{dto.Vendor} ({dto.Model})";
    }

    /// Disposes this workspace's own daemon-cache projections, then delegates to
    /// Terminal.TeardownAsync() -- Task 13's tracker calls this once, when the tab closes.
    public Task TeardownAsync() {
        _disposables.Dispose();
        return Terminal.TeardownAsync();
    }
}
