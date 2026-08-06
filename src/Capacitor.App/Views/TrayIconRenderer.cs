using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Capacitor.App.ViewModels;

namespace Capacitor.App.Views;

/// Draws one monochrome 32x32 WindowIcon per (TrayState, capped Running count) — spec §4. Glyph
/// shapes are StreamGeometry resources in Assets/TrayGlyphs.axaml (merged into App.axaml, keyed
/// TrayGlyphStopped/Connecting/Idle/Running/Attention); Running additionally overlays
/// CountBadge(count) via FormattedText, bottom-right. Cached per key so the same (state, count)
/// always returns the same WindowIcon reference — bitmap pixel correctness is manual macOS
/// verification, not unit-tested. UI-thread-only (MenuModel is delivered on
/// RxSchedulers.MainThreadScheduler), so the cache dictionary needs no locking.
public static class TrayIconRenderer {
    const int Size = 32;
    const int MaxDigitCount = 9; // above this, CountBadge collapses to "9+"
    const int CountCap = 10;     // cache-key clamp: every count >= this renders the same "9+" badge

    static readonly IBrush GlyphBrush = new SolidColorBrush(Color.Parse("#808080"));
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
        var geometry = ResolveGeometry(state);
        var bitmap = new RenderTargetBitmap(new PixelSize(Size, Size));
        using (var context = bitmap.CreateDrawingContext()) {
            context.DrawGeometry(GlyphBrush, null, geometry);
            if (state == TrayState.Running) DrawCountBadge(context, cappedCount);
        }
        return new WindowIcon(bitmap);
    }

    static void DrawCountBadge(DrawingContext context, int cappedCount) {
        var text = new FormattedText(
            CountBadge(cappedCount), CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
            Typeface.Default, 12, GlyphBrush);
        context.DrawText(text, new Point(Size - text.Width - 1, Size - text.Height - 1));
    }

    static Geometry ResolveGeometry(TrayState state) {
        var key = state switch {
            TrayState.Stopped    => "TrayGlyphStopped",
            TrayState.Connecting => "TrayGlyphConnecting",
            TrayState.Idle       => "TrayGlyphIdle",
            TrayState.Running    => "TrayGlyphRunning",
            TrayState.Attention  => "TrayGlyphAttention",
            _                    => "TrayGlyphAttention",
        };
        if (Application.Current!.TryGetResource(key, null, out var value) && value is Geometry geometry)
            return geometry;
        throw new InvalidOperationException($"Missing tray glyph resource '{key}' — check Assets/TrayGlyphs.axaml.");
    }
}
