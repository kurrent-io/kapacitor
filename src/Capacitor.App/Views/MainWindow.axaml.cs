using System.Reactive.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Notifications;
using Capacitor.App.Services;
using Capacitor.App.ViewModels;
using ReactiveUI;
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

    WindowNotificationManager? _notifications;
    IDisposable? _notifierSubscription;
    IAppNotifier? _notifier;

    // Defaults to false — the Home TabItem is selected first (MainWindow.axaml), so Activity
    // starts unselected regardless of the window's own visibility.
    bool _activityTabSelected;

    /// Assigned by App.BuildAndShowMainWindow (spec §11) — the SAME IAppNotifier instance
    /// AgentActionService pushes into, so the toast overlay and stderr mirroring are always in
    /// sync. Replaces the inline Banner/BannerLifetime this window used to bind: AppNotifier
    /// itself and its stderr mirroring are unchanged, only the presentation moved from a
    /// layout-shifting Border to a WindowNotificationManager overlay. Left null on a
    /// plainly-constructed window (tests that don't exercise toasts) — the setter tolerates that.
    public IAppNotifier? Notifier {
        get => _notifier;
        set {
            _notifier = value;
            _notifierSubscription?.Dispose();
            _notifierSubscription = value?.Messages
                .ObserveOn(RxSchedulers.MainThreadScheduler)
                .Subscribe(ShowToast);
        }
    }

    public MainWindow() {
        InitializeComponent();
        Closing += (_, e) => {
            if (CloseInterceptor?.Invoke() == true) e.Cancel = true;
        };

        // Built on Loaded (window open), not here in the constructor: WindowNotificationManager
        // self-installs into the TopLevel's AdornerLayer once its template is applied, and Loaded
        // is when that is guaranteed. A hide-to-tray/reopen cycle (MainWindowCoordinator.Hide())
        // only toggles OS-level visibility — it never detaches this window from the visual tree —
        // so Loaded fires once per window instance and this null-guard is defensive only.
        Loaded += (_, _) => _notifications ??= new WindowNotificationManager(this) {
            Position = NotificationPosition.TopRight,
        };
    }

    // A toast fired before Loaded, or while the window is hidden (Hide() suspends rendering
    // entirely), is invisible to the user — stderr (AppNotifier's own mirroring, unchanged) is
    // the only channel that survives either case. Accepted limitation, unchanged from the inline
    // banner it replaces (spec §11).
    void ShowToast(string message) =>
        _notifications?.Show(new Notification("Kurrent Capacitor", message, NotificationType.Warning, TimeSpan.FromSeconds(4)));

    // Wired from MainWindow.axaml's TabControl (spec §7): the Activity tab's refresh cadence
    // needs to know it is ACTUALLY on screen, which is the AND of tab selection and the window's
    // own visibility below — a background window with Activity selected must not poll.
    void OnTabSelectionChanged(object? sender, SelectionChangedEventArgs e) {
        _activityTabSelected = e.AddedItems.Count > 0 && ReferenceEquals(e.AddedItems[0], ActivityTabItem);
        UpdateActivityVisibility();
    }

    // IsVisible is decompile-verified to be exactly what Show()/Hide() toggle (see
    // App.ShowConfirmForceStopDialogAsync's owner check) — hide-to-tray never fires Closed/Opened
    // (MainWindowCoordinator's own doc comment: it "never detaches this window from the visual
    // tree"), so this property is the one signal that actually tracks on-screen state across a
    // hide/reopen cycle.
    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change) {
        base.OnPropertyChanged(change);
        // DataContextProperty too — defensive: production always assigns DataContext before the
        // first Show(), but this keeps the check correct even if a caller (a test) does it the
        // other way around.
        if (change.Property == IsVisibleProperty || change.Property == DataContextProperty) UpdateActivityVisibility();
    }

    void UpdateActivityVisibility() {
        if (DataContext is MainWindowViewModel vm) vm.Activity.OnTabVisibleChanged(_activityTabSelected && IsVisible);
    }
}
