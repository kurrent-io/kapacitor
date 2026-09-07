using System.Collections.ObjectModel;
using System.Reactive;
using System.Reactive.Disposables;
using System.Reactive.Disposables.Fluent;
using System.Reactive.Linq;
using System.Reactive.Subjects;
using Capacitor.App.Services;
using Capacitor.Cli.Core;
using Capacitor.Cli.Core.Harness.Claude;
using Capacitor.Remote.Models;
using DynamicData;
using DynamicData.Binding;
using ReactiveUI;

namespace Capacitor.App.ViewModels;

/// One entry of the repository chip's menu. Vendor is the remembered harness for RepoPath, or
/// HomeViewModel.DefaultVendor when none was ever chosen there; Selected marks the entry that
/// matches SelectedRepoPath under HomeViewModel's own path comparison.
public sealed record RepositoryOption(string RepoPath, string Vendor, bool Selected);

/// One entry of the launcher's machine chip: the local daemon, or one of the viewer's own remote
/// daemons — name-based launch routing is only defined within one owner, so a daemon owned by
/// someone else is never surfaced as an option. RepoPaths/SupportedVendors come from DaemonInfo
/// verbatim for a remote machine; the local entry's come from the existing repo flow.
public sealed record MachineOption(
    string DaemonName, bool IsLocal, bool Connected, string? Platform,
    string[] RepoPaths, string[]? SupportedVendors, bool Selected);

/// Whether a launch can reach a daemon right now, merged from the local attach state and the
/// daemon's own upstream connection word — the same two inputs the footer's status line reads.
internal enum LaunchAvailability { Ready, Pending, DaemonUnavailable, ServerDisconnected }

/// The Home tab's view-model: repository + harness picker, a free-text goal, and
/// the Start action that launches a session through ILaunchClient. Constructed once, like
/// TrayViewModel/ActivityViewModel — not gated behind IActivatableViewModel — since Harnesses and
/// Sessions must be live from construction, not deferred to a window's activation. Snapshots and
/// Agents are mutated on the daemon client's own background thread (same as
/// MainWindowViewModel/ConsentPromptViewModel), so both projections below ObserveOn
/// RxSchedulers.MainThreadScheduler BEFORE the operator that touches bound state — the
/// ItemsControl binding must never see a mutation off the UI thread.
public sealed class HomeViewModel : ReactiveObject, IDisposable {
    /// A repository with no remembered choice falls back to this — never to whatever vendor was
    /// selected for a DIFFERENT repository, which would leak a preference across repositories.
    public const string DefaultVendor = "claude";

    public const string DefaultPermissionMode = ClaudePermissionModes.Manual;

    /// Reserved key for the not-yet-in-a-repository target. It is a normal AppState.HarnessByRepo
    /// key, so it round-trips and keeps its own remembered harness like any real repo path —
    /// but LAUNCHING it is not supported yet: AgentOrchestrator rejects a launch whose repo path
    /// fails Directory.Exists, which "" does. Until the daemon accepts a repo-less launch this is
    /// a storage key only.
    public const string ScratchRepoPath = "";

    /// A launch that started but handed back an id nothing can open. The session is real — open
    /// it from the session list rather than treating this as a launch failure.
    public const string UnusableIdMessage = "Launched, but the session ID was unusable. Open it from the session list.";

    /// StartAsync's own re-check refuses this even if a caller invoked ReactiveCommand.Execute()
    /// straight past CanExecute — the wire request is never built for a machine that failed the
    /// ownership/connected check at the moment of launch, whatever the UI-affordance state said.
    public const string MachineUnavailableMessage = "This machine is no longer available. Choose a different one.";

    internal const string ConnectingNotice     = "Connecting to the server…";
    internal const string FinishingSignInNotice =
        "Finishing sign-in. Reconnecting to the server…";
    internal const string DaemonDownNotice     =
        "The daemon isn't running. Press Start daemon to launch sessions.";
    internal const string DaemonIncompatibleNotice =
        "App and daemon are incompatible. Update both to matching versions, then press Reconnect.";
    internal const string ServerLostNotice     = "Not connected to the server. Sign in again to reconnect.";
    internal const string SignInExpiredNotice  = "Your sign-in has expired. Sign in again.";

    const string IncompatibleReason = "daemon_incompatible";

    readonly IDaemonClientService _daemon;
    readonly IAppStateStore _state;
    readonly ILaunchClient _launch;
    readonly Func<Task<string[]>> _knownRepos;
    readonly Action<string>? _openSession;
    readonly Func<int>? _navigationGeneration;
    readonly Action<string, int>? _openSessionIfCurrent;
    readonly CompositeDisposable _disposables = new();

    string _selectedRepoPath = ScratchRepoPath;
    // Subject (not WhenAnyValue) so the ctor can compose StartButtonTip — same reason as
    // _signInRequired above.
    readonly BehaviorSubject<string> _selectedRepoPathChanges = new(ScratchRepoPath);
    public string SelectedRepoPath {
        get => _selectedRepoPath;
        set {
            this.RaiseAndSetIfChanged(ref _selectedRepoPath, value);
            _selectedRepoPathChanges.OnNext(value);
        }
    }

    string _selectedVendor = DefaultVendor;
    // Follows the repository — only ChooseHarnessAsync/SelectRepositoryAsync set it, so a caller
    // can never desync it from the persisted-or-default rule those two methods implement.
    public string SelectedVendor {
        get => _selectedVendor;
        private set => this.RaiseAndSetIfChanged(ref _selectedVendor, value);
    }

    string _selectedModel = "";
    /// "" = vendor default (the wire convention). Session-scoped, not persisted; reset whenever
    /// the vendor changes — model ids are vendor-specific, so a stale one would misfire.
    public string SelectedModel {
        get => _selectedModel;
        set => this.RaiseAndSetIfChanged(ref _selectedModel, value);
    }

    string? _selectedEffort;
    /// null = vendor default. Survives vendor changes — the effort vocabulary is shared enough
    /// (low/medium/high/xhigh) that the choice usually still means what the user meant.
    public string? SelectedEffort {
        get => _selectedEffort;
        set => this.RaiseAndSetIfChanged(ref _selectedEffort, value);
    }

    string _selectedPermissionMode = DefaultPermissionMode;
    /// A ClaudePermissionModes token. Session-scoped like the effort and kept across vendor
    /// changes; PermissionModeFor decides whether it rides a given launch.
    public string SelectedPermissionMode {
        get => _selectedPermissionMode;
        set => this.RaiseAndSetIfChanged(ref _selectedPermissionMode, value);
    }

    string _goal = "";
    public string Goal {
        get => _goal;
        set => this.RaiseAndSetIfChanged(ref _goal, value);
    }

    string? _startError;
    public string? StartError {
        get => _startError;
        private set => this.RaiseAndSetIfChanged(ref _startError, value);
    }

    /// A 401 outcome is the only writer besides its own reset paths. A subject (not a reactive
    /// property read via WhenAnyValue) so the ctor can compose it — see SessionRailViewModel's
    /// _selectedAgentIdChanges for why WhenAnyValue is avoided in constructors here.
    readonly BehaviorSubject<bool> _signInRequired = new(false);
    /// True after a successful re-auth until the daemon reports server-connected (or goes down).
    /// Keeps the banner from still asking to Sign in while the daemon catches up.
    readonly BehaviorSubject<bool> _awaitingServerAfterSignIn = new(false);
    readonly Action? _requestSignIn;

    readonly ObservableAsPropertyHelper<string?> _connectionNotice;
    /// Why launching is unavailable right now, or null when it isn't. The sign-in-expired text
    /// wins over the connection-derived one — it is the more specific diagnosis.
    public string? ConnectionNotice => _connectionNotice.Value;

    readonly ObservableAsPropertyHelper<string?> _bannerMessage;
    /// Single banner body: a start/lifecycle message wins over the generic connection notice so
    /// Unreachable + "kcap too old" does not stack two lines that both say press Start daemon.
    public string? BannerMessage => _bannerMessage.Value;

    readonly ObservableAsPropertyHelper<bool> _connectionBannerVisible;
    /// Same connection/sign-in/daemon banner the launcher shows above the composer.
    public bool ConnectionBannerVisible => _connectionBannerVisible.Value;

    readonly ObservableAsPropertyHelper<bool> _signInVisible;
    public bool SignInVisible => _signInVisible.Value;

    ObservableAsPropertyHelper<bool>? _daemonStartVisible;
    public bool DaemonStartVisible => _daemonStartVisible?.Value ?? false;

    ObservableAsPropertyHelper<bool>? _daemonRetryVisible;
    public bool DaemonRetryVisible => _daemonRetryVisible?.Value ?? false;

    // Feeds BannerMessage before AttachDaemonRecovery runs (null) and after (lifecycle lane).
    readonly BehaviorSubject<string?> _daemonStartMessageFeed = new(null);

    /// Wired by AttachDaemonRecovery to MainWindow's Start/Reconnect commands.
    public ReactiveCommand<Unit, Unit>? StartDaemonCommand { get; private set; }
    public ReactiveCommand<Unit, Unit>? RetryDaemonCommand { get; private set; }

    readonly ObservableAsPropertyHelper<string> _startButtonTip;
    /// Tip when Start is disabled (no repo / connection gate). ToolTip.ShowOnDisabled is required.
    public string StartButtonTip => _startButtonTip.Value;

    public ReactiveCommand<Unit, Unit> SignInCommand { get; }

    readonly ObservableAsPropertyHelper<IReadOnlyList<HarnessOption>> _harnesses;
    public IReadOnlyList<HarnessOption> Harnesses => _harnesses.Value;

    readonly IObservable<IReadOnlyList<DaemonInfo>> _daemons;
    readonly Func<CancellationToken, Task<string?>> _viewerId;
    readonly IObservable<ServerLaneStatus> _laneStatus;
    readonly string? _localMachineId;

    /// Live mirror of the local availability CombineLatest below, read (never bound directly) by
    /// ListMachinesAsync to stamp the local MachineOption's Connected flag at the moment asked.
    LaunchAvailability _currentAvailability = LaunchAvailability.Pending;

    /// Live mirror of the local daemon's latest advertised SupportedVendors — read (never bound
    /// directly) when a machine selection reverts to local, to revalidate SelectedVendor against
    /// what the local daemon actually hosts rather than leaving whatever a prior remote pick set.
    string[]? _currentLocalVendors;

    /// Set by OwnDaemonsAsync every time it resolves viewerId — the CanExecute pipeline
    /// (FindMachine) reads it to re-verify ownership synchronously, since it cannot itself await
    /// viewerId on every lane/daemons emission. Null until first resolved, or whenever viewerId
    /// itself resolves to null.
    string? _lastViewerId;

    string _selectedMachine;
    /// Which daemon a launch targets: the local one until SelectMachineAsync picks a remote name.
    public string SelectedMachine {
        get => _selectedMachine;
        private set => this.RaiseAndSetIfChanged(ref _selectedMachine, value);
    }

    bool _remoteMachineSelected;
    /// False ⇒ every existing repo/harness/launch behavior is untouched by a HomeViewModel that
    /// never wires the machine picker.
    public bool RemoteMachineSelected {
        get => _remoteMachineSelected;
        private set => this.RaiseAndSetIfChanged(ref _remoteMachineSelected, value);
    }

    // Drives the reactive pipelines below (CanExecute, Harnesses) — a subject rather than
    // WhenAnyValue, same reason as _selectedRepoPathChanges.
    readonly BehaviorSubject<(string Name, bool Remote)> _machineSelectionChanges;
    // null = local (daemon.Snapshots governs Harnesses); non-null = the remote machine's own
    // advertised vendors, set by SelectMachineAsync.
    readonly BehaviorSubject<string[]?> _remoteVendorOverride = new(null);
    // Frozen at selection time — ListRepositoriesAsync's remote branch reads it rather than
    // re-resolving the daemon list on every menu open.
    MachineOption? _selectedRemoteMachine;

    readonly ObservableAsPropertyHelper<bool> _machinePickerVisible;
    /// The machine chip's visibility: hidden until the viewer owns at least one remote daemon.
    public bool MachinePickerVisible => _machinePickerVisible.Value;

    static readonly IComparer<SessionCardViewModel> RowComparer = Comparer<SessionCardViewModel>.Create((a, b) => {
        var byCreated = a.CreatedAt.CompareTo(b.CreatedAt);
        return byCreated != 0 ? byCreated : string.CompareOrdinal(a.Id, b.Id);
    });

    readonly ObservableCollectionExtended<SessionCardViewModel> _sessionsSource = new();
    public ReadOnlyObservableCollection<SessionCardViewModel> Sessions { get; }

    public ReactiveCommand<Unit, Unit> StartCommand { get; }

    /// A launch must be cancellable: the app disposes the launch client (and its HubConnection) on
    /// shutdown, so an in-flight hub invoke holding no token races that teardown.
    readonly CancellationToken _shutdown;

    // The id RequestLaunchAgentV2 hands back is request-accepted, not success: failure arrives
    // later as a LaunchFailed broadcast, success as the agent's row appearing. StartAsync tracks
    // the id here (id -> recorded-at UTC) until one of those settles it; one lock covers both maps
    // since StartAsync, the failure subscription and the rows subscription all touch them.
    readonly object _launchTrackingLock = new();
    readonly Dictionary<string, DateTime> _pendingLaunches = new(StringComparer.Ordinal);
    readonly Dictionary<string, (string Reason, DateTime At)> _recentFailures = new(StringComparer.Ordinal);
    static readonly TimeSpan PendingLaunchTtl = TimeSpan.FromMinutes(10);
    static readonly TimeSpan RecentFailureTtl = TimeSpan.FromSeconds(30);
    readonly IAgentDirectory? _directory;

    /// knownRepos is RepoPathStore.GetSortedPathsAsync in production — the same persisted list
    /// DaemonConnect.RepoPaths feeds the server's launch dialog. Required (no defaulted overload)
    /// so a test can never silently read the developer's own ~/.config/kcap/repos.json.
    /// <param name="openSession">
    /// A session card's click (MainWindowViewModel.OpenSession). Null leaves the cards inert — a
    /// HomeViewModel with no window to navigate.
    /// </param>
    /// <param name="navigationGeneration">
    /// Read BEFORE the launch call, never after: the captured value is what makes a success that
    /// lands after the user navigated away open nothing.
    /// </param>
    /// <param name="openSessionIfCurrent">
    /// The launch auto-open (MainWindowViewModel.OpenSessionIfCurrent), carrying that captured
    /// generation. Never invoked for a launch that targeted a remote machine — that workspace is
    /// backed by the local daemon socket, which can never find the remote agent.
    /// </param>
    /// <param name="requestSignIn">Opens the re-auth sign-in surface (App owns the window). Null
    /// leaves the Sign in button inert — a HomeViewModel with no windows to open.</param>
    /// <param name="daemons">The viewer's own-plus-others remote registry (IRemoteAgentsService.
    /// Daemons). Null ⇒ the machine picker offers only the local daemon.</param>
    /// <param name="viewerId">The signed-in user's own id (JwtClaims), read fresh per call. Null
    /// ⇒ no remote daemon is ever offered — ownership is never guessed.</param>
    /// <param name="laneStatus">The app's own server lane (IServerLane.Status) — what a remote
    /// launch's availability gates on, independent of the local daemon's own connection word.</param>
    /// <param name="localMachineId">This machine's id, for excluding the local daemon's own remote
    /// registry twin from the picker's remote options.</param>
    /// <param name="launchFailures">The app's own server lane (IServerLane.LaunchFailures). A
    /// failure whose id is being tracked renders as StartError; an unknown id is another client's
    /// launch and is ignored. Null ⇒ launch failures are never correlated.</param>
    /// <param name="directory">The merged local+remote rows (IAgentDirectory.Rows). A row for a
    /// tracked id is success confirmation and stops tracking it. Null ⇒ only a LaunchFailed or the
    /// pending entry's own 10-minute timeout ever stops tracking.</param>
    public HomeViewModel(
            IDaemonClientService daemon, IAppStateStore state, ILaunchClient launch,
            Func<Task<string[]>> knownRepos, CancellationToken shutdown = default,
            Action<string>? openSession = null, Func<int>? navigationGeneration = null,
            Action<string, int>? openSessionIfCurrent = null, Action? requestSignIn = null,
            IObservable<IReadOnlyList<DaemonInfo>>? daemons = null,
            Func<CancellationToken, Task<string?>>? viewerId = null,
            IObservable<ServerLaneStatus>? laneStatus = null, string? localMachineId = null,
            IObservable<LaunchFailure>? launchFailures = null, IAgentDirectory? directory = null) {
        _daemon = daemon;
        _state = state;
        _launch = launch;
        _knownRepos = knownRepos;
        _shutdown = shutdown;
        _openSession = openSession;
        _navigationGeneration = navigationGeneration;
        _openSessionIfCurrent = openSessionIfCurrent;
        _requestSignIn = requestSignIn;
        _daemons = daemons ?? Observable.Return<IReadOnlyList<DaemonInfo>>([]);
        _viewerId = viewerId ?? (_ => Task.FromResult<string?>(null));
        _laneStatus = laneStatus ?? Observable.Return(new ServerLaneStatus(ServerLaneState.Dormant));
        _localMachineId = localMachineId;
        _directory = directory;
        _selectedMachine = daemon.DaemonName;
        _machineSelectionChanges = new((daemon.DaemonName, false));

        // Never starts empty: a null SupportedVendors means "daemon
        // capability unknown", not "hosts nothing" — Build(null) offers everything until the first
        // real snapshot narrows it. Merged with the machine-picker's own override (null when
        // local, the selected remote machine's advertised vendors otherwise — SelectMachineAsync's
        // rule). ObserveOn BEFORE ToProperty: Snapshots is pushed from the daemon client's own
        // background thread (MainWindowViewModel's identical comment).
        _harnesses = daemon.Snapshots
            .Select(s => s.Daemon.SupportedVendors)
            .StartWith((string[]?)null)
            .CombineLatest(_remoteVendorOverride, _machineSelectionChanges,
                (localVendors, remoteVendors, sel) => HostedHarnessCatalog.Build(sel.Remote ? remoteVendors : localVendors))
            .ObserveOn(RxSchedulers.MainThreadScheduler)
            .ToProperty(this, x => x.Harnesses, HostedHarnessCatalog.Build(null))
            .DisposeWith(_disposables);

        // A throw from viewerId (e.g. a claims-file read fault) is a missed visibility recompute,
        // never a fault that kills this OAPH's subscription forever (RemoteAgentsService's
        // identical philosophy for its own daemons refresh).
        var ownDaemonsNonEmpty = _daemons
            .Select(list => Observable.FromAsync(() => OwnDaemonsAsync(list)).Catch(Observable.Return<IReadOnlyList<DaemonInfo>>([])))
            .Switch()
            .Select(own => own.Count > 0);
        // OR'd with the live selection so an active remote pick is never stranded behind a
        // registry blip that empties the owned list out from under it.
        _machinePickerVisible = ownDaemonsNonEmpty
            .CombineLatest(_machineSelectionChanges, (hasOwn, sel) => hasOwn || sel.Remote)
            .ObserveOn(RxSchedulers.MainThreadScheduler)
            .ToProperty(this, x => x.MachinePickerVisible, initialValue: false)
            .DisposeWith(_disposables);

        Sessions = new ReadOnlyObservableCollection<SessionCardViewModel>(_sessionsSource);
        // ObserveOn BEFORE the binding operator (SortAndBind counts as "Bind" here, same as
        // ConsentPromptViewModel.Pending): the cache is mutated on the
        // daemon client's background thread. Transform stays upstream of it, which is only safe
        // because a SessionCardViewModel holds no thread-affine Avalonia object (its status dot is
        // an ImmutableSolidColorBrush) — adding one would have to move Transform below the
        // ObserveOn.
        daemon.Agents.Connect()
            .Transform(dto => new SessionCardViewModel(dto))
            .ObserveOn(RxSchedulers.MainThreadScheduler)
            .SortAndBind(_sessionsSource, RowComparer)
            .Subscribe()
            .DisposeWith(_disposables);

        // The word is seeded with "" (no snapshot yet): AvailabilityFor only reads it once the
        // attach state is Connected, by which point DaemonClientService's snapshot-before-Connected
        // ordering guarantees a real value is there (MainWindowViewModel's identical seam comment).
        var availability = daemon.Status.CombineLatest(
            daemon.Snapshots.Select(s => s.Daemon.Connection).StartWith(""), AvailabilityFor);
        availability.Subscribe(a => _currentAvailability = a).DisposeWith(_disposables);
        daemon.Snapshots.Select(s => s.Daemon.SupportedVendors)
            .Subscribe(v => _currentLocalVendors = v)
            .DisposeWith(_disposables);

        // A remote selection swaps in RemoteAvailabilityFor (lane + the selected daemon's latest
        // Connected AND still-owned-by-the-viewer) rather than changing AvailabilityFor itself —
        // the local gate stays exactly what every existing (non-machine-picking) caller already
        // exercises. FindMachine re-verifies ownership against _lastViewerId on every emission, so
        // a registry update that reassigns an already-selected name to a different owner revokes
        // launch readiness rather than trusting the selection made when it was still valid.
        // Shared with notices/signInState/StartButtonTip below — every surface that asks "can I
        // launch right now" reads the SAME selection-aware value, so a remote pick's banner can
        // never disagree with whether Start is actually enabled.
        var selectedAvailability = availability.CombineLatest(
            _laneStatus, _daemons, _machineSelectionChanges,
            (local, lane, list, sel) => sel.Remote ? RemoteAvailabilityFor(lane, FindMachine(list, sel.Name, _lastViewerId)) : local);

        var canLaunch = selectedAvailability.Select(a => a == LaunchAvailability.Ready);

        // Explicit ObserveOn: ReactiveCommand does NOT reschedule the supplied canExecute, and a
        // Status event arrives on the daemon client's pump thread (MainWindowViewModel's canStart
        // comment) — without it CanExecuteChanged would touch the bound Button off the UI thread.
        StartCommand = ReactiveCommand.CreateFromTask(
            StartAsync,
            canLaunch.ObserveOn(RxSchedulers.MainThreadScheduler));

        // OR'd into _signInRequired at the read side (never written into the subject itself) so
        // NotifySignInCompleted's reset stays a clean false — a subject write here would let the
        // lane's still-SignedOut status immediately re-flip it back to true.
        var laneSignedOut = _laneStatus
            .Select(s => s.State == ServerLaneState.SignedOut)
            .DistinctUntilChanged();
        var signInRequired = _signInRequired.CombineLatest(laneSignedOut, (expired, lane) => expired || lane);

        var signInState = selectedAvailability
            .CombineLatest(
                signInRequired,
                _awaitingServerAfterSignIn,
                (a, expired, awaiting) => (Availability: a, Expired: expired, Awaiting: awaiting))
            .ObserveOn(RxSchedulers.MainThreadScheduler);
        // Chained rather than one wide CombineLatest: local status/connection/signIn/awaiting
        // first, then folded against the selection-aware availability and the selection itself —
        // NoticeFor needs both to know whether a local-only notice (daemon down/incompatible)
        // may apply at all.
        var localNoticeInputs = daemon.Status
            .CombineLatest(
                daemon.Snapshots.Select(s => s.Daemon.Connection).StartWith(""),
                signInRequired,
                _awaitingServerAfterSignIn,
                (status, connection, expired, awaiting) => (status, connection, expired, awaiting));
        var notices = localNoticeInputs
            .CombineLatest(selectedAvailability, _machineSelectionChanges,
                (n, avail, sel) => NoticeFor(n.status, n.connection, n.expired, n.awaiting, sel.Remote, avail))
            .ObserveOn(RxSchedulers.MainThreadScheduler);
        _connectionNotice = notices
            .ToProperty(this, x => x.ConnectionNotice, ConnectingNotice)
            .DisposeWith(_disposables);
        // The lifecycle feed speaks for the LOCAL daemon (start attempts, version mismatch), so it
        // may only pre-empt the notice while the local machine is the selected one — under a remote
        // selection its banner would outrank a healthy remote's own (absent) notice.
        var bannerMessages = notices.CombineLatest(
            _daemonStartMessageFeed, _machineSelectionChanges,
            (notice, startMessage, sel) => BannerMessageFor(notice, sel.Remote ? null : startMessage));
        _bannerMessage = bannerMessages
            .ToProperty(this, x => x.BannerMessage, ConnectingNotice)
            .DisposeWith(_disposables);
        _connectionBannerVisible = bannerMessages
            .Select(message => message is not null)
            .ToProperty(this, x => x.ConnectionBannerVisible, initialValue: false)
            .DisposeWith(_disposables);
        _signInVisible = signInState
            .Select(t => !t.Awaiting && (t.Expired || t.Availability == LaunchAvailability.ServerDisconnected))
            .ToProperty(this, x => x.SignInVisible, initialValue: false)
            .DisposeWith(_disposables);

        availability
            .Where(a => a is LaunchAvailability.Ready or LaunchAvailability.DaemonUnavailable)
            .Subscribe(_ => _awaitingServerAfterSignIn.OnNext(false))
            .DisposeWith(_disposables);

        // Local availability alone can never settle awaiting when the local daemon points at a
        // DIFFERENT server than this app's own lane — a terminal lane outcome (either direction)
        // is the other half of the same "finished catching up" signal.
        _laneStatus
            .Select(s => s.State is ServerLaneState.Connected or ServerLaneState.SignedOut)
            .DistinctUntilChanged()
            .Where(t => t)
            .Subscribe(_ => _awaitingServerAfterSignIn.OnNext(false))
            .DisposeWith(_disposables);

        _startButtonTip = _selectedRepoPathChanges
            .CombineLatest(notices, TipFor)
            .ToProperty(this, x => x.StartButtonTip, TipFor(SelectedRepoPath, ConnectingNotice))
            .DisposeWith(_disposables);

        SignInCommand = ReactiveCommand.Create(() => { _requestSignIn?.Invoke(); });

        (launchFailures ?? Observable.Empty<LaunchFailure>())
            .Do(RecordRecentFailure)
            .ObserveOn(RxSchedulers.MainThreadScheduler)
            .Subscribe(ApplyFailureIfPending)
            .DisposeWith(_disposables);

        (directory?.Rows.Connect() ?? Observable.Empty<IChangeSet<AgentRow, string>>())
            .Subscribe(ConfirmPendingRows)
            .DisposeWith(_disposables);

        // Adopt a recent known repo when the launcher starts empty — fire-and-forget; the picker
        // also calls EnsureDefaultRepositoryAsync before listing.
        _ = EnsureDefaultRepositoryAsync();
    }

    /// MainWindow owns Start/Reconnect (lifecycle startAction + shutdown token). The launcher banner
    /// reuses those commands and the start-message lane so chrome and pane never diverge.
    public void AttachDaemonRecovery(
            ReactiveCommand<Unit, Unit> startDaemon,
            ReactiveCommand<Unit, Unit> retry,
            IObservable<bool> startVisible,
            IObservable<bool> retryVisible,
            IObservable<string?> startMessage) {
        StartDaemonCommand = startDaemon;
        RetryDaemonCommand = retry;
        this.RaisePropertyChanged(nameof(StartDaemonCommand));
        this.RaisePropertyChanged(nameof(RetryDaemonCommand));

        _daemonStartVisible = startVisible
            .ToProperty(this, x => x.DaemonStartVisible, initialValue: false)
            .DisposeWith(_disposables);
        _daemonRetryVisible = retryVisible
            .ToProperty(this, x => x.DaemonRetryVisible, initialValue: false)
            .DisposeWith(_disposables);
        startMessage
            .ObserveOn(RxSchedulers.MainThreadScheduler)
            .Subscribe(_daemonStartMessageFeed)
            .DisposeWith(_disposables);
    }

    /// Prefer the actionable start/lifecycle line when present; otherwise the connection notice.
    internal static string? BannerMessageFor(string? connectionNotice, string? startMessage) =>
        !string.IsNullOrEmpty(startMessage) ? startMessage : connectionNotice;

    /// The re-auth dialog's success lands here (App wires it): clears the expired flag and holds a
    /// finishing notice until the daemon reports the server is connected again.
    public void NotifySignInCompleted() {
        _signInRequired.OnNext(false);
        _awaitingServerAfterSignIn.OnNext(true);
    }

    /// Local attach state is checked FIRST — the upstream word is only meaningful once the attach
    /// is Connected (a stale retained snapshot might carry any word). Unknown upstream words read
    /// as disconnected, matching the daemon's own catch-all spelling.
    internal static LaunchAvailability AvailabilityFor(AttachStatus status, string daemonConnection) => status.State switch {
        AttachState.Connecting  => LaunchAvailability.Pending,
        AttachState.Unreachable => LaunchAvailability.DaemonUnavailable,
        _ => daemonConnection switch {
            "connected"                    => LaunchAvailability.Ready,
            "connecting" or "reconnecting" => LaunchAvailability.Pending,
            _                              => LaunchAvailability.ServerDisconnected,
        },
    };

    /// `selectedAvailability` is whichever of AvailabilityFor/RemoteAvailabilityFor applies to the
    /// CURRENT selection — the same value canLaunch gates on, so the notice a remote pick shows
    /// never disagrees with whether Start is actually enabled. `status`/`daemonConnection` matter
    /// only for a local selection's daemon-down/incompatible text — a remote pick has no local
    /// daemon affordance to name, so those never apply to it (a lost lane there is always the
    /// generic ServerLostNotice/ConnectingNotice, matching RemoteAvailabilityFor's own vocabulary).
    internal static string? NoticeFor(
            AttachStatus status, string daemonConnection, bool signInExpired, bool awaitingServer,
            bool remoteSelected, LaunchAvailability selectedAvailability) {
        if (signInExpired) return SignInExpiredNotice;

        if (!remoteSelected && status.State == AttachState.Unreachable)
            return status.Reason == IncompatibleReason ? DaemonIncompatibleNotice : DaemonDownNotice;

        if (awaitingServer && selectedAvailability is LaunchAvailability.ServerDisconnected or LaunchAvailability.Pending)
            return FinishingSignInNotice;

        if (remoteSelected)
            return selectedAvailability switch {
                LaunchAvailability.Ready   => null,
                LaunchAvailability.Pending => ConnectingNotice,
                _                          => ServerLostNotice,
            };

        return selectedAvailability switch {
            LaunchAvailability.Ready             => null,
            LaunchAvailability.Pending           => ConnectingNotice,
            LaunchAvailability.DaemonUnavailable => DaemonDownNotice,
            _                                    => ServerLostNotice,
        };
    }

    /// Repo gate first (IsEnabled), then the connection/sign-in notice StartCommand also gates on.
    internal static string TipFor(string? repoPath, string? connectionNotice) =>
        string.IsNullOrEmpty(repoPath) ? "Select a repository to start"
        : connectionNotice ?? "Start";

    // Constructor-scoped (like TrayViewModel/ActivityViewModel), not WhenActivated — the OAPH and
    // the Agents subscription above run for this object's whole lifetime, not a window's.
    public void Dispose() {
        _disposables.Dispose();
        _daemonStartMessageFeed.Dispose();
    }

    /// Sets the selection and persists it for SelectedRepoPath — except in remote mode, where the
    /// choice applies for the session only; a remote repository is never written into the local
    /// (this-machine-scoped) store.
    public async Task ChooseHarnessAsync(string vendor) {
        SetVendor(vendor);
        if (RemoteMachineSelected) return;

        var repoPath = SelectedRepoPath;
        await _state.UpdateAsync(s => s with { HarnessByRepo = WithEntry(s.HarnessByRepo, repoPath, vendor) });
    }

    /// The repository chip's menu, assembled per open rather than kept as a live projection — the
    /// flyout is transient, so reading at click time is always fresh with no extra subscription.
    /// Sources: remembered HarnessByRepo keys, distinct agent RepoPaths, the daemon's persisted
    /// known repos (what the server's launch dialog sees), and the current selection (a
    /// picker-added repo with no remembered harness and no agent yet lives nowhere else).
    /// Deduped under PathComparer with remembered keys added first, so where two casings are one
    /// repository the casing the user picked is the one displayed. Scratch ("No repository") is
    /// offered only when that list is empty — with real repos, an empty selection adopts the
    /// most recently used known path instead.
    ///
    /// A remote machine selection swaps this entirely: only that machine's own advertised
    /// RepoPaths are offered (no known-repos merge, no scratch adoption — a remote path is never
    /// verified locally), each labelled DefaultVendor since there is no per-repo remembered
    /// harness for a machine that is not this one.
    public async Task<IReadOnlyList<RepositoryOption>> ListRepositoriesAsync() {
        if (RemoteMachineSelected) {
            var remoteSelected = SelectedRepoPath;
            return (_selectedRemoteMachine?.RepoPaths ?? [])
                .Select(p => new RepositoryOption(p, DefaultVendor, PathComparer.Equals(p, remoteSelected)))
                .ToList();
        }

        await EnsureDefaultRepositoryAsync();

        var (byRepo, paths) = await GatherLocalRepoDataAsync();

        var selected = SelectedRepoPath;
        var options = paths
            .OrderBy(p => RepoLabel.Leaf(p), StringComparer.OrdinalIgnoreCase)
            .ThenBy(p => p, StringComparer.Ordinal)
            .Select(p => new RepositoryOption(p, Lookup(byRepo, p) ?? DefaultVendor, PathComparer.Equals(p, selected)))
            .ToList();

        if (paths.Count == 0)
            options.Add(new RepositoryOption(
                ScratchRepoPath, Lookup(byRepo, ScratchRepoPath) ?? DefaultVendor, selected.Length == 0));
        return options;
    }

    /// The path-gathering half of ListRepositoriesAsync's local branch, split out so
    /// ListMachinesAsync can reuse it for the local MachineOption's RepoPaths without a second
    /// AppStateStore read of the same file.
    async Task<(IReadOnlyDictionary<string, string>? ByRepo, List<string> Paths)> GatherLocalRepoDataAsync() {
        var byRepo = (await _state.LoadAsync()).HarnessByRepo;
        var known = await _knownRepos();

        var seen = new HashSet<string>(PathComparer);
        var paths = new List<string>();
        void Add(string? path) {
            if (string.IsNullOrEmpty(path)) return;
            var normalized = PlatformPaths.Normalize(path);
            if (seen.Add(normalized)) paths.Add(normalized);
        }

        foreach (var key in byRepo?.Keys ?? [])
            Add(key);
        // An agent's RepoPath can be a worktree checkout (review flows launch into the
        // requester's worktree) — the menu offers the repository, never the checkout (GH #655).
        foreach (var agent in _daemon.Agents.Items)
            if (agent.RepoPath is { Length: > 0 } repoPath)
                Add(GitRepository.ResolveMainRepoRoot(repoPath));
        foreach (var repo in known)
            Add(repo);
        Add(SelectedRepoPath);

        return (byRepo, paths);
    }

    /// The launcher's machine chip menu: local first, always, then the viewer's own connected
    /// remote daemons — filtered through the one ownership definition (OwnDaemonsAsync), so a row
    /// is never offered for a daemon the viewer doesn't own AT LISTING TIME. That filter can go
    /// stale the moment it's read, so it is the UI affordance layer only: StartAsync re-verifies
    /// ownership fresh, immediately before the wire request is built, and is the actual boundary.
    /// A null viewer id (signed out, or claims not readable yet) means no remote options ever,
    /// never a guess.
    public async Task<IReadOnlyList<MachineOption>> ListMachinesAsync() {
        var (_, localPaths) = await GatherLocalRepoDataAsync();
        var options = new List<MachineOption> {
            new(_daemon.DaemonName, IsLocal: true, _currentAvailability == LaunchAvailability.Ready,
                Platform: null, localPaths.ToArray(), SupportedVendors: null, Selected: !RemoteMachineSelected),
        };

        foreach (var d in await OwnRemoteDaemonsAsync())
            options.Add(new MachineOption(
                d.Name, IsLocal: false, d.Connected, d.Platform, d.RepoPaths ?? [], d.SupportedVendors,
                Selected: RemoteMachineSelected && string.Equals(d.Name, SelectedMachine, StringComparison.Ordinal)));

        return options;
    }

    /// isLocal comes from the clicked MachineOption's own IsLocal, never re-derived from name
    /// equality here — an owned REMOTE daemon can be named identically to the local one (they
    /// live on different servers), so matching by name alone would make that remote entry
    /// unselectable. Picking local restores the local repository/vendor flow (revalidated below)
    /// for every non-machine-picking caller; picking a remote name switches the repository and
    /// harness sources to that machine's own advertised set and resets the vendor off one it
    /// cannot host. A remote name that fails the ownership filter is refused outright — the
    /// selection is left exactly as it was, the same as clicking a menu row that was never offered.
    public async Task SelectMachineAsync(string daemonName, bool isLocal) {
        if (isLocal) {
            var wasRemote = RemoteMachineSelected;
            SetMachineSelection(daemonName, remote: false);
            _selectedRemoteMachine = null;
            _remoteVendorOverride.OnNext(null);
            if (!wasRemote) return; // already local — nothing a remote pick could have left behind

            // A remote machine's path/vendor can never carry over into the local menu — restore
            // exactly the flow a fresh, no-selection launcher would run.
            SelectedRepoPath = ScratchRepoPath;
            await EnsureDefaultRepositoryAsync();
            RevalidateVendorAgainst(_currentLocalVendors);
            return;
        }

        DaemonInfo? match = null;
        foreach (var d in await OwnRemoteDaemonsAsync())
            if (string.Equals(d.Name, daemonName, StringComparison.Ordinal)) { match = d; break; }
        if (match is null) return; // not one of the viewer's own daemons — never guess ownership

        var machine = new MachineOption(match.Name, false, match.Connected, match.Platform,
            match.RepoPaths ?? [], match.SupportedVendors, true);

        SetMachineSelection(daemonName, remote: true);
        _selectedRemoteMachine = machine;
        _remoteVendorOverride.OnNext(machine.SupportedVendors);
        SelectedRepoPath = machine.RepoPaths.FirstOrDefault() is { Length: > 0 } first ? first : ScratchRepoPath;
        RevalidateVendorAgainst(machine.SupportedVendors);
    }

    /// Resets SelectedVendor to the first vendor Available in supportedVendors' Build() when the
    /// current one isn't — shared so a machine's OWN advertised set is what a selection (either
    /// direction) is checked against, never whatever a PRIOR machine last set it to.
    void RevalidateVendorAgainst(string[]? supportedVendors) {
        var options = HostedHarnessCatalog.Build(supportedVendors);
        var current = options.FirstOrDefault(o => string.Equals(o.Vendor, SelectedVendor, StringComparison.OrdinalIgnoreCase));
        if (current is not { Available: true })
            SetVendor(options.FirstOrDefault(o => o.Available)?.Vendor ?? DefaultVendor);
    }

    void SetMachineSelection(string name, bool remote) {
        SelectedMachine = name;
        RemoteMachineSelected = remote;
        _machineSelectionChanges.OnNext((name, remote));
    }

    /// A daemon with the local machine id and the local daemon's own name is this same daemon's
    /// registry entry, not a distinct remote target — MachineId is null-guarded so an app that has
    /// never learned its own machine id (localMachineId: null) never treats a name collision as a
    /// twin (LocalDaemonTwin's identical guard).
    bool IsLocalTwin(DaemonInfo d) =>
        _localMachineId is not null && d.MachineId == _localMachineId
        && string.Equals(d.Name, _daemon.DaemonName, StringComparison.Ordinal);

    /// The one ownership filter: every remote daemon this ViewModel ever offers or resolves by
    /// name goes through this, so "the viewer's own daemons" has exactly one definition. Also
    /// caches the resolved id in _lastViewerId, which FindMachine reads to re-verify ownership
    /// synchronously — the CanExecute pipeline cannot itself await viewerId on every tick.
    async Task<IReadOnlyList<DaemonInfo>> OwnDaemonsAsync(IReadOnlyList<DaemonInfo> all) {
        var viewer = await _viewerId(_shutdown);
        _lastViewerId = viewer;
        if (viewer is null) return [];
        return all.Where(d => d.OwnerUserId == viewer && !IsLocalTwin(d)).ToList();
    }

    async Task<IReadOnlyList<DaemonInfo>> OwnRemoteDaemonsAsync() => await OwnDaemonsAsync(await _daemons.FirstAsync());

    /// Live lookup by name AND owner against the raw (unfiltered) daemons list, for the CanExecute
    /// pipeline. A name that ListMachinesAsync/SelectMachineAsync ownership-filtered earlier is
    /// re-checked against viewerId here too: if a later registry update reassigns that same name
    /// to a different owner, the match fails and the selection stops being launchable, rather than
    /// trusting a filter that only ran once, at selection time. A null viewerId (never resolved,
    /// or resolved to signed-out) means no remote daemon is ever treated as owned.
    static MachineOption? FindMachine(IReadOnlyList<DaemonInfo> daemons, string name, string? viewerId) {
        if (viewerId is null) return null;
        foreach (var d in daemons)
            if (string.Equals(d.Name, name, StringComparison.Ordinal) && d.OwnerUserId == viewerId)
                return new MachineOption(d.Name, false, d.Connected, d.Platform, d.RepoPaths ?? [], d.SupportedVendors, true);
        return null;
    }

    /// A remote launch is Ready only with the app's OWN server lane connected AND the selected
    /// daemon's latest reported Connected — the local daemon-down notices never apply here (they
    /// stay local-only; a remote pick with the lane down still just shows ServerLostNotice).
    /// Connecting reads as Pending (not ServerDisconnected) so the notice for a remote selection
    /// can distinguish "still connecting" from "lost" the same way the local path already does.
    internal static LaunchAvailability RemoteAvailabilityFor(ServerLaneStatus lane, MachineOption? machine) {
        if (lane.State == ServerLaneState.Connecting) return LaunchAvailability.Pending;
        if (lane.State != ServerLaneState.Connected) return LaunchAvailability.ServerDisconnected;
        return machine is { Connected: true } ? LaunchAvailability.Ready : LaunchAvailability.DaemonUnavailable;
    }

    /// When nothing is selected, adopt the most recently used known repository (then a remembered
    /// harness key, then an agent root). No-op when a selection already exists or nothing is known.
    public async Task EnsureDefaultRepositoryAsync() {
        if (SelectedRepoPath.Length > 0) return;
        if (await PreferRecentRepositoryAsync() is not { Length: > 0 } recent) return;
        // PreferRecent can await file I/O — a pick made while that ran must win.
        if (SelectedRepoPath.Length > 0) return;
        await SelectRepositoryAsync(recent);
    }

    async Task<string?> PreferRecentRepositoryAsync() {
        foreach (var path in await _knownRepos()) {
            var normalized = PlatformPaths.Normalize(path);
            if (normalized.Length > 0) return normalized;
        }

        foreach (var key in (await _state.LoadAsync()).HarnessByRepo?.Keys ?? []) {
            var normalized = PlatformPaths.Normalize(key);
            if (normalized.Length > 0) return normalized;
        }

        foreach (var agent in _daemon.Agents.Items) {
            if (agent.RepoPath is not { Length: > 0 } repoPath) continue;
            var normalized = PlatformPaths.Normalize(GitRepository.ResolveMainRepoRoot(repoPath));
            if (normalized.Length > 0) return normalized;
        }

        return null;
    }

    /// Sets the repository and restores that repository's remembered harness, or DefaultVendor
    /// when none — never the vendor a DIFFERENT repository had selected. In remote mode the local
    /// HarnessByRepo store is never consulted: the vendor is instead revalidated against the
    /// selected remote machine's own advertised set (RevalidateVendorAgainst).
    public async Task SelectRepositoryAsync(string repoPath) {
        SelectedRepoPath = repoPath.Length == 0 ? ScratchRepoPath : PlatformPaths.Normalize(repoPath);
        if (RemoteMachineSelected) {
            RevalidateVendorAgainst(_selectedRemoteMachine?.SupportedVendors);
            return;
        }

        var saved = await _state.LoadAsync();
        SetVendor(Lookup(saved.HarnessByRepo, SelectedRepoPath) ?? DefaultVendor);
    }

    /// The one place a vendor change lands, so the model-reset invariant (ids are
    /// vendor-specific) can never be forgotten by a new call site.
    void SetVendor(string vendor) {
        if (vendor != SelectedVendor) SelectedModel = "";
        SelectedVendor = vendor;
    }

    /// A session card's click (HomeView routes it here). No generation is involved — the click IS
    /// the current navigation, unlike the launch auto-open below.
    public void OpenSessionRequested(string agentId) => _openSession?.Invoke(agentId);

    async Task StartAsync() {
        // ReactiveCommand.Execute() does not itself gate on CanExecute — a caller that bypasses
        // the bound Button (or the canExecute observable's own staleness, however small) can still
        // reach here, so a remote target gets one more, fully fresh ownership+connected check
        // right before the wire request is built. This is the actual boundary; canLaunch/
        // FindMachine above are the UI-responsive affordance, not a substitute for it.
        if (RemoteMachineSelected) {
            var owned = await OwnRemoteDaemonsAsync();
            if (!owned.Any(d => string.Equals(d.Name, SelectedMachine, StringComparison.Ordinal) && d.Connected)) {
                StartError = MachineUnavailableMessage;
                return;
            }
        }

        var request = new LaunchRequest(
            SelectedMachine, SelectedRepoPath, SelectedVendor, Goal, SelectedModel, SelectedEffort,
            PermissionModeFor(SelectedVendor, SelectedPermissionMode));
        // Both captured BEFORE the call, never after: the whole point is to notice a navigation —
        // or a machine selection — that changed WHILE the launch was in flight.
        var generation = _navigationGeneration?.Invoke() ?? 0;
        var launchedRemote = RemoteMachineSelected;

        var outcome = await _launch.StartAsync(request, _shutdown);
        if (!outcome.Started) {
            // Every finished attempt is fresh evidence, so the flag follows it both ways. An
            // unauthorized outcome renders as the sign-in notice, never as raw transport text.
            _signInRequired.OnNext(outcome.Unauthorized);
            StartError = outcome.Unauthorized ? null : outcome.Error;
            return;
        }

        _signInRequired.OnNext(false);
        StartError = null;
        Goal = ""; // the launch really did start — the goal is spent either way
        if (NormalizeAgentId(outcome.AgentId) is not { } agentId) {
            StartError = UnusableIdMessage;
            return;
        }

        // The accepted id is request-accepted, not success — track it until a LaunchFailed or a
        // directory row settles it. RecordPendingLaunch also resolves the race where the failure
        // already arrived (and was buffered) while the invoke above was still in flight.
        RecordPendingLaunch(agentId);
        // A remote launch's workspace is backed by the local daemon socket, which can never find
        // an agent that isn't there — auto-open only ever applies to a local target.
        if (!launchedRemote) _openSessionIfCurrent?.Invoke(agentId, generation);
    }

    void RecordPendingLaunch(string agentId) {
        // A row for this id confirms success, and it can appear on either side of the registration
        // below: the launch may have succeeded before this method ran at all, or the directory's
        // Add may land while it runs — at which point ConfirmPendingRows finds nothing pending yet
        // and clears nothing. So the row is checked twice, and a buffered failure only ever renders
        // when neither check found one. Both checks are outside _launchTrackingLock (RowExists
        // takes no lock of its own), keeping the cache→tracking lock order intact.
        if (RowExists(agentId)) {
            ForgetLaunch(agentId);
            return;
        }

        string? bufferedReason = null;
        lock (_launchTrackingLock) {
            _pendingLaunches[agentId] = DateTime.UtcNow;
            if (_recentFailures.TryGetValue(agentId, out var recent)) {
                if (DateTime.UtcNow - recent.At <= RecentFailureTtl) {
                    bufferedReason = recent.Reason;
                    _pendingLaunches.Remove(agentId);
                }
                _recentFailures.Remove(agentId);
            }
        }
        if (RowExists(agentId)) {
            ForgetLaunch(agentId);
            return;
        }
        if (bufferedReason is not null) StartError = FriendlyLaunchFailure(bufferedReason);
    }

    void ForgetLaunch(string agentId) {
        lock (_launchTrackingLock) {
            _pendingLaunches.Remove(agentId);
            _recentFailures.Remove(agentId);
        }
    }

    // Directory keys preserve the row's incoming id spelling (e.g. a dashed Guid never becomes
    // "local:{N-form}"), so a lookup by the "N"-normalized pending id would miss it — scan and
    // compare under NormalizeAgentId instead, the same comparison ConfirmPendingRows uses.
    bool RowExists(string agentId) =>
        _directory is { } directory
        && directory.Rows.Items.Any(r => NormalizeAgentId(r.Id) == agentId);

    void RecordRecentFailure(LaunchFailure failure) {
        if (NormalizeAgentId(failure.AgentId) is not { } agentId) return;
        lock (_launchTrackingLock) {
            _recentFailures[agentId] = (failure.Reason, DateTime.UtcNow);
            var cutoff = DateTime.UtcNow - RecentFailureTtl;
            foreach (var stale in _recentFailures.Where(kv => kv.Value.At < cutoff).Select(kv => kv.Key).ToList())
                _recentFailures.Remove(stale);
        }
    }

    /// A failure for an id nothing here is tracking is another client's launch — ignored. A
    /// pending entry consulted past its 10-minute TTL is treated the same way. Compared under
    /// NormalizeAgentId so a dashed-Guid failure id still matches the "N"-normalized key StartAsync
    /// recorded — the hub returns either shape (NormalizeAgentId's own comment).
    void ApplyFailureIfPending(LaunchFailure failure) {
        if (NormalizeAgentId(failure.AgentId) is not { } agentId) return;
        bool applies;
        lock (_launchTrackingLock) {
            applies = _pendingLaunches.TryGetValue(agentId, out var recordedAt)
                && DateTime.UtcNow - recordedAt <= PendingLaunchTtl;
            _pendingLaunches.Remove(agentId);
        }
        if (applies) StartError = FriendlyLaunchFailure(failure.Reason);
    }

    /// A row for a tracked id is success confirmation: drop the pending entry and any buffered
    /// failure so a later, stale LaunchFailed for the same id cannot override it. Same
    /// NormalizeAgentId comparison as ApplyFailureIfPending, for the same id-shape reason.
    void ConfirmPendingRows(IChangeSet<AgentRow, string> changes) {
        lock (_launchTrackingLock) {
            foreach (var change in changes)
                if (change.Reason == ChangeReason.Add && NormalizeAgentId(change.Current.Id) is { } agentId) {
                    _pendingLaunches.Remove(agentId);
                    _recentFailures.Remove(agentId);
                }
        }
    }

    /// <see cref="WireTokens.LaunchDeniedByOwnerPrefix"/> is a consent-gate denial on the target
    /// machine; every other reason passes through verbatim (the server already truncates to 400
    /// characters).
    internal static string FriendlyLaunchFailure(string reason) =>
        reason.StartsWith(WireTokens.LaunchDeniedByOwnerPrefix, StringComparison.Ordinal)
            ? "That machine's consent policy denied the launch. Approve it there, or pre-set a rule with kcap consent."
            : reason;

    /// Null for Manual (the harness's own default) and for any vendor that takes no mode.
    internal static string? PermissionModeFor(string vendor, string mode) =>
        HostedHarnessCatalog.SupportsPermissionMode(vendor)
     && !string.Equals(mode, ClaudePermissionModes.Manual, StringComparison.Ordinal)
            ? mode
            : null;

    /// Id shapes differ across the stack and across daemon versions: the server hub has returned
    /// DASHED Guids while a production daemon keys its status cache on SHORT (8-hex) ids — so a
    /// Guid in any format is normalized to "N" (the Guid-keyed daemons' cache shape), and any
    /// other non-empty id passes through VERBATIM to match whatever the daemon actually sent.
    /// Only a null/blank id is unusable; an id that matches nothing degrades gracefully in the
    /// workspace ("session not found" with retry), which beats a red error under a live card.
    internal static string? NormalizeAgentId(string? agentId) =>
        Guid.TryParse(agentId, out var parsed) ? parsed.ToString("N")
        : string.IsNullOrWhiteSpace(agentId) ? null
        : agentId;

    /// Applied on READ because System.Text.Json rebuilds the dictionary with a default (ordinal)
    /// comparer on load — a comparer set only at write time would not survive the round-trip.
    static readonly StringComparer PathComparer = PlatformPaths.Comparer;

    static string? Lookup(IReadOnlyDictionary<string, string>? byRepo, string repoPath) {
        if (byRepo is null) return null;

        foreach (var entry in byRepo)
            if (PathComparer.Equals(entry.Key, repoPath))
                return entry.Value;

        return null;
    }

    /// Replaces any entry whose key matches under PathComparer, so re-choosing a harness for the
    /// same repository reached under different casing or a trailing separator overwrites rather
    /// than accumulating a second, shadowing entry.
    static IReadOnlyDictionary<string, string> WithEntry(IReadOnlyDictionary<string, string>? existing, string key, string value) {
        var normalized = key.Length == 0 ? key : PlatformPaths.Normalize(key);
        var next = new Dictionary<string, string>();
        if (existing is not null)
            foreach (var entry in existing)
                if (!PathComparer.Equals(entry.Key, normalized))
                    next[entry.Key] = entry.Value;

        next[normalized] = value;
        return next;
    }
}
