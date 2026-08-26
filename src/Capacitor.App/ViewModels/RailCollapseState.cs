using System.Reactive.Subjects;

namespace Capacitor.App.ViewModels;

/// Collapse choices for worktree rows, held OUTSIDE the group VMs: DynamicData drops and
/// re-forms a group whenever it empties or the cache resets, so state on the VM itself would
/// silently reset (spec §3). Everything starts expanded — the rail only carries current
/// sessions, so a fresh row is worth seeing (owner revision of the canvas's collapsed-main
/// default); collapsing is an explicit choice that then sticks. UI-thread only.
public sealed class RailCollapseState {
    readonly Dictionary<string, bool> _explicit = new(StringComparer.Ordinal);
    readonly Subject<string> _changes = new();

    /// Fires the path whose state changed — worktree VMs re-read IsCollapsed on it.
    public IObservable<string> Changes => _changes;

    public bool IsCollapsed(string path) =>
        _explicit.TryGetValue(path, out var collapsed) && collapsed;

    public void Set(string path, bool collapsed) {
        _explicit[path] = collapsed;
        _changes.OnNext(path);
    }
}
