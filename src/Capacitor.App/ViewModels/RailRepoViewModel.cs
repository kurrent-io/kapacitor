using System.Collections.ObjectModel;
using System.Reactive.Disposables;
using System.Reactive.Disposables.Fluent;
using System.Reactive.Linq;
using Capacitor.App.Services;
using DynamicData;
using DynamicData.Binding;
using ReactiveUI.Reactive;

namespace Capacitor.App.ViewModels;

/// One repository level of the rail. IsNoRepository is the "No repository" sentinel group — its
/// single nested worktree group renders headerless. No ObserveOn: the outer pipeline already
/// marshaled (RailWorktreeViewModel's identical note).
public sealed class RailRepoViewModel : ReactiveObject, IDisposable {
    public string RootPath { get; }
    /// RootPath in a form worth showing a user — a tooltip, never a comparer or collapse key.
    public string RootDisplay { get; }
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
        RootDisplay = DisplayFor(RootPath, Label);

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

    // RootPath's own prefixes ("repo:", "path:", "daemon:{owner}/{daemon}:{path}") are a group
    // identity, not display text (RepoIdentity's own rule) — this is the one place that reads
    // them anyway, to turn the key into something worth a tooltip.
    static string DisplayFor(string rootPath, string label) {
        if (rootPath.StartsWith("repo:", StringComparison.Ordinal))
            return rootPath["repo:".Length..];
        if (rootPath.StartsWith("path:", StringComparison.Ordinal))
            return rootPath["path:".Length..];
        if (rootPath.StartsWith("daemon:", StringComparison.Ordinal)) {
            var rest = rootPath["daemon:".Length..]; // "{owner}/{daemon}:{path}"
            var pathAt = rest.IndexOf(':');
            var ownerDaemon = pathAt < 0 ? rest : rest[..pathAt];
            var path = pathAt < 0 ? "" : rest[(pathAt + 1)..];
            var daemon = ownerDaemon[(ownerDaemon.LastIndexOf('/') + 1)..];
            return path.Length == 0 ? label : $"{path} on {daemon}";
        }
        return rootPath;
    }

    public void Dispose() => _disposables.Dispose();
}
