using System.Reactive.Disposables.Fluent;
using Capacitor.App.ViewModels;
using ReactiveUI;
using ReactiveUI.Avalonia;

namespace Capacitor.App.Views;

// ReactiveWindow<T> ties ViewModel.Activator to THIS window's Loaded/Unloaded lifecycle
// (ConsentPromptWindow's same note) — irrelevant to LifecyclePromptViewModel itself (it has no
// activation-scoped state, unlike the queue/ticker-driven ConsentPromptViewModel), but the
// CloseRequested subscription below still rides the window's own WhenActivated so it is torn down
// with the window rather than leaking past Close().
public partial class LifecyclePromptWindow : ReactiveWindow<LifecyclePromptViewModel> {
    public LifecyclePromptWindow() {
        InitializeComponent();

        this.WhenActivated(disposables => {
            ViewModel?.CloseRequested.Subscribe(_ => Close()).DisposeWith(disposables);
        });
    }
}
