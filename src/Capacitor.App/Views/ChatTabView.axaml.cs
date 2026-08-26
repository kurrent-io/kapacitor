using System.Collections.Specialized;
using System.Windows.Input;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.VisualTree;
using Capacitor.App.ViewModels;

namespace Capacitor.App.Views;

/// Follow-tail lives here, stateless across events: "was at end" is decided at the collection
/// change from the OLD extent (the new rows are not measured yet), and the scroll is applied
/// after the layout pass that establishes the new extent — only if the reader has not moved.
public partial class ChatTabView : UserControl {
    INotifyCollectionChanged? _observed;

    public ChatTabView() {
        InitializeComponent();
        DataContextChanged += (_, _) => Observe((DataContext as ChatTabViewModel)?.Items as INotifyCollectionChanged);
    }

    void Observe(INotifyCollectionChanged? items) {
        if (_observed is not null) _observed.CollectionChanged -= OnItemsChanged;
        _observed = items;
        if (_observed is not null) _observed.CollectionChanged += OnItemsChanged;
    }

    ScrollViewer? Scroll => ChatItems.GetVisualDescendants().OfType<ScrollViewer>().FirstOrDefault();

    void OnItemsChanged(object? sender, NotifyCollectionChangedEventArgs e) {
        if (e.Action != NotifyCollectionChangedAction.Add || Scroll is not { } scroll) return;
        var wasAtEnd = scroll.Offset.Y + scroll.Viewport.Height >= scroll.Extent.Height - 1;
        if (!wasAtEnd) return;

        var captured = scroll.Offset;
        void OnLayoutUpdated(object? _, EventArgs __) {
            scroll.LayoutUpdated -= OnLayoutUpdated;
            if (scroll.Offset == captured) scroll.ScrollToEnd();
        }
        scroll.LayoutUpdated += OnLayoutUpdated;
    }

    // Enter sends, Shift+Enter is the TextBox's own newline.
    void OnComposerKeyDown(object? sender, KeyEventArgs e) {
        if (e.Key != Key.Enter || e.KeyModifiers.HasFlag(KeyModifiers.Shift)) return;
        e.Handled = true;
        if (DataContext is ChatTabViewModel vm && ((ICommand)vm.SendCommand).CanExecute(null))
            vm.SendCommand.Execute().Subscribe();
    }

    public void FocusComposer() => ComposerInput.Focus();
}
