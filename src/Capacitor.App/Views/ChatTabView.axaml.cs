using System.Collections.Specialized;
using System.Windows.Input;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using Capacitor.App.ViewModels;

namespace Capacitor.App.Views;

/// Follow-tail lives here: "was at end" is decided at the collection change from the OLD extent
/// (the new rows are not measured yet), and the scroll is applied after the layout pass that
/// establishes the new extent — only if the reader has not moved. One one-shot at a time: a
/// layout pass that resolves the ScrollViewer retires it, so a stretch without one — the surface
/// collapsed behind the other tab — would otherwise accumulate a handler per append, at the
/// transcript's poll rate.
public partial class ChatTabView : UserControl {
    INotifyCollectionChanged? _observed;
    bool _followPending;

    public ChatTabView() {
        InitializeComponent();
        // Tunnel, not bubble: TextBox's own class handler runs first on the bubbling route, where
        // it inserts the newline and marks Enter handled before any instance handler sees it.
        ComposerInput.AddHandler(KeyDownEvent, OnComposerKeyDown, RoutingStrategies.Tunnel);
        DataContextChanged += (_, _) => Observe((DataContext as ChatTabViewModel)?.Items as INotifyCollectionChanged);
    }

    void Observe(INotifyCollectionChanged? items) {
        if (_observed is not null) _observed.CollectionChanged -= OnItemsChanged;
        _observed = items;
        if (_observed is not null) _observed.CollectionChanged += OnItemsChanged;
    }

    ScrollViewer? Scroll => ChatItems.GetVisualDescendants().OfType<ScrollViewer>().FirstOrDefault();

    void OnItemsChanged(object? sender, NotifyCollectionChangedEventArgs e) {
        if (e.Action != NotifyCollectionChangedAction.Add || _followPending) return;
        if (Scroll is not { } scroll) {
            FollowOnFirstLayout();
            return;
        }
        var wasAtEnd = scroll.Offset.Y + scroll.Viewport.Height >= scroll.Extent.Height - 1;
        if (!wasAtEnd) return;

        _followPending = true;
        var captured = scroll.Offset;
        void OnLayoutUpdated(object? _, EventArgs __) {
            scroll.LayoutUpdated -= OnLayoutUpdated;
            _followPending = false;
            if (scroll.Offset == captured) scroll.ScrollToEnd();
        }
        scroll.LayoutUpdated += OnLayoutUpdated;
    }

    /// Rows can land before the view has ever been laid out — the tab is built and its first read
    /// started before the workspace view exists — leaving no extent to decide "was at end" from.
    /// This one-shot waits on the view's own layout and retires only once a ScrollViewer exists,
    /// so a pass that builds none (the surface still collapsed) leaves it armed.
    void FollowOnFirstLayout() {
        _followPending = true;
        void OnLayoutUpdated(object? _, EventArgs __) {
            if (Scroll is not { } scroll) return;
            LayoutUpdated -= OnLayoutUpdated;
            _followPending = false;
            if (scroll.Offset.Y == 0) scroll.ScrollToEnd();
        }
        LayoutUpdated += OnLayoutUpdated;
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
