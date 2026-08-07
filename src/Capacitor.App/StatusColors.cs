namespace Capacitor.App;

/// The one status-dot palette (spec §4/§8) — hex-only constants (plain strings, not Brush
/// instances) shared by MainWindowViewModel's status line dot and TrayIconRenderer's per-state
/// tray-icon overlay, so the window and the tray icon can never disagree about what a color
/// means. Callers build their own Brush per use (see MainWindowViewModel.DotBrush) rather than
/// caching one here, for the same UI-thread-affinity reason documented there.
public static class StatusColors {
    public const string Connected   = "#4CAF50";
    public const string InProgress  = "#FFB300";
    public const string Disrupted   = "#E53935";
    public const string Unavailable = "#9E9E9E";
}
