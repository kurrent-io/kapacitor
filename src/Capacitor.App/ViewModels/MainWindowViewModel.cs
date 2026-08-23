using System.Collections.ObjectModel;
using System.Reactive;
using System.Reactive.Disposables.Fluent;
using System.Reactive.Linq;
using Avalonia.Media;
using Capacitor.App.Services;
using DynamicData;
using DynamicData.Binding;
using ReactiveUI;

namespace Capacitor.App.ViewModels;

/// Projects IDaemonClientService.Status/Snapshots into display text and drives Start/Retry.
/// All display projections are activation-scoped (WhenActivated) — the service outlives this
/// ViewModel and owns its subjects (spec §5), so nothing here disposes the service itself.
/// DEVIATION: StartDaemonCommand/RetryCommand and their canExecute pipelines are built in the
/// CONSTRUCTOR, not inside WhenActivated — commands must exist (and be assertable via
/// CanExecute) independent of window activation; service.Status/service.Snapshots are the
/// service's own long-lived subjects, not resources the VM needs to scope to a window's
/// lifetime. StartVisible/RetryVisible mirror that same constructor scoping (spec: presentation
/// visibility must track the identical state predicate the command's own canExecute uses,
/// independent of activation too).
public sealed class MainWindowViewModel : ReactiveObject, IActivatableViewModel {
    const string IncompatibleReason = "daemon_incompatible";
    const string UnreachableReason  = "daemon_unreachable";

    // Neutral wording (spec §5): §4.2's incompatibility classification is a broad heuristic —
    // an unexpected frame can equally mean the APP is the older side — so the UI must not
    // prescribe an upgrade direction.
    const string SkewMessage = "app and daemon are incompatible — make sure both are up to date";

    // StatusColors (shared with TrayIconRenderer's tray-icon overlay, spec §4) is hex-only
    // constants (plain strings, not Brush instances). A Brush is an AvaloniaObject with UI-thread
    // affinity enforced the moment the renderer references it; caching one as a shared
    // `static readonly` field would tie its affinity to whichever thread happens to trigger this
    // type's static initializer FIRST (e.g. a plain unit test calling a static helper off the UI
    // thread) and then poison every later render that reuses the same cached instance. DotBrush
    // below constructs a fresh instance per call instead — cheap, and always on whatever thread
    // the caller is on.
    static IBrush DotBrush(string hex) => new SolidColorBrush(Color.Parse(hex));

    readonly IDaemonClientService _service;

    public ViewModelActivator Activator { get; } = new();

    ObservableAsPropertyHelper<string>? _daemonName;
    public string DaemonName => _daemonName?.Value ?? "";

    ObservableAsPropertyHelper<string>? _daemonVersion;
    public string DaemonVersion => _daemonVersion?.Value ?? "";

    // SEMVER-only projection of DaemonVersion (everything from the first '+' is build metadata —
    // never meaningful to a human glancing at the status line); the untruncated value still lives
    // on DaemonVersion for the version TextBlock's ToolTip.Tip.
    ObservableAsPropertyHelper<string>? _versionDisplay;
    public string VersionDisplay => _versionDisplay?.Value ?? "";

    ObservableAsPropertyHelper<string>? _serverUrl;
    public string ServerUrl => _serverUrl?.Value ?? "";

    // The daemon's OWN upstream connection to the Capacitor server (DaemonInfoDto.Connection):
    // connected|connecting|reconnecting|disconnected. Distinct from State/Reason below, which
    // are this app's local attach status to the daemon.
    ObservableAsPropertyHelper<string>? _connectionText;
    public string ConnectionText => _connectionText?.Value ?? "";

    // Single-word presentation of the OVERALL connection situation (local attach State first,
    // falling back to the daemon's own upstream Connection only once State is Connected — see
    // ConnectionDisplayFor). Capitalized, in-progress words get a trailing ellipsis ("Connecting…").
    ObservableAsPropertyHelper<string>? _connectionDisplay;
    public string ConnectionDisplay => _connectionDisplay?.Value ?? "";

    // Status-dot color for ConnectionDisplay's same bucket — kept as a single source of truth so
    // the dot and the word can never disagree.
    ObservableAsPropertyHelper<IBrush>? _statusDotBrush;
    public IBrush StatusDotBrush => _statusDotBrush?.Value ?? DotBrush(StatusColors.Unavailable);

    // "n of m agents" only while Connected (spec §1.5: active_agents is a display count, never
    // a free-slots/launch-capacity claim) — "—" otherwise, even though the last-known snapshot
    // (and the Agents cache) is retained by the service across disconnects.
    ObservableAsPropertyHelper<string>? _agentCountText;
    public string AgentCountText => _agentCountText?.Value ?? "—";

    ObservableAsPropertyHelper<AttachState>? _state;
    public AttachState State => _state?.Value ?? AttachState.Connecting;

    // Display text for why we're not connected: the raw wire reason for daemon_unreachable, but
    // the NEUTRAL skew message (never an upgrade-direction verdict) for daemon_incompatible; null
    // outside Unreachable.
    ObservableAsPropertyHelper<string?>? _reason;
    public string? Reason => _reason?.Value;

    static readonly IComparer<AgentRowViewModel> RowComparer = Comparer<AgentRowViewModel>.Create((a, b) => {
        var byCreated = a.CreatedAt.CompareTo(b.CreatedAt);
        return byCreated != 0 ? byCreated : string.CompareOrdinal(a.Id, b.Id);
    });

    // ONE stable collection, created once here and never replaced: WhenActivated re-runs on
    // every activation (hide-to-tray/reopen included), and Agents is a plain get-only property
    // with no change notification — swapping the bound INSTANCE on each activation (the prior
    // `SortAndBind(out _agents, ...)` shape) would leave the view's ItemsControl bound to a dead
    // collection forever. SortAndBind's IList-targeting overload mutates THIS instance in place
    // instead. Spec §8: rows persist across disconnects (the underlying SourceCache is retained
    // by the service) — GridEnabled below is what disables actions and dims the XAML, never a
    // local removal of rows.
    readonly ObservableCollectionExtended<AgentRowViewModel> _agentsSource = new();
    public ReadOnlyObservableCollection<AgentRowViewModel> Agents { get; }

    /// The Activity tab (spec §7) — constructed once at the composition root, same instance the
    /// prompt window's onConcluded callback nudges, so this is a plain ctor-injected reference,
    /// not something built here.
    public ActivityViewModel Activity { get; }

    /// The Home tab (Task 6, AI-2194) — constructed at the composition root over the SAME
    /// IDaemonClientService instance this window uses, never a second daemon connection. Null
    /// only for a caller that doesn't supply one (most existing tests predate Home); HomeView
    /// tolerates a null DataContext, same as any other unbound view.
    public HomeViewModel? Home { get; }

    ObservableAsPropertyHelper<bool>? _gridEnabled;
    public bool GridEnabled => _gridEnabled?.Value ?? false;

    string? _startMessage;
    // Start-daemon failure text. Cleared on every new start attempt AND on any transition to
    // Connected (spec §5); set only when a start attempt actually fails.
    public string? StartMessage {
        get => _startMessage;
        private set => this.RaiseAndSetIfChanged(ref _startMessage, value);
    }

    public ReactiveCommand<Unit, Unit> StartDaemonCommand { get; }
    public ReactiveCommand<Unit, Unit> RetryCommand { get; }

    // Button IsVisible projections (spec: "shows ONLY when its action is meaningful"). Deliberately
    // NOT ReactiveCommand.CanExecute — that ANDs in "not currently executing", which would hide the
    // button mid-attempt instead of just disabling it. These track the exact same state predicate
    // (canStart/canRetry below) the commands' own canExecute pipelines use, ctor-scoped for the
    // same reason those pipelines are (see class doc comment).
    readonly ObservableAsPropertyHelper<bool> _startVisible;
    public bool StartVisible => _startVisible.Value;

    readonly ObservableAsPropertyHelper<bool> _retryVisible;
    public bool RetryVisible => _retryVisible.Value;

    readonly TimeProvider _time;

    /// <param name="shutdownToken">
    /// Abandons StartDaemonAsync's WAIT (never the spawned daemon) on app shutdown. MUST be a
    /// token linked to the app lifetime — never CancellationToken.None (Task 4 carry-note: an
    /// unbounded wait would survive app exit).
    /// </param>
    /// <param name="startAction">
    /// spec §4.4: the service-aware Start action (DaemonLifecycleController.StartActionAsync).
    /// Null falls back to the plain detached `StartDaemonAsync` RunStartAsync always used —
    /// preserved so a caller without a live controller (most existing tests) keeps today's
    /// behavior verbatim.
    /// </param>
    /// <param name="lifecycleStatus">
    /// spec §6: ILifecycleSurface.Status one-liners (e.g. "daemon started, app not yet
    /// attached — retrying", a coded transaction failure) ride the SAME start-message lane
    /// RunStartAsync already uses — one place near the Start button for "why isn't this working",
    /// cleared by the identical Connected-transition rule below. Null (most existing tests, and
    /// any caller without a live lifecycle controller) means this lane never receives anything.
    /// </param>
    public MainWindowViewModel(
            IDaemonClientService service, AgentActionService actions, ITicker ticker,
            CancellationToken shutdownToken, ActivityViewModel activity, Func<CancellationToken, Task>? startAction = null,
            IObservable<string?>? lifecycleStatus = null, TimeProvider? time = null, HomeViewModel? home = null) {
        _service = service;
        _time = time ?? TimeProvider.System;
        Agents = new ReadOnlyObservableCollection<AgentRowViewModel>(_agentsSource);
        Activity = activity;
        Home = home;

        // ReactiveCommand's own CanExecute observable already ANDs the supplied canExecute with
        // "not currently executing" (confirmed against the installed ReactiveUI 23.2.28 API
        // docs) — no separate in-flight flag is needed to satisfy "Start also disabled while a
        // start is in flight".
        //
        // ReactiveCommand does NOT reschedule the SUPPLIED canExecute onto outputScheduler
        // (decompile-verified: only IsExecuting/ThrownExceptions ride outputScheduler) — without
        // an explicit ObserveOn here, a Status event arriving on a background thread (the
        // service's pump thread) would carry CanExecuteChanged, and therefore a bound Button's
        // IsEnabled write, onto that same background thread, tripping Avalonia's dispatcher
        // thread-affinity check. These stay constructor-scoped (not inside WhenActivated) since
        // commands must exist and be assertable pre-activation — see the class doc comment.
        var canStart = service.Status
            .Select(s => s.State == AttachState.Unreachable && s.Reason == UnreachableReason)
            .ObserveOn(RxSchedulers.MainThreadScheduler);
        var canRetry = service.Status
            .Select(s => s.State != AttachState.Connected)
            .ObserveOn(RxSchedulers.MainThreadScheduler);

        var start = startAction ?? RunStartAsync;
        StartDaemonCommand = ReactiveCommand.CreateFromTask(() => start(shutdownToken), canStart);
        RetryCommand        = ReactiveCommand.CreateFromTask(service.RestartLoopAsync, canRetry);

        // Independent subscriptions to the SAME canStart/canRetry state predicates the commands
        // above were built from (service.Status is hot/multicast, so a second subscriber replays
        // the current value same as the first) — visibility that never disagrees with why a button
        // is enabled, without inheriting CanExecute's "not currently executing" hide-while-running
        // behavior. Ctor-scoped for the same reason as the commands themselves.
        _startVisible = canStart.ToProperty(this, x => x.StartVisible, initialValue: false);
        _retryVisible = canRetry.ToProperty(this, x => x.RetryVisible, initialValue: false);

        this.WhenActivated(disposables => {
            var status    = service.Status.ObserveOn(RxSchedulers.MainThreadScheduler);
            var snapshots = service.Snapshots.ObserveOn(RxSchedulers.MainThreadScheduler);
            var connected = status.Select(s => s.State == AttachState.Connected);
            // Pre-scheduled here (not inside AgentRowViewModel) so every row's ActionsEnabled
            // OAPH only ever observes on the UI thread — StopsInFlight is a plain BehaviorSubject
            // that AgentActionService pushes to from a background Task.Run, same class of bug the
            // canStart/canRetry comment above documents.
            var stopsInFlight = actions.StopsInFlight.ObserveOn(RxSchedulers.MainThreadScheduler);

            _daemonName = snapshots.Select(s => s.Daemon.Name)
                .ToProperty(this, x => x.DaemonName, "")
                .DisposeWith(disposables);

            _daemonVersion = snapshots.Select(s => s.Daemon.Version)
                .ToProperty(this, x => x.DaemonVersion, "")
                .DisposeWith(disposables);

            _versionDisplay = snapshots.Select(s => StripBuildMetadata(s.Daemon.Version))
                .ToProperty(this, x => x.VersionDisplay, "")
                .DisposeWith(disposables);

            _serverUrl = snapshots.Select(s => s.Daemon.ServerUrl)
                .ToProperty(this, x => x.ServerUrl, "")
                .DisposeWith(disposables);

            _connectionText = snapshots.Select(s => s.Daemon.Connection)
                .ToProperty(this, x => x.ConnectionText, "")
                .DisposeWith(disposables);

            // Seeded with "" so this fires even before the FIRST snapshot ever arrives (a daemon
            // never previously connected has nothing in Snapshots yet) — ConnectionDisplayFor/
            // StatusDotFor only read the daemon-connection word once status.State is Connected,
            // where DaemonClientService's ordering guarantee (snapshot applied before the Connected
            // transition, see its own comment) means a real value is always already there by then.
            var daemonConnection = snapshots.Select(s => s.Daemon.Connection).StartWith("");

            _connectionDisplay = status.CombineLatest(daemonConnection, ConnectionDisplayFor)
                .ToProperty(this, x => x.ConnectionDisplay, "")
                .DisposeWith(disposables);

            _statusDotBrush = status.CombineLatest(daemonConnection, StatusDotFor)
                .ToProperty(this, x => x.StatusDotBrush, DotBrush(StatusColors.Unavailable))
                .DisposeWith(disposables);

            _agentCountText = status.CombineLatest(snapshots, (st, snap) => (st, snap))
                .Select(t => t.st.State == AttachState.Connected
                    ? $"{t.snap.Daemon.ActiveAgents} of {t.snap.Daemon.MaxAgents} agents"
                    : "—")
                .ToProperty(this, x => x.AgentCountText, "—")
                .DisposeWith(disposables);

            _state = status.Select(s => s.State)
                .ToProperty(this, x => x.State, AttachState.Connecting)
                .DisposeWith(disposables);

            _reason = status.Select(ReasonText)
                .ToProperty(this, x => x.Reason, (string?)null)
                .DisposeWith(disposables);

            status.Where(s => s.State == AttachState.Connected)
                .Subscribe(_ => StartMessage = null)
                .DisposeWith(disposables);

            lifecycleStatus?.ObserveOn(RxSchedulers.MainThreadScheduler)
                .Where(msg => msg is not null)
                .Subscribe(msg => StartMessage = msg)
                .DisposeWith(disposables);

            _gridEnabled = connected
                .ToProperty(this, x => x.GridEnabled, initialValue: false)
                .DisposeWith(disposables);

            // _agentsSource is reused across activations (see its field comment) but SortAndBind
            // below starts a brand-new pipeline — and DynamicData internal Cache<> — on every
            // activation, so without this Clear a reactivation's initial replay would INSERT a
            // second copy of every currently-cached row alongside whatever was left over (frozen,
            // already-disposed) from the previous activation, since SortAndBind only clears its
            // target on a reset, not on ordinary Add changes.
            _agentsSource.Clear();

            // Connect -> Transform to row VMs -> DisposeMany (disposes a row the instant Transform
            // replaces or removes it — AgentRowViewModel's OAPHs otherwise stay subscribed to the
            // shared ticker/stopsInFlight forever, since Transform recreates a row on every dto
            // revision rather than updating one in place) -> ObserveOn BEFORE the operator that
            // mutates the bound collection (SortAndBind counts as "Bind" here — DynamicData
            // requires marshaling onto the UI thread before that mutation, not after) ->
            // SortAndBind (spec §8: CreatedAt asc, Id ordinal tiebreak), targeting the stable
            // _agentsSource in place rather than the out-param overload that would allocate a
            // fresh collection every activation. EditDiff removals flow through as Remove changes,
            // which is how a stopped agent's row disappears (spec §7 — no local removal on stop,
            // only the next snapshot's absence) and DisposeMany's Remove path is what cleans up
            // its subscriptions. Disposing this Subscribe() (window deactivation) also disposes
            // whatever rows are still live at that point — DisposeMany disposes its full current
            // set on teardown, not just on per-item Remove/Update.
            service.Agents.Connect()
                .Transform(dto => new AgentRowViewModel(dto, actions, ticker.Ticks, _time, connected, stopsInFlight))
                .DisposeMany()
                .ObserveOn(RxSchedulers.MainThreadScheduler)
                .SortAndBind(_agentsSource, RowComparer)
                .Subscribe()
                .DisposeWith(disposables);
        });
    }

    static string? ReasonText(AttachStatus status) => status.State switch {
        AttachState.Unreachable when status.Reason == IncompatibleReason => SkewMessage,
        AttachState.Unreachable => status.Reason,
        _ => null,
    };

    // SEMVER-only: everything from the first '+' is build metadata (e.g. "1.2.3+a1b2c3"), never
    // meaningful on a compact status line. Null/empty-safe — returns the input unchanged.
    internal static string StripBuildMetadata(string? version) {
        if (string.IsNullOrEmpty(version)) return version ?? "";
        var plus = version.IndexOf('+');
        return plus < 0 ? version : version[..plus];
    }

    static string Capitalize(string word) => word.Length == 0 ? word : char.ToUpperInvariant(word[0]) + word[1..];

    // Single word for the merged status line. Local attach State is checked FIRST — the daemon's
    // own upstream Connection word is only meaningful once State is Connected (see the
    // daemonConnection seam comment above); an Unreachable/Connecting attach state always wins
    // regardless of whatever Connection word a stale retained snapshot might carry.
    internal static string ConnectionDisplayFor(AttachStatus status, string daemonConnection) {
        if (status.State == AttachState.Connecting) return "Connecting…";
        if (status.State == AttachState.Unreachable)
            return status.Reason == IncompatibleReason ? "Incompatible" : "Unreachable";

        var word = Capitalize(daemonConnection);
        return daemonConnection is "connecting" or "reconnecting" ? word + "…" : word;
    }

    // Same bucketing as ConnectionDisplayFor, kept as a parallel switch (not derived from the text)
    // so a future wording tweak there can never silently detune the dot's color.
    internal static IBrush StatusDotFor(AttachStatus status, string daemonConnection) {
        if (status.State == AttachState.Connecting) return DotBrush(StatusColors.InProgress);
        if (status.State == AttachState.Unreachable)
            return status.Reason == IncompatibleReason ? DotBrush(StatusColors.Disrupted) : DotBrush(StatusColors.Unavailable);

        return daemonConnection switch {
            "connected" => DotBrush(StatusColors.Connected),
            "connecting" or "reconnecting" => DotBrush(StatusColors.InProgress),
            "disconnected" => DotBrush(StatusColors.Disrupted),
            _ => DotBrush(StatusColors.Unavailable),
        };
    }

    async Task RunStartAsync(CancellationToken ct) {
        StartMessage = null; // clear on every new attempt
        try {
            var result = await _service.StartDaemonAsync(ct);
            if (!result.Ok) StartMessage = result.Message;
        } catch (OperationCanceledException) {
            // App is quitting: OnShutdownRequested cancelled `ct` while this start was still in
            // flight, and StartDaemonAsync deliberately rethrows OCE for exactly that case (spec
            // §5 — ct abandons the WAIT, not the started daemon). Nothing subscribes to
            // StartDaemonCommand.ThrownExceptions, so letting this escape would have ReactiveUI's
            // default handler reschedule an UnhandledErrorException onto the still-alive
            // dispatcher. The app is exiting — there is nothing left to render.
        }
    }
}
