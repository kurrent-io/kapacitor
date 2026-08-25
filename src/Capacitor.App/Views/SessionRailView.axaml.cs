using Avalonia.Controls;

namespace Capacitor.App.Views;

/// The Sessions surface's rail. DataContext is the window's own MainWindowViewModel, inherited —
/// this view never builds or assigns one, same contract as HomeView.
public partial class SessionRailView : UserControl {
    public SessionRailView() => InitializeComponent();
}
