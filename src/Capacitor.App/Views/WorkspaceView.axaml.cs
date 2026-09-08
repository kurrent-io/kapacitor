using System.Globalization;
using System.Reactive.Linq;
using Avalonia.Controls;
using Avalonia.Data.Converters;
using Avalonia.Threading;
using Capacitor.App.Services;
using Capacitor.App.ViewModels;
using ReactiveUI;

namespace Capacitor.App.Views;

/// Receives a workspace model from navigation and retains each tab's native control.
/// TerminalControl owns terminal resizing, including font metrics and reattachment.
public partial class WorkspaceView : UserControl {
    IDisposable? _tabFocus;

    // The workspace header doubles as the draggable chrome on its side of the split — see
    // WindowChrome.
    void OnHeaderPointerPressed(object? sender, Avalonia.Input.PointerPressedEventArgs e) =>
        WindowChrome.BeginDrag(this, e);

    public WorkspaceView() {
        InitializeComponent();
        // The control draws its caret and takes keystrokes only while focused; a Model assignment
        // is the "terminal became live" moment — but only the Terminal tab may take focus, or a
        // reattach under the Chat tab would steal it from the composer.
        TerminalHost.PropertyChanged += (_, e) => {
            if (e.Property == SvcSystems.UI.Terminal.TerminalControl.ModelProperty && TerminalHost.Model is not null
                && DataContext is WorkspaceViewModel { IsTerminalActive: true })
                TerminalHost.Focus();
        };
        // The PR reader is created on first use, including for sessions without a PTY.
        DataContextChanged += (_, _) => {
            _tabFocus?.Dispose();
            PullRequestHost.Content = null;
            var model = DataContext as WorkspaceViewModel;
            _tabFocus = model?
                .WhenAnyValue(vm => vm.ActiveTab, vm => vm.Chat)
                .Subscribe(pair => Dispatcher.UIThread.Post(() => {
                    if (!ReferenceEquals(model, DataContext) || model?.ActiveTab != pair.Item1) return;
                    if (pair.Item1 == WorkspaceTab.PullRequest && PullRequestHost.Content is null && model?.PullRequests is { } pullRequests)
                        PullRequestHost.Content = new PullRequestReader { DataContext = pullRequests };
                    if (PullRequestHost.Content is PullRequestReader reader) reader.IsVisible = pair.Item1 == WorkspaceTab.PullRequest;
                    if (pair.Item1 == WorkspaceTab.Chat && pair.Item2 is not null) ChatHost.FocusComposer();
                    else if (pair.Item1 == WorkspaceTab.Terminal) TerminalHost.Focus();
                }, DispatcherPriority.Loaded));
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
/// where the banner explains otherwise-dead keystrokes. A read-write attach shows none: the
/// banner would overlay the terminal it sits on.
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
