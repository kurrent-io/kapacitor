using System.Collections.ObjectModel;
using System.Reactive;
using System.Reactive.Disposables;
using System.Reactive.Disposables.Fluent;
using System.Reactive.Linq;
using System.Reactive.Subjects;
using Capacitor.App.Services;
using Capacitor.Cli.Core;
using Capacitor.Cli.Core.Harness.Claude;
using DynamicData;
using DynamicData.Binding;
using ReactiveUI;

namespace Capacitor.App.ViewModels;

/// One entry of the repository chip's menu. Vendor is the remembered harness for RepoPath, or
/// HomeViewModel.DefaultVendor when none was ever chosen there; Selected marks the entry that
/// matches SelectedRepoPath under HomeViewModel's own path comparison.
public sealed record RepositoryOption(string RepoPath, string Vendor, bool Selected);

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

    /// A launch that started but handed back an id nothing can open. The session is real and running
    /// — it just has to be reached from the session list, so this is a launch-succeeded wording, not
    /// a failure one (spec §3, entry-point guards).
    public const string UnusableIdMessage = "Launched, but the session id was unusable. Open it from the session list.";

    internal const string ConnectingNotice     = "Connecting to the server…";
    internal const string DaemonDownNotice     =
        "The daemon isn't running. Start it to launch sessions. If it should already be up, press Retry.";
    internal const string DaemonIncompatibleNotice =
        "App and daemon are incompatible. Update both to matching versions, then press Retry.";
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
    readonly Action? _requestSignIn;

    readonly ObservableAsPropertyHelper<string?> _connectionNotice;
    /// Why launching is unavailable right now, or null when it isn't. The sign-in-expired text
    /// wins over the connection-derived one — it is the more specific diagnosis.
    public string? ConnectionNotice => _connectionNotice.Value;

    readonly ObservableAsPropertyHelper<bool> _connectionBannerVisible;
    /// Same connection/sign-in/daemon banner the launcher shows above the composer.
    public bool ConnectionBannerVisible => _connectionBannerVisible.Value;

    readonly ObservableAsPropertyHelper<bool> _signInVisible;
    public bool SignInVisible => _signInVisible.Value;

    ObservableAsPropertyHelper<bool>? _daemonStartVisible;
    public bool DaemonStartVisible => _daemonStartVisible?.Value ?? false;

    ObservableAsPropertyHelper<bool>? _daemonRetryVisible;
    public bool DaemonRetryVisible => _daemonRetryVisible?.Value ?? false;

    ObservableAsPropertyHelper<string?>? _daemonStartMessage;
    /// Start-daemon failure text mirrored from MainWindow (cleared on Connected / new attempt).
    public string? DaemonStartMessage => _daemonStartMessage?.Value;

    /// Shared with MainWindowViewModel so the banner's Start daemon button is the same command
    /// lifecycle / startAction already owns. Null until AttachDaemonRecovery runs.
    public ReactiveCommand<Unit, Unit>? StartDaemonCommand { get; private set; }

    /// Shared with MainWindowViewModel.RetryCommand. Null until AttachDaemonRecovery runs.
    public ReactiveCommand<Unit, Unit>? RetryDaemonCommand { get; private set; }

    readonly ObservableAsPropertyHelper<string> _startButtonTip;
    /// Hover tip for Start: names the gate that keeps it disabled (no repo, or ConnectionNotice),
    /// else plain "Start". Bound with ToolTip.ShowOnDisabled so a disabled button still explains.
    public string StartButtonTip => _startButtonTip.Value;

    public ReactiveCommand<Unit, Unit> SignInCommand { get; }

    readonly ObservableAsPropertyHelper<IReadOnlyList<HarnessOption>> _harnesses;
    public IReadOnlyList<HarnessOption> Harnesses => _harnesses.Value;

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

    /// knownRepos is RepoPathStore.GetSortedPathsAsync in production — the same persisted list
    /// DaemonConnect.RepoPaths feeds the server's launch dialog. Required (no defaulted overload)
    /// so a test can never silently read the developer's own ~/.config/kcap/repos.json.
    /// <param name="openSession">
    /// A session card's click (MainWindowViewModel.OpenSession). Null leaves the cards inert — a
    /// HomeViewModel with no window to navigate.
    /// </param>
    /// <param name="navigationGeneration">
    /// Read BEFORE the launch call, never after: the captured value is what makes a success that
    /// lands after the user navigated away open nothing (spec §3).
    /// </param>
    /// <param name="openSessionIfCurrent">
    /// The launch auto-open (MainWindowViewModel.OpenSessionIfCurrent), carrying that captured
    /// generation.
    /// </param>
    /// <param name="requestSignIn">Opens the re-auth sign-in surface (App owns the window). Null
    /// leaves the Sign in button inert — a HomeViewModel with no windows to open.</param>
    public HomeViewModel(
            IDaemonClientService daemon, IAppStateStore state, ILaunchClient launch,
            Func<Task<string[]>> knownRepos, CancellationToken shutdown = default,
            Action<string>? openSession = null, Func<int>? navigationGeneration = null,
            Action<string, int>? openSessionIfCurrent = null, Action? requestSignIn = null) {
        _daemon = daemon;
        _state = state;
        _launch = launch;
        _knownRepos = knownRepos;
        _shutdown = shutdown;
        _openSession = openSession;
        _navigationGeneration = navigationGeneration;
        _openSessionIfCurrent = openSessionIfCurrent;
        _requestSignIn = requestSignIn;

        // Never starts empty: a null SupportedVendors means "daemon
        // capability unknown", not "hosts nothing" — Build(null) offers everything until the first
        // real snapshot narrows it. ObserveOn BEFORE ToProperty: Snapshots is pushed from the
        // daemon client's own background thread (MainWindowViewModel's identical comment).
        _harnesses = daemon.Snapshots
            .ObserveOn(RxSchedulers.MainThreadScheduler)
            .Select(s => HostedHarnessCatalog.Build(s.Daemon.SupportedVendors))
            .ToProperty(this, x => x.Harnesses, HostedHarnessCatalog.Build(null))
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

        // Explicit ObserveOn: ReactiveCommand does NOT reschedule the supplied canExecute, and a
        // Status event arrives on the daemon client's pump thread (MainWindowViewModel's canStart
        // comment) — without it CanExecuteChanged would touch the bound Button off the UI thread.
        StartCommand = ReactiveCommand.CreateFromTask(
            StartAsync,
            availability.Select(a => a == LaunchAvailability.Ready)
                .ObserveOn(RxSchedulers.MainThreadScheduler));

        var signInState = availability
            .CombineLatest(_signInRequired, (a, expired) => (Availability: a, Expired: expired))
            .ObserveOn(RxSchedulers.MainThreadScheduler);
        var notices = daemon.Status
            .CombineLatest(
                daemon.Snapshots.Select(s => s.Daemon.Connection).StartWith(""),
                _signInRequired,
                NoticeFor)
            .ObserveOn(RxSchedulers.MainThreadScheduler);
        _connectionNotice = notices
            .ToProperty(this, x => x.ConnectionNotice, ConnectingNotice)
            .DisposeWith(_disposables);
        _connectionBannerVisible = notices
            .Select(notice => notice is not null)
            .ToProperty(this, x => x.ConnectionBannerVisible, initialValue: false)
            .DisposeWith(_disposables);
        _signInVisible = signInState
            .Select(t => t.Expired || t.Availability == LaunchAvailability.ServerDisconnected)
            .ToProperty(this, x => x.SignInVisible, initialValue: false)
            .DisposeWith(_disposables);

        _startButtonTip = _selectedRepoPathChanges
            .CombineLatest(notices, TipFor)
            .ToProperty(this, x => x.StartButtonTip, TipFor(SelectedRepoPath, ConnectingNotice))
            .DisposeWith(_disposables);

        SignInCommand = ReactiveCommand.Create(() => { _requestSignIn?.Invoke(); });
    }

    /// MainWindow owns Start/Retry (lifecycle startAction + shutdown token). The launcher banner
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
        _daemonStartMessage = startMessage
            .ObserveOn(RxSchedulers.MainThreadScheduler)
            .ToProperty(this, x => x.DaemonStartMessage, (string?)null)
            .DisposeWith(_disposables);
    }

    /// The re-auth dialog's success lands here (App wires it): the expired flag lifts without
    /// waiting for the next launch attempt to re-prove it.
    public void NotifySignInCompleted() => _signInRequired.OnNext(false);

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

    internal static string? NoticeFor(AttachStatus status, string daemonConnection, bool signInExpired) {
        if (signInExpired) return SignInExpiredNotice;
        if (status.State == AttachState.Unreachable) {
            return status.Reason == IncompatibleReason ? DaemonIncompatibleNotice : DaemonDownNotice;
        }
        return AvailabilityFor(status, daemonConnection) switch {
            LaunchAvailability.Ready             => null,
            LaunchAvailability.Pending           => ConnectingNotice,
            LaunchAvailability.DaemonUnavailable => DaemonDownNotice,
            _                                    => ServerLostNotice,
        };
    }

    /// Back-compat for callers that already classified availability.
    internal static string? NoticeFor(LaunchAvailability availability, bool signInExpired) =>
        signInExpired ? SignInExpiredNotice
        : availability switch {
            LaunchAvailability.Ready             => null,
            LaunchAvailability.Pending           => ConnectingNotice,
            LaunchAvailability.DaemonUnavailable => DaemonDownNotice,
            _                                    => ServerLostNotice,
        };

    /// Repo gate first (IsEnabled), then the connection/sign-in notice StartCommand also gates on.
    internal static string TipFor(string? repoPath, string? connectionNotice) =>
        string.IsNullOrEmpty(repoPath) ? "Select a repository to start"
        : connectionNotice ?? "Start";

    // Constructor-scoped (like TrayViewModel/ActivityViewModel), not WhenActivated — the OAPH and
    // the Agents subscription above run for this object's whole lifetime, not a window's.
    public void Dispose() => _disposables.Dispose();

    /// Sets the selection and persists it for SelectedRepoPath.
    public async Task ChooseHarnessAsync(string vendor) {
        SetVendor(vendor);

        var repoPath = SelectedRepoPath;
        await _state.UpdateAsync(s => s with { HarnessByRepo = WithEntry(s.HarnessByRepo, repoPath, vendor) });
    }

    /// The repository chip's menu, assembled per open rather than kept as a live projection — the
    /// flyout is transient, so reading at click time is always fresh with no extra subscription.
    /// Sources: remembered HarnessByRepo keys, distinct agent RepoPaths, the daemon's persisted
    /// known repos (what the server's launch dialog sees), and the current selection (a
    /// picker-added repo with no remembered harness and no agent yet lives nowhere else).
    /// Deduped under PathComparer with remembered keys added first, so where two casings are one
    /// repository the casing the user picked is the one displayed. Scratch is always last; the
    /// view renders it separated.
    public async Task<IReadOnlyList<RepositoryOption>> ListRepositoriesAsync() {
        var byRepo = (await _state.LoadAsync()).HarnessByRepo;
        var known = await _knownRepos();

        var seen = new HashSet<string>(PathComparer);
        var paths = new List<string>();
        void Add(string? path) {
            if (!string.IsNullOrEmpty(path) && seen.Add(path)) paths.Add(path);
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

        var selected = SelectedRepoPath;
        var options = paths
            .OrderBy(p => RepoLabel.Leaf(p), StringComparer.OrdinalIgnoreCase)
            .ThenBy(p => p, StringComparer.Ordinal)
            .Select(p => new RepositoryOption(p, Lookup(byRepo, p) ?? DefaultVendor, PathComparer.Equals(p, selected)))
            .ToList();

        options.Add(new RepositoryOption(
            ScratchRepoPath, Lookup(byRepo, ScratchRepoPath) ?? DefaultVendor, selected.Length == 0));
        return options;
    }

    /// Sets the repository and restores that repository's remembered harness, or DefaultVendor
    /// when none — never the vendor a DIFFERENT repository had selected.
    public async Task SelectRepositoryAsync(string repoPath) {
        SelectedRepoPath = repoPath;
        var saved = await _state.LoadAsync();
        SetVendor(Lookup(saved.HarnessByRepo, repoPath) ?? DefaultVendor);
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
        var request = new LaunchRequest(
            _daemon.DaemonName, SelectedRepoPath, SelectedVendor, Goal, SelectedModel, SelectedEffort,
            PermissionModeFor(SelectedVendor, SelectedPermissionMode));
        // Captured BEFORE the call, never after (spec §3): the whole point is to notice a navigation
        // that happened WHILE the launch was in flight.
        var generation = _navigationGeneration?.Invoke() ?? 0;

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

        _openSessionIfCurrent?.Invoke(agentId, generation);
    }

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
    /// same repository reached under different casing overwrites rather than accumulating a
    /// second, shadowing entry.
    static IReadOnlyDictionary<string, string> WithEntry(IReadOnlyDictionary<string, string>? existing, string key, string value) {
        var next = new Dictionary<string, string>();
        if (existing is not null)
            foreach (var entry in existing)
                if (!PathComparer.Equals(entry.Key, key))
                    next[entry.Key] = entry.Value;

        next[key] = value;
        return next;
    }
}
