using System.Reactive;
using System.Reactive.Disposables;
using System.Reactive.Disposables.Fluent;
using System.Reactive.Linq;
using Capacitor.App.Services;
using Capacitor.Cli.Core.LocalIpc;
using ReactiveUI;

namespace Capacitor.App.ViewModels;

/// Projects IDaemonClientService.Status/Snapshots + IPauseController.State +
/// AgentActionService.StopsInFlight into the tray's menu model (spec §4, §5, §7). Constructor-
/// scoped, not WhenActivated: the tray icon exists before any window is shown, so MenuModel must
/// be live from construction, not gated on activation.
public sealed class TrayViewModel : ReactiveObject, IDisposable {
    const string IncompatibleReason = "daemon_incompatible";
    const string UnreachableReason  = "daemon_unreachable";
    const string ConsentCapability  = "consent/1";

    // Neutral wording (spec §5), duplicated from MainWindowViewModel's SkewMessage: §4.2's
    // incompatibility classification is a broad heuristic — an unexpected frame can equally mean
    // the APP is the older side — so the UI must not prescribe an upgrade direction.
    const string SkewMessage = "app and daemon are incompatible — make sure both are up to date";

    readonly IPauseController _pause;
    readonly CompositeDisposable _disposables = new();

    readonly ObservableAsPropertyHelper<TrayMenuModel> _menuModel;
    public TrayMenuModel MenuModel => _menuModel.Value;

    // Parameter is the desired checked value, frozen by the adapter at menu-rebuild time (spec
    // §6) — the click handler never reads NativeMenuItem.IsChecked. Fire-and-forget by design:
    // PauseController itself serializes (single-flight + one queued slot), so the command need
    // not track in-flight state.
    public ReactiveCommand<bool, Unit> TogglePauseCommand { get; }

    // Both parameters are an agent id; RequestStop's label comes from the CURRENT MenuModel
    // (spec §7 — "the tray label passed to RequestStop is the TrayAgentEntry.Label"), not a
    // captured value, so it reflects whatever is rendered at click time. Fire-and-forget:
    // AgentActionService never throws and tracks its own in-flight state (StopsInFlight below).
    public ReactiveCommand<string, Unit> StopAgentCommand { get; }
    public ReactiveCommand<string, Unit> OpenInWebCommand  { get; }

    public TrayViewModel(IDaemonClientService service, IPauseController pause, AgentActionService actions) {
        _pause = pause;

        TogglePauseCommand = ReactiveCommand.Create<bool>(pause.RequestToggle);
        StopAgentCommand = ReactiveCommand.Create<string>(id =>
            actions.RequestStop(id, MenuModel.Agents.FirstOrDefault(a => a.Id == id)?.Label ?? id));
        OpenInWebCommand = ReactiveCommand.Create<string>(actions.OpenInWeb);

        var snapshots = service.Snapshots
            .Select(s => (DaemonStatusDto?)s)
            .StartWith((DaemonStatusDto?)null);

        var projected = service.Status.CombineLatest(snapshots, pause.State, actions.StopsInFlight,
            (status, snap, pauseState, inFlight) => Build(service.DaemonName, status, snap, pauseState, inFlight));

        // Status, snapshots (seeded above), and pause.State are all replay-1, so CombineLatest
        // emits synchronously on subscribe — captured here as the OAPH's initial value so
        // MenuModel is never default(TrayMenuModel) (null) before RxSchedulers.MainThreadScheduler
        // delivers the ObserveOn'd copy below. The synchronous-emission assumption rests on
        // IPauseController.State's documented replay-1 contract, which a future implementation
        // could violate — defended below rather than left to surface as an unexplained NRE on
        // first MenuModel access.
        TrayMenuModel? seed = null;
        using (projected.Subscribe(v => seed = v)) { }

        _menuModel = projected
            .ObserveOn(RxSchedulers.MainThreadScheduler)
            .ToProperty(this, x => x.MenuModel, seed ?? throw new InvalidOperationException(
                "IPauseController.State must replay a value on subscribe (contract in IPauseController)."))
            .DisposeWith(_disposables);
    }

    /// Adapter's Opening hook (spec §5) — trivially delegating; the drop-while-busy rule lives in
    /// the IPauseController implementation (spec §6).
    public void RequestPauseRefresh() => _pause.RequestRefresh();

    public void Dispose() => _disposables.Dispose();

    static TrayMenuModel Build(
            string daemonName, AttachStatus status, DaemonStatusDto? snap, PauseState pauseState,
            IReadOnlySet<string> stopsInFlight) {
        var (state, count) = Project(status, snap);
        return new TrayMenuModel(
            state, count, HeaderText(daemonName, status, snap, state, count),
            BuildEntries(status, snap, stopsInFlight), BuildPause(status, pauseState));
    }

    /// Pure ten-row mapping (spec §4), precedence top-down.
    internal static (TrayState State, int Count) Project(AttachStatus status, DaemonStatusDto? snap) {
        if (status.State == AttachState.Unreachable) {
            // Row 1: daemon_unreachable → Stopped. Rows 2 and 10 (daemon_incompatible and any
            // other reason) collapse to Attention — the header distinguishes them (HeaderText).
            return status.Reason == UnreachableReason ? (TrayState.Stopped, 0) : (TrayState.Attention, 0);
        }

        if (status.State == AttachState.Connecting) return (TrayState.Connecting, 0); // row 3

        // Connected. Defensive only (cannot happen per the client pin): no snapshot yet.
        if (snap is null) return (TrayState.Connecting, 0);

        var connection = snap.Daemon.Connection;
        if (connection == "connecting") return (TrayState.Connecting, 0);              // row 4
        if (connection is "reconnecting" or "disconnected") return (TrayState.Attention, 0); // row 5

        if (connection == "connected") {
            var active = snap.Daemon.ActiveAgents;
            return active switch {
                < 0 => (TrayState.Attention, 0),        // row 6 — malformed count
                0   => (TrayState.Idle, 0),              // row 7
                _   => (TrayState.Running, active),      // row 8
            };
        }

        return (TrayState.Attention, 0); // row 9 — unrecognized connection value
    }

    static string HeaderText(string daemonName, AttachStatus status, DaemonStatusDto? snap, TrayState state, int count) {
        if (state == TrayState.Attention && status.State == AttachState.Unreachable && status.Reason == IncompatibleReason)
            return SkewMessage; // no daemon-name prefix

        var body = state switch {
            TrayState.Stopped    => "not running",
            TrayState.Connecting => "connecting…",
            TrayState.Idle       => "connected — no agents",
            TrayState.Running    => $"connected — {count} agent(s) running",
            TrayState.Attention  => AttentionBody(status, snap),
            _                    => "needs attention",
        };
        return $"{daemonName}: {body}";
    }

    // Rows 6 and 9 (connected, malformed count / unrecognized connection) and row 10 (unreachable,
    // unrecognized reason) share the neutral fallback; rows 5's two connection values get their
    // own copy.
    static string AttentionBody(AttachStatus status, DaemonStatusDto? snap) {
        if (status.State == AttachState.Connected && snap is not null) {
            return snap.Daemon.Connection switch {
                "reconnecting" => "reconnecting to server",
                "disconnected" => "disconnected from server",
                _              => "needs attention",
            };
        }
        return "needs attention";
    }

    // Only while Connected (spec §5) — the daemon's own upstream link status (rows 5–6, 9) does
    // not hide the entries, since the snapshot Agents array is still the app's local truth.
    static IReadOnlyList<TrayAgentEntry> BuildEntries(AttachStatus status, DaemonStatusDto? snap, IReadOnlySet<string> stopsInFlight) {
        if (status.State != AttachState.Connected || snap is null) return [];

        return snap.Agents
            .Where(a => a.Status is "Starting" or "Running")
            .OrderBy(a => a.CreatedAt)
            .ThenBy(a => a.Id, StringComparer.Ordinal)
            .Select(a => new TrayAgentEntry(a.Id, Label(a), StopEnabled: !stopsInFlight.Contains(a.Id)))
            .ToList();
    }

    static string Label(AgentStatusDto agent) {
        var repoLeaf = agent.RepoPath is null
            ? "—"
            : Path.GetFileName(Path.TrimEndingDirectorySeparator(agent.RepoPath));
        return $"{agent.Kind} · {agent.Vendor} · {repoLeaf}";
    }

    static TrayPauseItem BuildPause(AttachStatus status, PauseState pauseState) {
        var connected = status.State == AttachState.Connected;
        var hasCapability = status.Capabilities?.Contains(ConsentCapability) ?? false;
        var enabled = connected && hasCapability && pauseState.Verified && !pauseState.Busy;
        return new TrayPauseItem(enabled, pauseState.Checked);
    }
}
