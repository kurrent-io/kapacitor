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
