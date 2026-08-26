using Avalonia.Controls;
using Avalonia.Input;

namespace Capacitor.App.Views;

/// The Sessions surface's rail. DataContext is the window's own MainWindowViewModel, inherited —
/// this view never builds or assigns one, same contract as HomeView.
public partial class SessionRailView : UserControl {
    public SessionRailView() => InitializeComponent();

    // The rail's 44px chrome row IS the title bar on this surface — see WindowChrome.
    void OnChromePointerPressed(object? sender, PointerPressedEventArgs e) => WindowChrome.BeginDrag(this, e);
}
