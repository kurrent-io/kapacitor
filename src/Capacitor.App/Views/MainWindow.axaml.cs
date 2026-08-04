using Capacitor.App.ViewModels;
using ReactiveUI.Avalonia;

namespace Capacitor.App.Views;

// ReactiveWindow<T> ties ViewModel.Activator to THIS window's own Loaded/Unloaded lifecycle
// (AvaloniaActivationForViewFetcher) — no manual Activator.Activate() call is needed; Show()
// activates the VM's WhenActivated projections, Close() deactivates them.
public partial class MainWindow : ReactiveWindow<MainWindowViewModel> {
    public MainWindow() => InitializeComponent();
}
