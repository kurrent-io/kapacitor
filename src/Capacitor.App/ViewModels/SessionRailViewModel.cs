using System.Collections.ObjectModel;
using System.Reactive.Disposables;
using System.Reactive.Disposables.Fluent;
using System.Reactive.Linq;
using System.Reactive.Subjects;
using Capacitor.App.Services;
using Capacitor.Cli.Core;
using Capacitor.Cli.Core.LocalIpc;
using DynamicData;
using DynamicData.Binding;
using ReactiveUI;

namespace Capacitor.App.ViewModels;

/// The session rail's root: repository → worktree → session over daemon.Agents (spec §3).
/// Ctor-scoped and disposable like HomeViewModel — the tree must be live from construction.
/// ONE ObserveOn at the top of the pipeline: nested group caches are mutated by this outer
/// pipeline, so every inner Connect() below already fires on the UI thread.
public sealed class SessionRailViewModel : ReactiveObject, IDisposable {
    readonly IDaemonClientService _daemon;
    readonly RailCollapseState _collapse = new();
    readonly Dictionary<string, string> _rootByPath = new(StringComparer.Ordinal);
    readonly Func<string, string> _resolveRepoRoot;
    readonly CompositeDisposable _disposables = new();

    // SelectedAgentId is fed to the nested VMs via this subject, not this.WhenAnyValue: that
    // call routes through ReactiveUI's ObservableForProperty/RxAppBuilder global init, which is
    // only reliably primed when some other test has already pumped the headless dispatcher
    // first — the same race RailWorktreeViewModel's IsExpanded/SessionsVisible sharing avoids
    // (see its header comment) and AvaloniaSession.cs documents for MainThreadScheduler.
    readonly BehaviorSubject<string?> _selectedAgentIdChanges = new(null);

    string? _selectedAgentId;
    public string? SelectedAgentId {
        get => _selectedAgentId;
        set {
            this.RaiseAndSetIfChanged(ref _selectedAgentId, value);
            _selectedAgentIdChanges.OnNext(value);
        }
    }

    readonly ObservableAsPropertyHelper<bool> _isEmpty;
    public bool IsEmpty => _isEmpty.Value;

    readonly ObservableAsPropertyHelper<string> _hostedText;
    public string HostedText => _hostedText.Value;

    readonly ObservableCollectionExtended<RailRepoViewModel> _reposSource = new();
    public ReadOnlyObservableCollection<RailRepoViewModel> Repos { get; }

    // No-repository last, then leaf label; root path tiebreak.
    static readonly IComparer<RailRepoViewModel> RepoComparer =
        Comparer<RailRepoViewModel>.Create((a, b) => {
            var byNoRepo = a.IsNoRepository.CompareTo(b.IsNoRepository);
            if (byNoRepo != 0) return byNoRepo;
            var byLabel = StringComparer.OrdinalIgnoreCase.Compare(a.Label, b.Label);
            return byLabel != 0 ? byLabel : string.CompareOrdinal(a.RootPath, b.RootPath);
        });

    /// resolveRepoRoot defaults to the real .git-reading heuristic; tests inject a pure one.
    /// agentsWithPending defaults to an always-empty set so callers that don't wire the
    /// permission service still compile and render.
    public SessionRailViewModel(
            IDaemonClientService daemon, Action<string> openSession,
            Func<string, string>? resolveRepoRoot = null,
            IObservable<IReadOnlySet<string>>? agentsWithPending = null) {
        _daemon = daemon;
        _resolveRepoRoot = resolveRepoRoot ?? GitRepository.ResolveMainRepoRoot;
        // Not disposed with the rest: same as RailCollapseState's Changes subject, a bare
        // signaling Subject the class never tears down, so a post-Dispose set never throws.
        IObservable<string?> selected = _selectedAgentIdChanges;
        // PermissionService.AgentsWithPending emits from background continuations; marshal once
        // here so every nested OAPH downstream (RailSessionViewModel, RailWorktreeViewModel) sees
        // it on the UI thread without adding its own ObserveOn.
        var pending = (agentsWithPending ?? Observable.Return((IReadOnlySet<string>)new HashSet<string>()))
            .ObserveOn(RxSchedulers.MainThreadScheduler);

        _isEmpty = daemon.Agents.CountChanged
            .Select(c => c == 0)
            .ObserveOn(RxSchedulers.MainThreadScheduler)
            .ToProperty(this, x => x.IsEmpty, initialValue: daemon.Agents.Count == 0)
            .DisposeWith(_disposables);
        _hostedText = daemon.Agents.CountChanged
            .Select(c => $"{c} hosted")
            .ObserveOn(RxSchedulers.MainThreadScheduler)
            .ToProperty(this, x => x.HostedText, initialValue: $"{daemon.Agents.Count} hosted")
            .DisposeWith(_disposables);

        Repos = new ReadOnlyObservableCollection<RailRepoViewModel>(_reposSource);
        daemon.Agents.Connect()
            .ObserveOn(RxSchedulers.MainThreadScheduler)
            .Group(RepoRootFor)
            .Transform(g => new RailRepoViewModel(g, _collapse, selected, pending, openSession))
            .DisposeMany()
            .SortAndBind(_reposSource, RepoComparer)
            .Subscribe()
            .DisposeWith(_disposables);
    }

    /// The launch auto-open's counterpart: a session opened into an explicitly collapsed
    /// worktree must never highlight an invisible row.
    public void NotifySessionOpened(string agentId) {
        var dto = _daemon.Agents.Lookup(agentId);
        if (!dto.HasValue || WorktreeKeyFor(dto.Value) is not { Length: > 0 } path) return;
        _collapse.Set(path, collapsed: false);
    }

    internal static string WorktreeKeyFor(AgentStatusDto dto) =>
        CheckoutLabel.CheckoutPathFor(dto) ?? dto.RepoPath ?? "";

    // Memoized: ResolveMainRepoRoot reads .git files — cheap once, not per-changeset cheap; a
    // path's resolution never changes within a daemon's lifetime. A current daemon already sends
    // the repository, so this only ever rewrites an older daemon's checkout path.
    string RepoRootFor(AgentStatusDto dto) {
        if (dto.RepoPath is not { Length: > 0 } path) return "";
        if (_rootByPath.TryGetValue(path, out var root)) return root;
        root = _resolveRepoRoot(path);
        _rootByPath[path] = root;
        return root;
    }

    public void Dispose() => _disposables.Dispose();
}
