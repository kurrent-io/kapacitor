using System.Reactive;
using System.Reactive.Disposables.Fluent;
using System.Reactive.Linq;
using System.Reactive.Subjects;
using Avalonia.Media;
using Capacitor.App.Services;
using ReactiveUI;

namespace Capacitor.App.ViewModels;

/// Which surface owns the window: Home (status block + launcher + cards + Activity) or
/// Sessions (rail | workspace). Orthogonal to CurrentWorkspace, which only means anything in
/// Sessions view.
public enum ShellView { Home, Sessions }

/// Projects IDaemonClientService.Status/Snapshots into display text and drives Start/Reconnect.
/// All display projections are activation-scoped (WhenActivated) — the service outlives this
/// ViewModel and owns its subjects (spec §5), so nothing here disposes the service itself.
/// DEVIATION: StartDaemonCommand/RetryCommand and their canExecute pipelines are built in the
/// CONSTRUCTOR, not inside WhenActivated — commands must exist (and be assertable via
/// CanExecute) independent of window activation; service.Status/service.Snapshots are the
/// service's own long-lived subjects, not resources the VM needs to scope to a window's
/// lifetime. StartVisible/RetryVisible mirror that same constructor scoping (spec: presentation
/// visibility must track the identical state predicate the command's own canExecute uses,
/// independent of activation too). One primary action at a time: Start when the daemon looks
/// down; Reconnect when attaching or skewed (never both).
public sealed class MainWindowViewModel : ReactiveObject, IActivatableViewModel {
    const string IncompatibleReason = "daemon_incompatible";
    const string UnreachableReason  = "daemon_unreachable";

    // Neutral wording: incompatibility classification is a broad heuristic — an unexpected frame
    // can equally mean the APP is the older side — so the UI must not prescribe an upgrade direction.
    // User-facing copy lives on HomeViewModel (launcher banner); Reason mirrors it for tests/tray.

    /// User-facing copy when the daemon isn't attached. Never the wire token (daemon_unreachable).
    internal static string UnreachableMessage => HomeViewModel.DaemonDownNotice;

    /// Shown the moment Start daemon is pressed, before the lifecycle/CLI work returns, so a
    /// click is never silent even when the start action itself has nothing further to say.
    internal const string StartingMessage = "Starting the daemon…";

    /// Shown the moment Reconnect is pressed. Cleared on Connected; replaced if attach stays unreachable.
    internal const string ReconnectingMessage = "Reconnecting…";

    internal const string ReconnectFailedMessage =
        "Could not reconnect. If the daemon isn't running, press Start daemon.";

    internal static bool IsInFlightReconnectMessage(string? message) =>
        message == ReconnectingMessage
        || message == DaemonLifecycleController.AlreadyRunningReconnectStatus;

    // StatusColors (shared with TrayIconRenderer's tray-icon overlay) is hex-only
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

    // Display text for why we're not connected: friendly copy only — never a raw wire token
    // like daemon_unreachable. Null outside Unreachable.
    ObservableAsPropertyHelper<string?>? _reason;
    public string? Reason => _reason?.Value;

    readonly BehaviorSubject<string?> _startMessageChanges = new(null);
    readonly ObservableAsPropertyHelper<bool> _recoveryVisible;
    /// Recovery chrome (banner + Start/Retry): Unreachable attach, or a failed start message still
    /// on screen — so the user always has a next step, not a dead end.
    public bool RecoveryVisible => _recoveryVisible.Value;


    /// The Activity feed (spec §7) — constructed once at the composition root, same instance the
    /// prompt window's onConcluded callback nudges, so this is a plain ctor-injected reference,
    /// not something built here.
    public ActivityViewModel Activity { get; }

    /// The Home surface's launcher and cards — constructed at the composition root over the SAME
    /// IDaemonClientService instance this window uses, never a second daemon connection. Null
    /// only for a caller that doesn't supply one (most existing tests predate Home); HomeView
    /// tolerates a null DataContext, same as any other unbound view.
    public HomeViewModel? Home { get; }

    readonly NavigationGate _navigation;
    readonly Action<Func<Task>> _trackTeardown;
    readonly Func<string, WorkspaceViewModel>? _workspaceFactory;

    WorkspaceViewModel? _currentWorkspace;
    /// null = the Sessions surface shows its placeholder pane; non-null = that session's workspace.
    /// Exactly one workspace at a time, and this VM owns it: every swap starts the outgoing one's
    /// tracked teardown (spec §3).
    public WorkspaceViewModel? CurrentWorkspace {
        get => _currentWorkspace;
        private set => this.RaiseAndSetIfChanged(ref _currentWorkspace, value);
    }

    // Sessions is the app's home for now: the right pane's empty state IS the launcher, and the
    // Home surface stays in the tree but hidden (nothing navigates to it) until it earns its keep.
    ShellView _currentView = ShellView.Sessions;
    public ShellView CurrentView {
        get => _currentView;
        private set {
            this.RaiseAndSetIfChanged(ref _currentView, value);
            this.RaisePropertyChanged(nameof(IsHomeView));
            this.RaisePropertyChanged(nameof(IsSessionsView));
        }
    }
    public bool IsHomeView => CurrentView == ShellView.Home;
    public bool IsSessionsView => CurrentView == ShellView.Sessions;

    public ReactiveCommand<Unit, Unit> ShowHomeCommand { get; }
    public ReactiveCommand<Unit, Unit> ShowSessionsCommand { get; }

    /// The Sessions rail (repo → worktree → session over daemon.Agents) — null for any caller
    /// that predates it, same nullable-seam shape as Home/workspaceFactory above.
    public SessionRailViewModel? Rail { get; }

    /// The active profile's name — the tenant slug (profiles are named after it at sign-in).
    /// "" when absent or when the name is the literal built-in "default" (hiding that segment
    /// so the rail footer does not read as a second "Default" next to connection state).
    public string TenantName { get; }

    /// The launch auto-open's staleness token — see NavigationGate. Read from the SHARED gate, not
    /// a per-window counter, so a window built after shutdown began sees the latch too.
    public int NavigationGeneration => _navigation.Generation;

    /// Clears the open workspace back to the Sessions surface's placeholder pane — the same command
    /// the coordinator's close paths route through.
    public ReactiveCommand<Unit, Unit> CloseWorkspaceCommand { get; }

    string? _startMessage;
    // Start-daemon failure text. Cleared on every new start attempt AND on any transition to
    // Connected (spec §5); set only when a start attempt actually fails.
    public string? StartMessage {
        get => _startMessage;
        private set {
            this.RaiseAndSetIfChanged(ref _startMessage, value);
            _startMessageChanges.OnNext(value);
        }
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
    /// ILifecycleSurface.Status one-liners (e.g. "daemon started, app not yet
    /// attached — retrying", a coded transaction failure) ride the SAME start-message lane
    /// RunStartAsync already uses — one place near the Start button for "why isn't this working",
    /// cleared by the identical Connected-transition rule below. Null (most existing tests, and
    /// any caller without a live lifecycle controller) means this lane never receives anything.
    /// </param>
    /// <param name="lifecycleAttention">
    /// ILifecycleSurface.Attention lines (mutation failures presented by the outcome consumer).
    /// Same StartMessage lane as lifecycleStatus — otherwise a Start daemon click that fails in
    /// the mutation lane only updates the menu-bar icon and the banner stays mute.
    /// </param>
    /// <param name="navigation">
    /// The composition root's app-lifetime NavigationGate. Null builds a private one, so
    /// a caller with no navigation of its own (most existing tests) still gets a working VM — but
    /// only a SHARED gate makes the shutdown latch reach a window built after shutdown began.
    /// </param>
    /// <param name="trackWorkspaceTeardown">
    /// WorkspaceTeardownTracker.Track, as a delegate: this VM only ever registers a teardown, never
    /// drains, and the delegate keeps the drain (a composition-root concern) off its surface. Null
    /// falls back to running the teardown untracked — never to skipping it, or a swap would strand
    /// a live attach.
    /// </param>
    /// <param name="workspaceFactory">
    /// Builds the workspace for an agent id (the production one wires the daemon socket's attach
    /// client and the xterm surface). Null means this window cannot navigate to a workspace at all
    /// — every existing caller that predates workspaces stays on the Home surface.
    /// </param>
    /// <param name="rail">
    /// The Sessions rail. Null means this window has no rail to keep in sync — every existing
    /// caller that predates it keeps working the way it always has.
    /// </param>
    public MainWindowViewModel(
            IDaemonClientService service,
            CancellationToken shutdownToken, ActivityViewModel activity, Func<CancellationToken, Task>? startAction = null,
            IObservable<string?>? lifecycleStatus = null, TimeProvider? time = null, HomeViewModel? home = null,
            NavigationGate? navigation = null, Action<Func<Task>>? trackWorkspaceTeardown = null,
            Func<string, WorkspaceViewModel>? workspaceFactory = null, SessionRailViewModel? rail = null,
            string? tenantName = null, IObservable<string?>? lifecycleAttention = null) {
        _service = service;
        _time = time ?? TimeProvider.System;
        Activity = activity;
        Home = home;
        _navigation = navigation ?? new NavigationGate();
        _trackTeardown = trackWorkspaceTeardown ?? RunUntracked;
        _workspaceFactory = workspaceFactory;
        Rail = rail;
        TenantName = ProfileLabelForRail(tenantName);
        CloseWorkspaceCommand = ReactiveCommand.Create(CloseWorkspace);
        ShowHomeCommand = ReactiveCommand.Create(() => { CurrentView = ShellView.Home; });
        ShowSessionsCommand = ReactiveCommand.Create(() => { CurrentView = ShellView.Sessions; });

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
        // One primary action at a time: Start when nothing is listening (spawn/reattach via the
        // lifecycle); Reconnect when Start is not the right next step (skew, or still connecting).
        var canStart = service.Status
            .Select(s => s.State == AttachState.Unreachable && s.Reason == UnreachableReason)
            .ObserveOn(RxSchedulers.MainThreadScheduler);
        var canRetry = service.Status
            .Select(s =>
                s.State == AttachState.Connecting
                || (s.State == AttachState.Unreachable && s.Reason != UnreachableReason))
            .ObserveOn(RxSchedulers.MainThreadScheduler);

        var start = startAction ?? RunStartAsync;
        StartDaemonCommand = ReactiveCommand.CreateFromTask(
            () => InvokeStartAsync(start, shutdownToken), canStart);
        RetryCommand = ReactiveCommand.CreateFromTask(InvokeRetryAsync, canRetry);

        // Independent subscriptions to the SAME canStart/canRetry state predicates the commands
        // above were built from (service.Status is hot/multicast, so a second subscriber replays
        // the current value same as the first) — visibility that never disagrees with why a button
        // is enabled, without inheriting CanExecute's "not currently executing" hide-while-running
        // behavior. Ctor-scoped for the same reason as the commands themselves.
        _startVisible = canStart.ToProperty(this, x => x.StartVisible, initialValue: false);
        _retryVisible = canRetry.ToProperty(this, x => x.RetryVisible, initialValue: false);
        _recoveryVisible = service.Status
            .CombineLatest(_startMessageChanges, (s, msg) =>
                s.State == AttachState.Unreachable || !string.IsNullOrEmpty(msg))
            .ObserveOn(RxSchedulers.MainThreadScheduler)
            .ToProperty(this, x => x.RecoveryVisible, initialValue: false);

        // Launcher banner owns the chrome; share the same Start/Reconnect commands and start-message
        // lane so the pane never drifts from what MainWindow already drives.
        home?.AttachDaemonRecovery(
            StartDaemonCommand, RetryCommand, canStart, canRetry, _startMessageChanges);

        this.WhenActivated(disposables => {
            var status    = service.Status.ObserveOn(RxSchedulers.MainThreadScheduler);
            var snapshots = service.Snapshots.ObserveOn(RxSchedulers.MainThreadScheduler);

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

            // Reconnect / already-running kicks only reattach. If we land Unreachable again while
            // still showing an in-flight reconnect copy, replace it so the banner does not claim
            // reconnect forever.
            status.Where(s => s.State == AttachState.Unreachable)
                .Subscribe(_ => {
                    if (IsInFlightReconnectMessage(StartMessage))
                        StartMessage = ReconnectFailedMessage;
                })
                .DisposeWith(disposables);

            // Status (start-action one-liners) and Attention (mutation-outcome presentation) both
            // land on StartMessage so the launcher banner is never mute after a Start daemon click.
            void BindStartMessage(IObservable<string?>? source) =>
                source?.ObserveOn(RxSchedulers.MainThreadScheduler)
                    .Where(msg => msg is not null)
                    .Subscribe(msg => StartMessage = msg)
                    .DisposeWith(disposables);

            BindStartMessage(lifecycleStatus);
            BindStartMessage(lifecycleAttention);
        });
    }

    /// Card and rail click: swaps the window to this session's workspace on the Sessions surface,
    /// starting the tracked teardown of whatever it replaces. Refused once shutdown has latched — a
    /// new workspace is a new attach, and quiesce/disposal is already running (spec §3).
    public void OpenSession(string agentId) {
        if (_navigation.ShutdownLatched || _workspaceFactory is null) return;
        CurrentView = ShellView.Sessions;
        // Re-clicking the open session must not tear down and rebuild a live attach.
        if (CurrentWorkspace?.AgentId == agentId) return;

        SwapTo(_workspaceFactory(agentId));
        Rail?.NotifySessionOpened(agentId);
    }

    /// The launch auto-open. `generation` is what the launch captured BEFORE its call: a success
    /// arriving after any navigation (closing the workspace, another session, close-to-hide, the
    /// shutdown latch) opens nothing, rather than attaching an invisible terminal or replacing what
    /// the user opened while the launch was in flight.
    public void OpenSessionIfCurrent(string agentId, int generation) {
        if (generation != _navigation.Generation) return;
        OpenSession(agentId);
    }

    /// The coordinator's close paths. Bumps unconditionally — a close-to-hide with no workspace
    /// open must still retire an in-flight launch's captured generation.
    public void CloseWorkspace() => SwapTo(null);

    /// The first shutdown pass, synchronously: unhook the live workspace and register its teardown
    /// BEFORE the drain seals the tracker, then latch the gate so no later window can open another
    /// one. A workspace that never went through a close or close-to-hide would otherwise register
    /// its teardown after the drain, against already-disposed dependencies (spec §3).
    public void LatchShutdown() {
        var live = CurrentWorkspace;
        CurrentWorkspace = null;
        if (Rail is not null) Rail.SelectedAgentId = null;
        _navigation.Latch();
        if (live is not null) _trackTeardown(live.TeardownAsync);
    }

    void SwapTo(WorkspaceViewModel? next) {
        var outgoing = CurrentWorkspace;
        CurrentWorkspace = next;
        if (Rail is not null) Rail.SelectedAgentId = next?.AgentId;
        _navigation.Bump();
        if (outgoing is not null) _trackTeardown(outgoing.TeardownAsync);
    }

    // A VM built without a tracker (a test, or any caller predating workspaces) must still not
    // strand a live attach: run the teardown and observe its fault exactly like the tracker's own
    // wrapper does. The teardown is bounded by TerminalTabViewModel's own budget, so this cannot
    // run away.
    static void RunUntracked(Func<Task> teardown) {
        try {
            _ = teardown().ContinueWith(
                t => Console.Error.WriteLine($"kcap app: untracked workspace teardown failed: {t.Exception}"),
                CancellationToken.None, TaskContinuationOptions.OnlyOnFaulted, TaskScheduler.Default);
        } catch (Exception ex) {
            Console.Error.WriteLine($"kcap app: untracked workspace teardown failed: {ex}");
        }
    }

    static string? ReasonText(AttachStatus status) => status.State switch {
        AttachState.Unreachable when status.Reason == IncompatibleReason => HomeViewModel.DaemonIncompatibleNotice,
        AttachState.Unreachable => HomeViewModel.DaemonDownNotice,
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

    /// Named profiles stay visible beside Connected; the built-in "default" profile does not —
    /// that word collides with the model/effort "Default" elsewhere in the chrome.
    internal static string ProfileLabelForRail(string? profileName) =>
        string.IsNullOrWhiteSpace(profileName)
        || string.Equals(profileName, "default", StringComparison.OrdinalIgnoreCase)
            ? ""
            : profileName;

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

    async Task InvokeStartAsync(Func<CancellationToken, Task> start, CancellationToken ct) {
        StartMessage = StartingMessage;
        await start(ct);
    }

    async Task InvokeRetryAsync() {
        StartMessage = ReconnectingMessage;
        await _service.RestartLoopAsync();
    }

    async Task RunStartAsync(CancellationToken ct) {
        try {
            var result = await _service.StartDaemonAsync(ct);
            if (!result.Ok) StartMessage = result.Message;
            else StartMessage = "Daemon start requested. Waiting to connect…";
        } catch (OperationCanceledException) {
            // App is quitting: OnShutdownRequested cancelled `ct` while this start was still in
            // flight, and StartDaemonAsync deliberately rethrows OCE for exactly that case —
            // ct abandons the WAIT, not the started daemon. Nothing subscribes to
            // StartDaemonCommand.ThrownExceptions, so letting this escape would have ReactiveUI's
            // default handler reschedule an UnhandledErrorException onto the still-alive
            // dispatcher. The app is exiting — there is nothing left to render.
        }
    }
}
