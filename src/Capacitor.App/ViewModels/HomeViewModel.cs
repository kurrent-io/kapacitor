using System.Collections.ObjectModel;
using System.Reactive;
using System.Reactive.Disposables;
using System.Reactive.Disposables.Fluent;
using System.Reactive.Linq;
using Capacitor.App.Services;
using Capacitor.Cli.Core;
using DynamicData;
using DynamicData.Binding;
using ReactiveUI;

namespace Capacitor.App.ViewModels;

/// One entry of the repository chip's menu. Vendor is the remembered harness for RepoPath, or
/// HomeViewModel.DefaultVendor when none was ever chosen there; Selected marks the entry that
/// matches SelectedRepoPath under HomeViewModel's own path comparison.
public sealed record RepositoryOption(string RepoPath, string Vendor, bool Selected);

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

    /// Reserved key for the not-yet-in-a-repository target. It is a normal AppState.HarnessByRepo
    /// key, so it round-trips and keeps its own remembered harness like any real repo path —
    /// but LAUNCHING it is not supported yet: AgentOrchestrator rejects a launch whose repo path
    /// fails Directory.Exists, which "" does. Until the daemon accepts a repo-less launch this is
    /// a storage key only.
    public const string ScratchRepoPath = "";

    /// A launch that started but handed back an id nothing can open. The session is real and running
    /// — it just has to be reached from the session list, so this is a launch-succeeded wording, not
    /// a failure one (spec §3, entry-point guards).
    public const string UnusableIdMessage = "Launched, but the session id was unusable — open it from the session list.";

    readonly IDaemonClientService _daemon;
    readonly IAppStateStore _state;
    readonly ILaunchClient _launch;
    readonly Func<Task<string[]>> _knownRepos;
    readonly Action<string>? _openSession;
    readonly Func<int>? _navigationGeneration;
    readonly Action<string, int>? _openSessionIfCurrent;
    readonly CompositeDisposable _disposables = new();

    string _selectedRepoPath = ScratchRepoPath;
    public string SelectedRepoPath {
        get => _selectedRepoPath;
        set => this.RaiseAndSetIfChanged(ref _selectedRepoPath, value);
    }

    string _selectedVendor = DefaultVendor;
    // Follows the repository — only ChooseHarnessAsync/SelectRepositoryAsync set it, so a caller
    // can never desync it from the persisted-or-default rule those two methods implement.
    public string SelectedVendor {
        get => _selectedVendor;
        private set => this.RaiseAndSetIfChanged(ref _selectedVendor, value);
    }

    bool _rememberHarness = true;
    public bool RememberHarness {
        get => _rememberHarness;
        set => this.RaiseAndSetIfChanged(ref _rememberHarness, value);
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
    public HomeViewModel(
            IDaemonClientService daemon, IAppStateStore state, ILaunchClient launch,
            Func<Task<string[]>> knownRepos, CancellationToken shutdown = default,
            Action<string>? openSession = null, Func<int>? navigationGeneration = null,
            Action<string, int>? openSessionIfCurrent = null) {
        _daemon = daemon;
        _state = state;
        _launch = launch;
        _knownRepos = knownRepos;
        _shutdown = shutdown;
        _openSession = openSession;
        _navigationGeneration = navigationGeneration;
        _openSessionIfCurrent = openSessionIfCurrent;

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
        // MainWindowViewModel.Agents/ConsentPromptViewModel.Pending): the cache is mutated on the
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

        StartCommand = ReactiveCommand.CreateFromTask(StartAsync);
    }

    // Constructor-scoped (like TrayViewModel/ActivityViewModel), not WhenActivated — the OAPH and
    // the Agents subscription above run for this object's whole lifetime, not a window's.
    public void Dispose() => _disposables.Dispose();

    /// Sets the selection and, when RememberHarness, persists it for SelectedRepoPath.
    /// RememberHarness = false skips the write only — it must never erase an existing choice.
    public async Task ChooseHarnessAsync(string vendor) {
        SelectedVendor = vendor;
        if (!RememberHarness) return;

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
        SelectedVendor = Lookup(saved.HarnessByRepo, repoPath) ?? DefaultVendor;
    }

    /// A session card's click (HomeView routes it here). No generation is involved — the click IS
    /// the current navigation, unlike the launch auto-open below.
    public void OpenSessionRequested(string agentId) => _openSession?.Invoke(agentId);

    async Task StartAsync() {
        var request = new LaunchRequest(_daemon.DaemonName, SelectedRepoPath, SelectedVendor, Goal);
        // Captured BEFORE the call, never after (spec §3): the whole point is to notice a navigation
        // that happened WHILE the launch was in flight.
        var generation = _navigationGeneration?.Invoke() ?? 0;

        var outcome = await _launch.StartAsync(request, _shutdown);
        if (!outcome.Started) {
            StartError = outcome.Error;
            return;
        }

        StartError = null;
        Goal = ""; // the launch really did start — the goal is spent either way
        if (NormalizeAgentId(outcome.AgentId) is not { } agentId) {
            StartError = UnusableIdMessage;
            return;
        }

        _openSessionIfCurrent?.Invoke(agentId, generation);
    }

    /// The daemon's status cache keys agents by Guid("N") — 32 hex digits — but the server hub
    /// returns the launch id as a DASHED Guid, so the shapes must be normalized here or the
    /// Resolving gate can never match the cache entry. Anything Guid.TryParse rejects cannot
    /// address a session and never reaches OpenSession (which would otherwise build a workspace
    /// that can only ever resolve to "session not found").
    internal static string? NormalizeAgentId(string? agentId) =>
        Guid.TryParse(agentId, out var parsed) ? parsed.ToString("N") : null;

    /// Repo paths compare the way the filesystem underneath them does: case-insensitively on
    /// Windows and macOS, case-sensitively on Linux where two checkouts differing only in case are
    /// genuinely different repositories. Applied on READ because System.Text.Json rebuilds the
    /// dictionary with a default (ordinal) comparer on load — a comparer set only at write time
    /// would not survive the round-trip.
    static readonly StringComparer PathComparer =
        OperatingSystem.IsLinux() ? StringComparer.Ordinal : StringComparer.OrdinalIgnoreCase;

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
