using System.Collections.ObjectModel;
using System.Globalization;
using System.Reactive;
using System.Reactive.Disposables;
using System.Reactive.Disposables.Fluent;
using System.Reactive.Linq;
using Capacitor.App.Services;
using DynamicData;
using DynamicData.Binding;
using ReactiveUI.Reactive;

namespace Capacitor.App.ViewModels;

/// One worktree level of the rail: the collapsible row plus its session rows. ShowHeader=false
/// is the No-repository group's single nested group — rendered headerless, sessions always
/// visible. No ObserveOn here: SessionRailViewModel marshals the group cache pipeline
/// and agentsWithPending to the UI thread once at the root before either reaches this class.
public sealed class RailWorktreeViewModel : ReactiveObject, IDisposable {
    public string Path { get; }
    public string Label { get; }
    public bool IsMainCheckout { get; }
    public bool ShowHeader { get; }
    public ReactiveCommand<Unit, Unit> ToggleCommand { get; }

    readonly ObservableAsPropertyHelper<bool> _isExpanded;
    public bool IsExpanded => _isExpanded.Value;

    readonly ObservableAsPropertyHelper<bool> _sessionsVisible;
    public bool SessionsVisible => _sessionsVisible.Value;

    readonly ObservableAsPropertyHelper<string> _countText;
    public string CountText => _countText.Value;

    readonly ObservableAsPropertyHelper<bool> _needsYou;
    public bool NeedsYou => _needsYou.Value;

    readonly ObservableAsPropertyHelper<bool> _holdsSelected;
    public bool HoldsSelected => _holdsSelected.Value;

    readonly ObservableCollectionExtended<RailSessionViewModel> _sessionsSource = new();
    public ReadOnlyObservableCollection<RailSessionViewModel> Sessions { get; }

    static readonly IComparer<RailSessionViewModel> SessionComparer =
        Comparer<RailSessionViewModel>.Create((a, b) => {
            var byCreated = a.CreatedAt.CompareTo(b.CreatedAt);
            return byCreated != 0 ? byCreated : string.CompareOrdinal(a.Id, b.Id);
        });

    readonly CompositeDisposable _disposables = new();

    public RailWorktreeViewModel(
            string path, Func<string, string> resolveRepoRoot, bool showHeader,
            IObservableCache<AgentRow, string> sessionsCache, RailCollapseState collapse,
            IObservable<string?> selectedAgentId, IObservable<IReadOnlySet<string>> agentsWithPending,
            Action<string> openLocal, Action<string> openRemoteInWeb) {
        Path = path;
        // Every row in one worktree group shares CheckoutLabel by construction — any member
        // names a remote pseudo-checkout (labeled by the daemon it runs on, never "main"); an
        // empty cache (a leaf-VM unit test building this directly) reads as local, same as a
        // local row would. A local checkout resolves against its OWN path — the same heuristic
        // GitRepository.ResolveMainRepoRoot already applies to a linked worktree's .git file.
        var remoteLabel = sessionsCache.Items.Count > 0 ? sessionsCache.Items[0].CheckoutLabel : "";
        if (remoteLabel.Length == 0) {
            var repoRoot = PlatformPaths.Normalize(resolveRepoRoot(path));
            IsMainCheckout = CheckoutLabel.IsMain(path, repoRoot);
            Label = CheckoutLabel.Format(path, repoRoot);
        } else {
            IsMainCheckout = false;
            Label = remoteLabel;
        }
        ShowHeader = showHeader;

        // Both IsExpanded and SessionsVisible are projected off this SAME stream, rather than
        // SessionsVisible re-observing the sibling property via this.WhenAnyValue: that call
        // routes through ReactiveUI's ObservableForProperty/RxAppBuilder global init, which is
        // only reliably primed when some other test has already pumped the headless dispatcher
        // first — the same AppBuilder.HasBeenBuilt race AvaloniaSession.cs documents for
        // RxSchedulers.MainThreadScheduler. Sharing the source avoids that dependency entirely.
        var expanded = collapse.Changes.Where(p => p == path).Select(_ => Unit.Default)
            .StartWith(Unit.Default)
            .Select(_ => !collapse.IsCollapsed(path));
        _isExpanded = expanded
            .ToProperty(this, x => x.IsExpanded)
            .DisposeWith(_disposables);
        _sessionsVisible = expanded.Select(isExpanded => isExpanded || !showHeader)
            .ToProperty(this, x => x.SessionsVisible)
            .DisposeWith(_disposables);
        ToggleCommand = ReactiveCommand.Create(() => collapse.Set(path, IsExpanded));
        _disposables.Add(ToggleCommand);

        _countText = sessionsCache.CountChanged
            .Select(c => c.ToString(CultureInfo.InvariantCulture))
            .ToProperty(this, x => x.CountText, initialValue: sessionsCache.Count.ToString(CultureInfo.InvariantCulture))
            .DisposeWith(_disposables);

        // Both projections compare against AgentRow.Id (the logical agent id), never the cache's
        // own key — that key is source-scoped ("local:"/"remote:" prefixed) so it never matches
        // selectedAgentId or an agentsWithPending member verbatim.
        _needsYou = sessionsCache.Connect().QueryWhenChanged()
            .CombineLatest(agentsWithPending, (q, set) =>
                q.Items.Any(r => SessionStatusDots.NeedsAttention(r) || set.Contains(r.Id)))
            .ToProperty(this, x => x.NeedsYou, initialValue: false)
            .DisposeWith(_disposables);

        _holdsSelected = sessionsCache.Connect().QueryWhenChanged()
            .CombineLatest(selectedAgentId, (q, sel) => sel is not null && q.Items.Any(r => r.Id == sel))
            .ToProperty(this, x => x.HoldsSelected, initialValue: false)
            .DisposeWith(_disposables);

        Sessions = new ReadOnlyObservableCollection<RailSessionViewModel>(_sessionsSource);
        sessionsCache.Connect()
            .Transform(row => new RailSessionViewModel(row, selectedAgentId, agentsWithPending, openLocal, openRemoteInWeb))
            .DisposeMany()
            .SortAndBind(_sessionsSource, SessionComparer)
            .Subscribe()
            .DisposeWith(_disposables);
    }

    public void Dispose() => _disposables.Dispose();
}
