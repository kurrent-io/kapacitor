using Capacitor.App.ViewModels;

namespace Capacitor.App.Views;

/// The open/dirty state machine behind the NeedsUpdate-only rebuild cadence (spec §5) — pure, no
/// Avalonia types, so it's testable without a headless session. A model change only records the
/// latest value and marks dirty; OnNeedsUpdate is the sole place a rebuild is invoked, and only
/// when dirty. A change arriving while the native menu is open therefore becomes visible only at
/// the NEXT NeedsUpdate, never mid-display — the native menu shows a static snapshot while open.
public sealed class TrayMenuSync {
    TrayMenuModel? _latest;

    public bool Dirty { get; private set; }

    public void OnModelChanged(TrayMenuModel model) {
        _latest = model;
        Dirty = true;
    }

    public void OnNeedsUpdate(Action<TrayMenuModel> rebuild) {
        if (!Dirty || _latest is null) return;
        rebuild(_latest);
        Dirty = false;
    }
}
