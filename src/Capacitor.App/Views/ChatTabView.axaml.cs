using System.Windows.Input;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Capacitor.App.ViewModels;

namespace Capacitor.App.Views;

/// Follow-tail sticks to the bottom: on every extent or viewport change, a reader who was at the
/// bottom before the change lands at the new bottom. The virtualizing panel reports the extent
/// as an estimate that a tall row corrects only once it is realized, so following re-evaluates
/// on each change rather than scrolling once per append. A reader scrolling up moves the offset
/// down in the same change or an earlier one — either way the change is not followed, and only
/// the panel's own adjustments move the offset up while growing the extent.
public partial class ChatTabView : UserControl {
    const double BottomTolerance = 2;

    ScrollViewer? _scroll;
    /// Armed by a click on a group's summary line and released once the dispatcher queue drains past layout, so neither the
    /// expansion's own extent change nor an append already queued behind the click scrolls the clicked line out of view.
    bool _holdTail;

    public ChatTabView() {
        InitializeComponent();
        // Tunnel, not bubble: TextBox's own class handler runs first on the bubbling route, where
        // it inserts the newline and marks Enter handled before any instance handler sees it.
        ComposerInput.AddHandler(KeyDownEvent, OnComposerKeyDown, RoutingStrategies.Tunnel);
        ChatItems.AddHandler(Button.ClickEvent, OnItemButtonClick);
        // The ScrollViewer is the list template's; it exists only once the list is first measured,
        // which for a surface built before its first layout is later than the first rows.
        ChatItems.TemplateApplied += (_, _) => {
            if (_scroll is not null) _scroll.ScrollChanged -= OnScrollChanged;
            _scroll = ChatItems.GetVisualDescendants().OfType<ScrollViewer>().FirstOrDefault();
            if (_scroll is not null) _scroll.ScrollChanged += OnScrollChanged;
        };
    }

    void OnItemButtonClick(object? sender, RoutedEventArgs e) {
        if (e.Source is not Button button || !button.Classes.Contains("toolSummary")) return;
        var wasHeld = _holdTail;
        _holdTail = true;
        if (!wasHeld) Dispatcher.UIThread.Post(() => _holdTail = false, DispatcherPriority.Background);
    }

    void OnScrollChanged(object? sender, ScrollChangedEventArgs e) {
        if (sender is not ScrollViewer scroll || (e.ExtentDelta.Y == 0 && e.ViewportDelta.Y == 0)) return;
        if (_holdTail) return;
        var offsetBefore   = scroll.Offset.Y - e.OffsetDelta.Y;
        var viewportBefore = scroll.Viewport.Height - e.ViewportDelta.Y;
        var extentBefore   = scroll.Extent.Height - e.ExtentDelta.Y;
        var stayed = e.OffsetDelta.Y >= 0;
        if (stayed && offsetBefore + viewportBefore >= extentBefore - BottomTolerance) scroll.ScrollToEnd();
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
