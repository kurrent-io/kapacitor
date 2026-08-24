using System.Collections.ObjectModel;
using System.Reactive;
using System.Reactive.Disposables;
using System.Reactive.Disposables.Fluent;
using System.Reactive.Linq;
using Capacitor.App.Services;
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

    readonly IDaemonClientService _daemon;
    readonly IAppStateStore _state;
    readonly ILaunchClient _launch;
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

    public HomeViewModel(
            IDaemonClientService daemon, IAppStateStore state, ILaunchClient launch,
            CancellationToken shutdown = default) {
        _daemon = daemon;
        _state = state;
        _launch = launch;
        _shutdown = shutdown;

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
    /// Sources: remembered HarnessByRepo keys, distinct agent RepoPaths, and the current selection
    /// (a picker-added repo with no remembered harness and no agent yet lives nowhere else).
    /// Deduped under PathComparer with remembered keys added first, so where two casings are one
    /// repository the casing the user picked is the one displayed. Scratch is always last; the
    /// view renders it separated.
    public async Task<IReadOnlyList<RepositoryOption>> ListRepositoriesAsync() {
        var byRepo = (await _state.LoadAsync()).HarnessByRepo;

        var seen = new HashSet<string>(PathComparer);
        var paths = new List<string>();
        void Add(string? path) {
            if (!string.IsNullOrEmpty(path) && seen.Add(path)) paths.Add(path);
        }

        foreach (var key in byRepo?.Keys ?? [])
            Add(key);
        foreach (var agent in _daemon.Agents.Items)
            Add(agent.RepoPath);
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

    async Task StartAsync() {
        var request = new LaunchRequest(_daemon.DaemonName, SelectedRepoPath, SelectedVendor, Goal);
        var outcome = await _launch.StartAsync(request, _shutdown);
        if (outcome.Started) {
            StartError = null;
            Goal = "";
        } else {
            StartError = outcome.Error;
        }
    }

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
