using System.Windows.Input;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Capacitor.App.ViewModels;

namespace Capacitor.App.Views;

/// Follow-tail sticks to the bottom until the reader leaves it: a scroll change that lands above
/// the bottom is followed unless a gesture of the reader's own — pointer, wheel, key or scrollbar
/// inside the list — landed in the same layout pass, and any change that lands on the bottom
/// re-arms it.
/// The decision cannot be read from the change's deltas: the virtualizing panel reports the extent
/// as an estimate, and a realized row changing height makes it drop its anchor and re-place every
/// row from the average size, after which the presenter clamps or anchor-shifts the offset by
/// arbitrary amounts that look exactly like a reader scrolling up.
public partial class ChatTabView : UserControl {
    const double BottomTolerance = 2;

    ScrollViewer? _scroll;
    bool _followTail = true;
    /// Armed by a gesture inside the list and released once the dispatcher queue drains past
    /// layout, so it covers exactly the scroll changes that gesture produced — an expansion click
    /// or a wheel notch — and not the appends that land later.
    bool _readerGesture;

    public ChatTabView() {
        InitializeComponent();
        // Tunnel, not bubble: TextBox's own class handler runs first on the bubbling route, where
        // it inserts the newline and marks Enter handled before any instance handler sees it.
        ComposerInput.AddHandler(KeyDownEvent, OnComposerKeyDown, RoutingStrategies.Tunnel);
        // Press and release both: a click's command runs on the release, in a later dispatcher turn
        // than the press, by which time a flag armed on the press alone has been released.
        ChatItems.AddHandler(PointerPressedEvent, OnReaderGesture, RoutingStrategies.Tunnel, handledEventsToo: true);
        ChatItems.AddHandler(PointerReleasedEvent, OnReaderGesture, RoutingStrategies.Tunnel, handledEventsToo: true);
        ChatItems.AddHandler(PointerWheelChangedEvent, OnReaderGesture, RoutingStrategies.Tunnel, handledEventsToo: true);
        ChatItems.AddHandler(ScrollGestureEvent, OnReaderGesture, RoutingStrategies.Tunnel, handledEventsToo: true);
        ChatItems.AddHandler(KeyDownEvent, OnReaderGesture, RoutingStrategies.Tunnel, handledEventsToo: true);
        ChatItems.AddHandler(Thumb.DragDeltaEvent, OnReaderGesture, RoutingStrategies.Bubble, handledEventsToo: true);
        // The ScrollViewer is the list template's; it exists only once the list is first measured,
        // which for a surface built before its first layout is later than the first rows.
        ChatItems.TemplateApplied += (_, _) => {
            if (_scroll is not null) _scroll.ScrollChanged -= OnScrollChanged;
            _scroll = ChatItems.GetVisualDescendants().OfType<ScrollViewer>().FirstOrDefault();
            if (_scroll is not null) _scroll.ScrollChanged += OnScrollChanged;
        };
    }

    void OnReaderGesture(object? sender, RoutedEventArgs e) {
        if (_readerGesture) return;
        _readerGesture = true;
        Dispatcher.UIThread.Post(() => _readerGesture = false, DispatcherPriority.Background);
    }

    void OnScrollChanged(object? sender, ScrollChangedEventArgs e) {
        if (sender is not ScrollViewer scroll) return;
        var atBottom = scroll.Offset.Y + scroll.Viewport.Height >= scroll.Extent.Height - BottomTolerance;
        _followTail = _readerGesture ? atBottom : _followTail || atBottom;
        if (_followTail && !atBottom) scroll.ScrollToEnd();
    }

    /// A bare Enter is always consumed — it sends when the composer can send, and otherwise does
    /// nothing, leaving the text and the hint that says why. Shift+Enter falls through to the
    /// TextBox's own newline.
    void OnComposerKeyDown(object? sender, KeyEventArgs e) {
        if (e.Key != Key.Enter || e.KeyModifiers.HasFlag(KeyModifiers.Shift)) return;
        e.Handled = true;
        if (DataContext is ChatTabViewModel vm && ((ICommand)vm.SendCommand).CanExecute(null))
            vm.SendCommand.Execute().Subscribe();
    }

    public void FocusComposer() => ComposerInput.Focus();
}
