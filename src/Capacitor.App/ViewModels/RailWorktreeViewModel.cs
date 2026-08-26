using System.Collections.ObjectModel;
using System.Globalization;
using System.Reactive;
using System.Reactive.Disposables;
using System.Reactive.Disposables.Fluent;
using System.Reactive.Linq;
using Capacitor.Cli.Core.LocalIpc;
using DynamicData;
using DynamicData.Binding;
using ReactiveUI;

namespace Capacitor.App.ViewModels;

/// One worktree level of the rail: the collapsible row plus its session rows. ShowHeader=false
/// is the No-repository group's single nested group — rendered headerless, sessions always
/// visible (spec §3). No ObserveOn here: the OUTER pipeline (SessionRailViewModel) marshals to
/// the UI thread before any group cache is mutated, so everything below already runs there.
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

    readonly ObservableAsPropertyHelper<int> _sessionCount;
    public int SessionCount => _sessionCount.Value;

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
            string path, string repoRoot, bool showHeader,
            IObservableCache<AgentStatusDto, string> sessionsCache, RailCollapseState collapse,
            IObservable<string?> selectedAgentId, Action<string> open) {
        Path = path;
        IsMainCheckout = PathEquals(path, repoRoot);
        Label = LabelFor(path, IsMainCheckout);
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

        // Same sharing rationale as above: CountText is projected off the count stream itself,
        // not via this.WhenAnyValue(x => x.SessionCount, ...).
        var count = sessionsCache.CountChanged.StartWith(sessionsCache.Count);
        _sessionCount = count
            .ToProperty(this, x => x.SessionCount, initialValue: sessionsCache.Count)
            .DisposeWith(_disposables);
        _countText = count.Select(c => c.ToString(CultureInfo.InvariantCulture))
            .ToProperty(this, x => x.CountText, initialValue: sessionsCache.Count.ToString(CultureInfo.InvariantCulture))
            .DisposeWith(_disposables);

        _needsYou = sessionsCache.Connect()
            .QueryWhenChanged(q => q.Items.Any(d => d.Status == "Failed"))
            .ToProperty(this, x => x.NeedsYou, initialValue: false)
            .DisposeWith(_disposables);

        _holdsSelected = sessionsCache.Connect().QueryWhenChanged(q => q.Keys.ToHashSet())
            .CombineLatest(selectedAgentId, (ids, sel) => sel is not null && ids.Contains(sel))
            .ToProperty(this, x => x.HoldsSelected, initialValue: false)
            .DisposeWith(_disposables);

        Sessions = new ReadOnlyObservableCollection<RailSessionViewModel>(_sessionsSource);
        sessionsCache.Connect()
            .Transform(dto => new RailSessionViewModel(dto, selectedAgentId, open))
            .DisposeMany()
            .SortAndBind(_sessionsSource, SessionComparer)
            .Subscribe()
            .DisposeWith(_disposables);
    }

    internal static string LabelFor(string path, bool isMainCheckout) =>
        isMainCheckout ? "main checkout"
        : System.IO.Path.GetFileName(System.IO.Path.TrimEndingDirectorySeparator(path));

    // Same platform rule HomeViewModel.PathComparer documents: case-insensitive except Linux.
    internal static bool PathEquals(string a, string b) =>
        string.Equals(
            System.IO.Path.TrimEndingDirectorySeparator(a), System.IO.Path.TrimEndingDirectorySeparator(b),
            OperatingSystem.IsLinux() ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase);

    public void Dispose() => _disposables.Dispose();
}
