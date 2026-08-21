using Avalonia.Controls;
using Avalonia.Media.Imaging;
using Avalonia.Platform;

namespace Capacitor.App.Views;

/// The product mark (Assets/kcap-icon.png), loaded once from its avares:// URI — spec §4's tray
/// base bitmap, and also MainWindow's and the startup-error window's Icon, so every window
/// surface uses the same asset. TrayIconRenderer draws Bitmap scaled into each per-state
/// composite; MainWindow.axaml sets its own Icon via the same URI directly (Avalonia's
/// IconTypeConverter resolves avares:// strings without needing this class), so only the
/// startup-error window (built entirely in code) consumes WindowIcon here.
static class ProductIcon {
    const string AssetUri = "avares://Kurrent Capacitor/Assets/kcap-icon.png";

    static readonly Lazy<Bitmap> LazyBitmap = new(() => new Bitmap(AssetLoader.Open(new Uri(AssetUri))));
    static readonly Lazy<WindowIcon> LazyWindowIcon = new(() => new WindowIcon(LazyBitmap.Value));

    public static Bitmap Bitmap => LazyBitmap.Value;
    public static WindowIcon WindowIcon => LazyWindowIcon.Value;
}
