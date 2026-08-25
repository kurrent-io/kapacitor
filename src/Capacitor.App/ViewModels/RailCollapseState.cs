using System.Reactive.Subjects;

namespace Capacitor.App.ViewModels;

/// Collapse choices for worktree rows, held OUTSIDE the group VMs: DynamicData drops and
/// re-forms a group whenever it empties or the cache resets, so state on the VM itself would
/// silently reset (spec §3). Default rule: collapsed iff main checkout. UI-thread only.
public sealed class RailCollapseState {
    readonly Dictionary<string, bool> _explicit = new(StringComparer.Ordinal);
    readonly Subject<string> _changes = new();

    /// Fires the path whose state changed — worktree VMs re-read IsCollapsed on it.
    public IObservable<string> Changes => _changes;

    public bool IsCollapsed(string path, bool isMainCheckout) =>
        _explicit.TryGetValue(path, out var collapsed) ? collapsed : isMainCheckout;

    public void Set(string path, bool collapsed) {
        _explicit[path] = collapsed;
        _changes.OnNext(path);
    }
}
