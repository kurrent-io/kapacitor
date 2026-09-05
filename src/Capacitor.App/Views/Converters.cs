using System.Globalization;
using Avalonia.Data.Converters;
using Avalonia.Media;
using Avalonia.Media.Immutable;

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

/// Activity row outcome badge: the same Connected/Disrupted greens and reds as the status dots,
/// so "allowed" matches a live session indicator rather than a darker Material green.
/// ActivityRow stays a plain record (no Avalonia types) — color lives here for the same
/// UI-thread-affinity reason MainWindowViewModel.DotBrush documents.
public sealed class OutcomeBrushConverter : IValueConverter {
    public static readonly OutcomeBrushConverter Instance = new();

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        new SolidColorBrush(Color.Parse(value is true ? StatusColors.Connected : StatusColors.Disrupted));

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

/// Opacity for the off-tab terminal: it must stay measured (so the PTY gets the real pane size),
/// so it is faded rather than collapsed.
public sealed class BoolToOpacityConverter : IValueConverter {
    public static readonly BoolToOpacityConverter Instance = new();

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is true ? 1.0 : 0.0;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

/// A visible-but-faded control is still announced as onscreen by default; the inactive terminal
/// must be reported offscreen instead.
public sealed class OffscreenWhenInactiveConverter : IValueConverter {
    public static readonly OffscreenWhenInactiveConverter Instance = new();

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is true ? Avalonia.Automation.IsOffscreenBehavior.Default : Avalonia.Automation.IsOffscreenBehavior.Offscreen;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

/// Tool-call outcome colour: the same Connected/Disrupted greens and reds as the status dots
/// and Activity "allowed"/"denied", so a settled tool never invents a second success green.
public sealed class ToolOutcomeBrushConverter : IValueConverter {
    public static readonly ToolOutcomeBrushConverter Instance = new();

    static readonly IBrush Success = new ImmutableSolidColorBrush(Color.Parse(StatusColors.Connected));
    static readonly IBrush Failure = new ImmutableSolidColorBrush(Color.Parse(StatusColors.Disrupted));

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is true ? Failure : Success;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
