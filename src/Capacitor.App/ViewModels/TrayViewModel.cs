using System.Reactive;
using System.Reactive.Disposables;
using System.Reactive.Disposables.Fluent;
using System.Reactive.Linq;
using Capacitor.App.Services;
using Capacitor.Cli.Core.LocalIpc;
using ReactiveUI;

namespace Capacitor.App.ViewModels;

/// Projects IDaemonClientService.Status/Snapshots + IPauseController.State +
/// AgentActionService.StopsInFlight + IConsentService.PendingCount into the tray's menu model
/// (spec §4, §5, §7, §8). Constructor-scoped, not WhenActivated: the tray icon exists before any
/// window is shown, so MenuModel must be live from construction, not gated on activation.
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

    // The parameter is an agent id; RequestStop's label/kind come from the CURRENT MenuModel
    // (the TrayAgentEntry for this id, consistent with spec §7's one code path for both the tray
    // menu item and the main-window row button), not a captured value, so they reflect whatever
    // is rendered at click time. A missing entry (defensive only — cannot happen from a live
    // menu) falls back to a kind that IsProtectedKind treats as protected, fail-safe rather than
    // silently allowing an unforced stop. Fire-and-forget: AgentActionService never throws and
    // tracks its own in-flight state (StopsInFlight below).
    public ReactiveCommand<string, Unit> StopAgentCommand { get; }
    public ReactiveCommand<string, Unit> OpenInWebCommand  { get; }

    // Injected delegates (spec §5, §9): the tray adapter wires these to real menu items, but the
    // VM owns the commands so tests can assert delegation without a live window/desktop lifetime.
    // No-op defaults so this VM stays constructible before Task 7 supplies the real callbacks.
    public ReactiveCommand<Unit, Unit> OpenMainWindowCommand { get; }
    public ReactiveCommand<Unit, Unit> QuitCommand { get; }

    // The tray menu's "Review pending launches…" target (spec §8); the coordinator itself
    // filters/marshals the raise, so this command is a plain delegate call, same shape as
    // OpenMainWindowCommand/QuitCommand above.
    public ReactiveCommand<Unit, Unit> ReviewPendingCommand { get; }

    // The tray menu's "Install command-line tool…" target (spec §5) — CreateFromTask, not
    // Create, since ShimOfferCoordinator.RunManualInstallAsync is async; a no-op default keeps
    // this VM constructible for every test that predates the shim coordinator.
    public ReactiveCommand<Unit, Unit> InstallShimCommand { get; }

    /// <param name="lifecycleAttention">
    /// spec §6: ILifecycleSurface.Attention repair-affordance text (e.g. a
    /// restore-verification failure). Null (most existing tests, and any caller without a live
    /// lifecycle controller) means this stream never upgrades the tray state — see Build.
    /// </param>
    /// <param name="shimOfferable">
    /// spec §5: ShimOfferCoordinator.Offerable — true while the "Install command-line tool…"
    /// item should show. Null (most existing tests) means the item never shows.
    /// </param>
    public TrayViewModel(
            IDaemonClientService service, IPauseController pause, AgentActionService actions, IConsentService consent,
            Action? openMainWindow = null, Action? quit = null, Action? openReviewPrompts = null,
            IObservable<string?>? lifecycleAttention = null, IObservable<bool>? shimOfferable = null,
            Func<Task>? installShim = null, IPermissionService? permissions = null) {
        _pause = pause;

        TogglePauseCommand = ReactiveCommand.Create<bool>(pause.RequestToggle);
        StopAgentCommand = ReactiveCommand.Create<string>(id => {
            var entry = MenuModel.Agents.FirstOrDefault(a => a.Id == id);
            actions.RequestStop(id, entry?.Label ?? id, entry?.Kind ?? "");
        });
        OpenInWebCommand = ReactiveCommand.Create<string>(actions.OpenInWeb);
        OpenMainWindowCommand = ReactiveCommand.Create(openMainWindow ?? (() => { }));
        QuitCommand = ReactiveCommand.Create(quit ?? (() => { }));
        ReviewPendingCommand = ReactiveCommand.Create(openReviewPrompts ?? (() => { }));
        InstallShimCommand = ReactiveCommand.CreateFromTask(installShim ?? (() => Task.CompletedTask));

        var snapshots = service.Snapshots
            .Select(s => (DaemonStatusDto?)s)
            .StartWith((DaemonStatusDto?)null);

        // consent.PendingCount and IPermissionService.PendingCount are DynamicData's CountChanged,
        // which seeds the current count on subscribe (decompile-verified) — deliberately NOT
        // StartWith(0)'d, which would inject a spurious extra 0 ahead of that seed and could
        // flicker Attention at startup. A null lifecycleAttention (no live lifecycle controller)
        // becomes Observable.Return(null): it emits synchronously on subscribe and then completes,
        // which is exactly the seed CombineLatest needs — Rx.CombineLatest only completes once
        // EVERY source has, so a completed source just freezes at its last (here: only) value
        // forever. A null permissions service (no live IPermissionService) uses the same
        // Observable.Return shape for its count.
        var attention = lifecycleAttention ?? Observable.Return((string?)null);
        var shim = shimOfferable ?? Observable.Return(false);
        var permissionCount = permissions?.PendingCount ?? Observable.Return(0);
        var projected = service.Status.CombineLatest(snapshots, pause.State, actions.StopsInFlight, consent.PendingCount, attention, permissionCount,
            (status, snap, pauseState, inFlight, pending, lifecycleMsg, permissionPending) =>
                Build(service.DaemonName, status, snap, pauseState, inFlight, pending, lifecycleMsg, permissionPending));

        // A second, narrower CombineLatest rather than folding `shim` into the six-source one
        // above: it keeps Build's signature untouched (Build already reads awkwardly with six
        // positional args) and ShimInstallVisible is orthogonal to everything Build computes —
        // it never influences TrayState/Header/Agents/Pause. Same replay-1-shaped reasoning as
        // the sources above applies to `shim`.
        var withShim = projected.CombineLatest(shim, (model, visible) => model with { ShimInstallVisible = visible });

        // Status, snapshots (seeded above), pause.State, consent.PendingCount, attention,
        // permissionCount, and shim are all replay-1-shaped (seed on subscribe), so CombineLatest
        // emits synchronously on subscribe — captured here as the OAPH's initial value so
        // MenuModel is never default(TrayMenuModel) (null) before RxSchedulers.MainThreadScheduler
        // delivers the ObserveOn'd copy below. The synchronous-emission assumption rests on
        // IPauseController.State's, IConsentService.PendingCount's, and
        // IPermissionService.PendingCount's documented replay-on-subscribe contracts, which a
        // future implementation could violate — defended below rather than left to surface as an
        // unexplained NRE on first MenuModel access.
        TrayMenuModel? seed = null;
        using (withShim.Subscribe(v => seed = v)) { }

        _menuModel = withShim
            .ObserveOn(RxSchedulers.MainThreadScheduler)
            .ToProperty(this, x => x.MenuModel, seed ?? throw new InvalidOperationException(
                "IPauseController.State, IConsentService.PendingCount, and IPermissionService.PendingCount must replay a value on subscribe."))
            .DisposeWith(_disposables);

        // Edge-triggered passive refresh (spec §6): fired once on the attach-state transition
        // INTO Connected, not on every snapshot/state emission that follows — so the toggle is
        // usually verified before the FIRST menu open instead of waiting for the adapter's
        // NeedsUpdate kick on the second. DistinctUntilChanged means a later Connected push with
        // no real state change (or a snapshot-only update) is a no-op here; the §6 lane drops a
        // redundant refresh while busy, so this can never race the NeedsUpdate-triggered one.
        service.Status
            .Select(s => s.State)
            .DistinctUntilChanged()
            .Where(state => state == AttachState.Connected)
            .Subscribe(_ => pause.RequestRefresh())
            .DisposeWith(_disposables);
    }

    /// Adapter's NeedsUpdate hook (spec §5) — trivially delegating; the drop-while-busy rule lives
    /// in the IPauseController implementation (spec §6).
    public void RequestPauseRefresh() => _pause.RequestRefresh();

    public void Dispose() => _disposables.Dispose();

    static TrayMenuModel Build(
            string daemonName, AttachStatus status, DaemonStatusDto? snap, PauseState pauseState,
            IReadOnlySet<string> stopsInFlight, int pendingConsent, string? lifecycleAttention, int pendingPermissions) {
        var (state, count) = Project(status, snap);
        var baseState = state; // the row's own verdict (rows 1-10), before either upgrade below

        // Row 11 (spec §8): pending consent or a pending permission request asserts Attention only
        // while Connected — the owner has something waiting. Judged against baseState (not state)
        // so a later independent upgrade can never make this fire retroactively; connection-trouble
        // rows above already left baseState at something other than Idle/Running, so they keep
        // precedence for free, and the running-count badge (count) keeps the agent count regardless.
        var pendingAttention = status.State == AttachState.Connected && (pendingConsent > 0 || pendingPermissions > 0)
            && baseState is TrayState.Idle or TrayState.Running;
        if (pendingAttention) state = TrayState.Attention;

        // spec §6: a lifecycle Attention call (e.g. a restore-verification failure, an orphan
        // label repair affordance) only ever upgrades a GENUINELY fine row (Idle/Running) — judged
        // against baseState, never against the already-Attention state a connection-trouble row
        // (2, 5, 6, 9, 10) produced on its own. A `state is ... or
        // TrayState.Attention` check would let ANY co-occurring Attention row hand its header line to a
        // stale/unrelated lifecycle message, masking exactly the text the comment claimed to
        // protect (e.g. "reconnecting to server" swallowed by a leftover repair-affordance line).
        // When active it also wins the header body over pendingAttention's generic text in
        // HeaderText — both are judged off the same baseState, so either or both can be active.
        var lifecycleAttentionActive = !string.IsNullOrEmpty(lifecycleAttention)
            && baseState is TrayState.Idle or TrayState.Running;
        if (lifecycleAttentionActive) state = TrayState.Attention;

        return new TrayMenuModel(
            state, count,
            HeaderText(daemonName, status, snap, state, count, pendingAttention, pendingConsent,
                lifecycleAttentionActive ? lifecycleAttention : null, pendingPermissions),
            BuildEntries(status, snap, stopsInFlight), BuildPause(status, pauseState), pendingConsent);
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

    static string HeaderText(
            string daemonName, AttachStatus status, DaemonStatusDto? snap, TrayState state, int count,
            bool pendingAttention, int pendingConsent, string? lifecycleAttentionText, int pendingPermissions) {
        if (state == TrayState.Attention && status.State == AttachState.Unreachable && status.Reason == IncompatibleReason)
            return SkewMessage; // no daemon-name prefix

        if (lifecycleAttentionText is not null) return $"{daemonName}: {lifecycleAttentionText}";

        var body = pendingAttention
            ? PendingBody(pendingPermissions, pendingConsent)
            : state switch {
                TrayState.Stopped    => "not running",
                TrayState.Connecting => "connecting…",
                TrayState.Idle       => "connected — no agents",
                TrayState.Running    => $"connected — {count} agent(s) running",
                TrayState.Attention  => AttentionBody(status, snap),
                _                    => "needs attention",
            };
        return $"{daemonName}: {body}";
    }

    static string PendingBody(int permissions, int consent) {
        var parts = new List<string>(2);
        if (permissions > 0) parts.Add($"{permissions} permission request{(permissions == 1 ? "" : "s")} waiting");
        if (consent > 0) parts.Add($"{consent} launch{(consent == 1 ? "" : "es")} awaiting approval");
        return string.Join(", ", parts);
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
            .Select(a => new TrayAgentEntry(a.Id, Label(a), a.Kind, StopEnabled: !stopsInFlight.Contains(a.Id)))
            .ToList();
    }

    static string Label(AgentStatusDto agent) => $"{agent.Kind} · {agent.Vendor} · {RepoLabel.Leaf(agent.RepoPath)}";

    static TrayPauseItem BuildPause(AttachStatus status, PauseState pauseState) {
        var connected = status.State == AttachState.Connected;
        var hasCapability = status.Capabilities?.Contains(ConsentCapability) ?? false;
        var enabled = connected && hasCapability && pauseState.Verified && !pauseState.Busy;
        return new TrayPauseItem(enabled, pauseState.Checked);
    }
}
