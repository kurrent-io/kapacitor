using System.Reactive.Disposables;
using System.Reactive.Disposables.Fluent;
using System.Reactive.Linq;
using Avalonia.Controls.Notifications;
using Capacitor.App.Services;
using Capacitor.App.ViewModels;
using ReactiveUI;
using ReactiveUI.Avalonia;

namespace Capacitor.App.Views;

// ReactiveWindow<T> ties ViewModel.Activator to THIS window's Loaded/Unloaded lifecycle, so
// Show() starts the ViewModel's queue/ticker projections and Close() stops them — which is also
// why a deferred (closed) prompt costs nothing until the coordinator builds the next one.
public partial class ConsentPromptWindow : ReactiveWindow<ConsentPromptViewModel> {
    WindowNotificationManager? _notifications;
    IDisposable? _notifierSubscription;
    IAppNotifier? _notifier;

    /// Assigned by App's window factory (spec §6): prompt warnings must surface on THIS window —
    /// the main window may be closed. Same shape as MainWindow.Notifier, with one difference that
    /// matters here: a prompt window is transient (a fresh instance per raise), so the
    /// app-lifetime notifier subscription is dropped on close instead of living forever.
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

    public ConsentPromptWindow() {
        InitializeComponent();

        // Built on Loaded, not in the constructor: WindowNotificationManager self-installs into
        // the TopLevel's AdornerLayer once the template is applied (MainWindow's same note).
        Loaded += (_, _) => _notifications ??= new WindowNotificationManager(this) {
            Position = NotificationPosition.TopRight,
        };
        Closed += (_, _) => _notifierSubscription?.Dispose();

        // The queue emptying is the ONE close this window performs on its own; a user close is an
        // explicit defer and leaves the queue untouched (spec §6).
        this.WhenActivated(disposables => {
            ViewModel?.CloseRequested.Subscribe(_ => Close()).DisposeWith(disposables);
        });
    }

    void ShowToast(string message) =>
        _notifications?.Show(new Notification("Kurrent Capacitor", message, NotificationType.Warning, TimeSpan.FromSeconds(4)));
}
