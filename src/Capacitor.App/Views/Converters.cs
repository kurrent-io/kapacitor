using System.Globalization;
using Avalonia.Data.Converters;

namespace Capacitor.App.Views;

/// Dims the Agents grid when GridEnabled is false (spec §8) — a single-purpose converter, not a
/// general bool-to-double one; deliberately not bidirectional (opacity never writes back).
public sealed class GridEnabledOpacityConverter : IValueConverter {
    public static readonly GridEnabledOpacityConverter Instance = new();

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is true ? 1.0 : 0.5;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

/// Empty state (spec §8): "No agents running" shows only while the grid is enabled (Connected)
/// AND the bound collection is empty — a retained-but-stale cache while disconnected must not
/// show this text, since the daemon's own status area already explains that state.
public sealed class EmptyStateVisibleConverter : IMultiValueConverter {
    public static readonly EmptyStateVisibleConverter Instance = new();

    public object? Convert(IList<object?> values, Type targetType, object? parameter, CultureInfo culture) =>
        values is [true, int count] && count == 0;
}

/// Grid header row: hidden when the Agents collection is empty (a naked column-header row above
/// "No agents running" reads as noise), visible as soon as at least one row exists — independent
/// of GridEnabled, since rows (and therefore their header) persist across disconnects (spec §8).
/// Single-purpose converter, not a general count-to-bool one — mirrors GridEnabledOpacityConverter.
public sealed class HeaderRowVisibleConverter : IValueConverter {
    public static readonly HeaderRowVisibleConverter Instance = new();

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is int count && count > 0;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
