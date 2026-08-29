using System.Collections.ObjectModel;
using System.Reactive.Disposables;
using System.Reactive.Disposables.Fluent;
using System.Reactive.Linq;
using Capacitor.Cli.Core.LocalIpc;
using DynamicData;
using DynamicData.Binding;
using ReactiveUI;

namespace Capacitor.App.ViewModels;

/// One repository level of the rail. IsNoRepository is the "" sentinel group — its single
/// nested worktree group renders headerless (spec §3). No ObserveOn: the outer pipeline
/// already marshaled (RailWorktreeViewModel's identical note).
public sealed class RailRepoViewModel : ReactiveObject, IDisposable {
    public string RootPath { get; }
    public string Label { get; }
    public bool IsNoRepository { get; }

    readonly ObservableAsPropertyHelper<string> _countText;
    public string CountText => _countText.Value;

    readonly ObservableCollectionExtended<RailWorktreeViewModel> _worktreesSource = new();
    public ReadOnlyObservableCollection<RailWorktreeViewModel> Worktrees { get; }

    // Main checkout first, then leaf label; path tiebreak keeps the order total.
    static readonly IComparer<RailWorktreeViewModel> WorktreeComparer =
        Comparer<RailWorktreeViewModel>.Create((a, b) => {
            var byMain = b.IsMainCheckout.CompareTo(a.IsMainCheckout);
            if (byMain != 0) return byMain;
            var byLabel = StringComparer.OrdinalIgnoreCase.Compare(a.Label, b.Label);
            return byLabel != 0 ? byLabel : string.CompareOrdinal(a.Path, b.Path);
        });

    readonly CompositeDisposable _disposables = new();

    public RailRepoViewModel(
            IGroup<AgentStatusDto, string, string> group, RailCollapseState collapse,
            IObservable<string?> selectedAgentId, IObservable<IReadOnlySet<string>> agentsWithPending,
            Action<string> open) {
        RootPath = group.Key;
        IsNoRepository = group.Key.Length == 0;
        // RepoLabel.Leaf, not the raw leaf: group.Key is a resolved main root, where the two agree —
        // and the "—" null arm can't trigger (the sentinel "" is the IsNoRepository branch).
        Label = IsNoRepository ? "No repository" : RepoLabel.Leaf(group.Key);

        _countText = group.Cache.CountChanged
            .Select(c => c == 1 ? "1 session" : $"{c} sessions")
            .ToProperty(this, x => x.CountText, initialValue: "")
            .DisposeWith(_disposables);

        Worktrees = new ReadOnlyObservableCollection<RailWorktreeViewModel>(_worktreesSource);
        group.Cache.Connect()
            .Group(dto => dto.RepoPath ?? "")
            .Transform(wt => new RailWorktreeViewModel(
                wt.Key, RootPath, showHeader: !IsNoRepository, wt.Cache, collapse, selectedAgentId,
                agentsWithPending, open))
            .DisposeMany()
            .SortAndBind(_worktreesSource, WorktreeComparer)
            .Subscribe()
            .DisposeWith(_disposables);
    }

    public void Dispose() => _disposables.Dispose();
}
