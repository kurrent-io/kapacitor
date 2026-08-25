using Avalonia.Controls;
using Avalonia.Input;

namespace Capacitor.App.Views;

/// The Sessions surface's rail. DataContext is the window's own MainWindowViewModel, inherited —
/// this view never builds or assigns one, same contract as HomeView.
public partial class SessionRailView : UserControl {
    public SessionRailView() => InitializeComponent();

    // The window extends its client area into the title bar, so the rail's 48px chrome row IS the
    // title bar on this surface — empty space there must still move the window. Buttons in the row
    // mark their presses handled, so this only fires on the blank stretch.
    void OnChromePointerPressed(object? sender, PointerPressedEventArgs e) {
        if (e.GetCurrentPoint(this).Properties.IsLeftButtonPressed && TopLevel.GetTopLevel(this) is Window window)
            window.BeginMoveDrag(e);
    }
}
