using System.Collections.ObjectModel;
using System.Reactive.Disposables;
using System.Reactive.Disposables.Fluent;
using System.Reactive.Linq;
using Capacitor.App.Services;
using DynamicData;
using DynamicData.Binding;
using ReactiveUI;

namespace Capacitor.App.ViewModels;

/// One repository level of the rail. IsNoRepository is the "No repository" sentinel group — its
/// single nested worktree group renders headerless (spec §3). No ObserveOn: the outer pipeline
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
            IGroup<AgentRow, string, string> group, RailCollapseState collapse,
            IObservable<string?> selectedAgentId, IObservable<IReadOnlySet<string>> agentsWithPending,
            Func<string, string> resolveRepoRoot, Action<string> openLocal, Action<string> openRemoteInWeb) {
        RootPath = group.Key;
        // Rows in one group share RepoGroupLabel by construction — any member names it.
        Label = group.Cache.Items[0].RepoGroupLabel;
        IsNoRepository = Label == "No repository";

        _countText = group.Cache.CountChanged
            .Select(c => c == 1 ? "1 session" : $"{c} sessions")
            .ToProperty(this, x => x.CountText, initialValue: "")
            .DisposeWith(_disposables);

        Worktrees = new ReadOnlyObservableCollection<RailWorktreeViewModel>(_worktreesSource);
        group.Cache.Connect()
            .Group(SessionRailViewModel.WorktreeKeyFor)
            .Transform(wt => new RailWorktreeViewModel(
                wt.Key, resolveRepoRoot, showHeader: !IsNoRepository, wt.Cache, collapse, selectedAgentId,
                agentsWithPending, openLocal, openRemoteInWeb))
            .DisposeMany()
            .SortAndBind(_worktreesSource, WorktreeComparer)
            .Subscribe()
            .DisposeWith(_disposables);
    }

    public void Dispose() => _disposables.Dispose();
}
