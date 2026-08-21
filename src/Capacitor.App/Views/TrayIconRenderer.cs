using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Capacitor.App.ViewModels;

namespace Capacitor.App.Views;

/// Draws one 32x32 WindowIcon per (TrayState, capped Running count) — spec §4. The base is
/// ProductIcon.Bitmap (the product mark, Assets/kcap-icon.png) drawn scaled to fill the canvas;
/// state is a small overlay in the bottom-right corner, not a swapped glyph — Running draws
/// CountBadge(count) over a filled dark-green circle (legible against the burgundy mark), every
/// other state a plain color dot from the shared StatusColors palette (also used by MainWindow's
/// status line, so the window and the tray icon can never disagree). Cached per key so the same
/// (state, count) always returns the same WindowIcon reference — bitmap pixel correctness is
/// manual macOS verification, not unit-tested. UI-thread-only (MenuModel is delivered on
/// RxSchedulers.MainThreadScheduler), so the cache dictionary needs no locking.
public static class TrayIconRenderer {
    const int IconSize = 32;
    const int OverlayDiameter = 12; // spec §4: "~12px" bottom-right state overlay
    const double OverlayMargin = 1;
    const int MaxDigitCount = 9; // above this, CountBadge collapses to "9+"
    const int CountCap = 10;     // cache-key clamp: every count >= this renders the same "9+" badge

    const string BadgeBackgroundHex = "#1B5E20"; // dark green — legible under the white numeral

    static readonly IBrush BadgeText = Brushes.White;
    static readonly IBrush BadgeBackground = new SolidColorBrush(Color.Parse(BadgeBackgroundHex));
    static readonly Dictionary<(TrayState State, int CappedCount), WindowIcon> Cache = new();

    public static WindowIcon Get(TrayState state, int count) {
        var key = (state, CappedCount: state == TrayState.Running ? Math.Clamp(count, 0, CountCap) : 0);
        if (Cache.TryGetValue(key, out var cached)) return cached;

        var icon = Render(state, key.CappedCount);
        Cache[key] = icon;
        return icon;
    }

    internal static string CountBadge(int count) => count switch {
        <= 0 => "0",
        <= MaxDigitCount => count.ToString(CultureInfo.InvariantCulture),
        _ => "9+",
    };

    static WindowIcon Render(TrayState state, int cappedCount) {
        var bitmap = new RenderTargetBitmap(new PixelSize(IconSize, IconSize));
        using (var context = bitmap.CreateDrawingContext()) {
            context.DrawImage(ProductIcon.Bitmap, new Rect(0, 0, IconSize, IconSize));
            if (state == TrayState.Running) DrawCountBadge(context, cappedCount);
            else DrawStatusDot(context, state);
        }
        return new WindowIcon(bitmap);
    }

    static void DrawStatusDot(DrawingContext context, TrayState state) {
        var brush = new SolidColorBrush(Color.Parse(StatusHexFor(state)));
        var (center, radius) = OverlayCircle();
        context.DrawEllipse(brush, null, center, radius, radius);
    }

    static void DrawCountBadge(DrawingContext context, int cappedCount) {
        var (center, radius) = OverlayCircle();
        context.DrawEllipse(BadgeBackground, null, center, radius, radius);

        var text = new FormattedText(
            CountBadge(cappedCount), CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
            Typeface.Default, 8, BadgeText);
        context.DrawText(text, center - new Point(text.Width / 2, text.Height / 2));
    }

    static (Point Center, double Radius) OverlayCircle() {
        const double radius = OverlayDiameter / 2.0;
        var center = new Point(IconSize - radius - OverlayMargin, IconSize - radius - OverlayMargin);
        return (center, radius);
    }

    static string StatusHexFor(TrayState state) => state switch {
        TrayState.Stopped    => StatusColors.Unavailable,
        TrayState.Connecting => StatusColors.InProgress,
        TrayState.Idle       => StatusColors.Connected,
        TrayState.Attention  => StatusColors.Disrupted,
        _                    => StatusColors.Disrupted,
    };
}
