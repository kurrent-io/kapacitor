using System.Globalization;
using Avalonia.Controls;
using Avalonia.Data.Converters;
using Capacitor.App.Services;

namespace Capacitor.App.Views;

/// The session workspace: DataContext is supplied externally (a plainly-constructed
/// WorkspaceViewModel) -- same contract as HomeView/MainWindow, this view never builds its own VM.
///
/// TerminalHost hosts SvcSystems.UI.Terminal's own TerminalControl directly (no custom host/
/// wrapper needed) -- decompile-verified (Task 12 discovery) that TerminalControl already calls
/// its Model's Resize(width, height, textWidth, textHeight) itself, from BOTH its ModelProperty
/// change handler (a reattach's fresh Model gets sized to the control's CURRENT bounds
/// immediately) and its inner surface's own OnSizeChanged (an actual window/pane resize). Task
/// 11's "the view must call Model.Resize(...) from its bounds-changed handling" obligation is
/// therefore satisfied by USING the real vendor control rather than by re-implementing what it
/// already does -- a hand-rolled bounds-changed handler here would only risk double-invoking
/// Resize with worse (font-metric-unaware) width/height than TerminalControl's own
/// _consoleTextSize-based computation.
public partial class WorkspaceView : UserControl {
    public WorkspaceView() {
        InitializeComponent();
        // Keyboard focus is a view concern: the control draws its filled caret (and receives
        // keystrokes) only while focused, and nothing else focuses it when a session opens or
        // reattaches — Model assignment is exactly the "terminal became live" moment.
        TerminalHost.PropertyChanged += (_, e) => {
            if (e.Property == SvcSystems.UI.Terminal.TerminalControl.ModelProperty && TerminalHost.Model is not null)
                TerminalHost.Focus();
        };
    }
}

/// ITerminalSurface -&gt; TerminalControlModel? bridge for TerminalHost's Model binding.
/// ITerminalSurface (the VM-facing seam, deliberately Avalonia/SvcSystems-free per its own doc
/// comment) has no Model member, so a plain property-path binding can't reach it -- this converter
/// downcasts to the production XtermTerminalSurface and returns null for anything else (no
/// surface yet, or a test's FakeTerminalSurface), which TerminalControl already renders as an
/// empty pane (its own Render short-circuits on a null Model).
public sealed class TerminalSurfaceModelConverter : IValueConverter {
    public static readonly TerminalSurfaceModelConverter Instance = new();

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        (value as XtermTerminalSurface)?.Model;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

/// Single-tab-strip banner visibility: true when TerminalTabViewModel.State.Phase equals the
/// phase named by ConverterParameter (e.g. "Connecting", "Exited") -- one converter reused across
/// every single-phase banner instead of a dedicated bool-returning class per phase.
public sealed class TerminalPhaseIsConverter : IValueConverter {
    public static readonly TerminalPhaseIsConverter Instance = new();

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is TerminalSessionState state && parameter is string phaseName
        && Enum.TryParse<TerminalSessionPhase>(phaseName, ignoreCase: true, out var phase)
        && state.Phase == phase;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

/// The read-only warning banner: visible only for a read-only Attached session — the one case
/// where the banner explains otherwise-dead keystrokes. A read-write attach shows no banner
/// (owner decision after QA: it overlaid the terminal).
public sealed class TerminalReadOnlyBannerVisibleConverter : IValueConverter {
    public static readonly TerminalReadOnlyBannerVisibleConverter Instance = new();

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is TerminalSessionState { Phase: TerminalSessionPhase.Attached, ReadOnly: true };

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

/// Detached and Failed share one "not attached, offer Reattach" banner (same affordance, same
/// recovery action) rather than two near-identical banners each carrying their own Reattach
/// button -- also the reason there is exactly one x:Name="ReattachButton" in the view.
public sealed class TerminalDetachedOrFailedVisibleConverter : IValueConverter {
    public static readonly TerminalDetachedOrFailedVisibleConverter Instance = new();

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is TerminalSessionState { Phase: TerminalSessionPhase.Detached or TerminalSessionPhase.Failed };

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

/// The combined banner's message: the failure detail when Failed, a fixed line for a plain
/// (non-error) Detached.
public sealed class TerminalDetachedOrFailedMessageConverter : IValueConverter {
    public static readonly TerminalDetachedOrFailedMessageConverter Instance = new();

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is TerminalSessionState state
            ? state.Phase == TerminalSessionPhase.Failed ? state.Detail ?? "The terminal failed." : "Detached from the terminal."
            : "";

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
