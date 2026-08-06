using Capacitor.App.ViewModels;
using ReactiveUI.Avalonia;

namespace Capacitor.App.Views;

// ReactiveWindow<T> ties ViewModel.Activator to THIS window's own Loaded/Unloaded lifecycle
// (AvaloniaActivationForViewFetcher) — no manual Activator.Activate() call is needed; Show()
// activates the VM's WhenActivated projections, Close() deactivates them.
public partial class MainWindow : ReactiveWindow<MainWindowViewModel> {
    /// Assigned by MainWindowCoordinator on every window it builds (spec §9): returns true when
    /// the close must be intercepted — the coordinator hides the window and the close below is
    /// cancelled. Left null on a plainly-constructed window (tests), where a close is a real
    /// close.
    public Func<bool>? CloseInterceptor { get; set; }

    public MainWindow() {
        InitializeComponent();
        Closing += (_, e) => {
            if (CloseInterceptor?.Invoke() == true) e.Cancel = true;
        };
    }
}
