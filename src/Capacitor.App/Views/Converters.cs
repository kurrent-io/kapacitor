using System.Globalization;
using Avalonia.Data.Converters;
using Avalonia.Media;

namespace Capacitor.App.Views;

/// The rail's repo headers render as small caps (design canvas); Avalonia has no text-transform,
/// so the casing happens here rather than in the VM, keeping Label reusable as-is in tooltips.
public sealed class UppercaseConverter : IValueConverter {
    public static readonly UppercaseConverter Instance = new();

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is string s ? s.ToUpperInvariant() : value;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

/// Activity row outcome badge (spec §7): green for allowed, red for denied. ActivityRow stays a
/// plain record (no Avalonia types, so its mapping can construct off any thread) — the color
/// lives here rather than as a cached Brush field, for the same UI-thread-affinity reason
/// MainWindowViewModel.DotBrush documents.
public sealed class OutcomeBrushConverter : IValueConverter {
    public static readonly OutcomeBrushConverter Instance = new();

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        new SolidColorBrush(Color.Parse(value is true ? "#2E7D32" : "#D32F2F"));

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
