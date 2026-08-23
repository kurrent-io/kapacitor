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

/// The Home tab's view-model (Task 5, AI-2194): repository + harness picker, a free-text goal, and
/// the Start action that launches a session through ILaunchClient. Constructed once, like
/// TrayViewModel/ActivityViewModel — not gated behind IActivatableViewModel — since Harnesses and
/// Sessions must be live from construction, not deferred to a window's activation. Snapshots and
/// Agents are mutated on the daemon client's own background thread (same as
/// MainWindowViewModel/ConsentPromptViewModel), so both projections below ObserveOn
/// RxSchedulers.MainThreadScheduler BEFORE the operator that touches bound state — Task 6's
/// ItemsControl binding must never see a mutation off the UI thread.
public sealed class HomeViewModel : ReactiveObject, IDisposable {
    /// A repository with no remembered choice falls back to this — never to whatever vendor was
    /// selected for a DIFFERENT repository, which would leak a preference across repositories.
    public const string DefaultVendor = "claude";

    /// The "No repository" target: a session started against it runs in a daemon-owned worktree
    /// with no upstream checkout. Still a normal AppState.HarnessByRepo key, so it keeps its own
    /// remembered harness like any real repo path.
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

    public HomeViewModel(IDaemonClientService daemon, IAppStateStore state, ILaunchClient launch) {
        _daemon = daemon;
        _state = state;
        _launch = launch;

        // Never starts empty (task-5-brief decision 2): a null SupportedVendors means "daemon
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
        // daemon client's background thread.
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

    /// Sets the repository and restores that repository's remembered harness, or DefaultVendor
    /// when none — never the vendor a DIFFERENT repository had selected.
    public async Task SelectRepositoryAsync(string repoPath) {
        SelectedRepoPath = repoPath;
        var saved = await _state.LoadAsync();
        SelectedVendor = saved.HarnessByRepo is { } byRepo && byRepo.TryGetValue(repoPath, out var vendor)
            ? vendor
            : DefaultVendor;
    }

    async Task StartAsync() {
        var request = new LaunchRequest(_daemon.DaemonName, SelectedRepoPath, SelectedVendor, Goal);
        var outcome = await _launch.StartAsync(request, CancellationToken.None);
        if (outcome.Started) {
            StartError = null;
            Goal = "";
        } else {
            StartError = outcome.Error;
        }
    }

    static IReadOnlyDictionary<string, string> WithEntry(IReadOnlyDictionary<string, string>? existing, string key, string value) {
        var next = existing is null ? new Dictionary<string, string>() : new Dictionary<string, string>(existing);
        next[key] = value;
        return next;
    }
}
